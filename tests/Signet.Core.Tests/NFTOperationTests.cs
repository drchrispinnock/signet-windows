using System.Text;
using System.Text.Json;
using Signet.Models;
using Signet.Services;
using Signet.ViewModels;

namespace Signet.Tests;

/// <summary>
/// NFT Transfer and Burn (0.9.13): the request the bridge is given, the batch limits it must
/// send with, the metadata that turns the menu off, and the model's bound estimate and send.
/// </summary>
public class NFTOperationTests
{
    private const string A = "KT1RJ6PbjHpwc3M5rw5s2Nbmefwbuwbdxton";
    private const string B = "KT1KEa8z6vWXDJrVqtMrAeDVzsvxat3kHaCE";
    private static readonly Address Them = new("tz1dCaMnnMJk76UodjewCK67ABiXWjcKj73N");

    [Fact]
    public void RequestGroupsByContractAndIsCheckedBeforeTheBridge()
    {
        var request = new TokenTransferRequest(Them, [new(A, "7", "1"), new(B, "12", "3"), new(A, "9", "1")]);
        Assert.True(request.IsValid && !request.IsBurn && request.Count == 3 && request.Contracts.SequenceEqual([A, B]));
        var rows = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(request.TransfersJSON)!;
        Assert.Equal(3, rows.Count);
        Assert.Equal(new Dictionary<string, string> { ["contract"] = A, ["tokenId"] = "7", ["amount"] = "1" }, rows[0]);
        Assert.Equal(new Dictionary<string, string> { ["contract"] = B, ["tokenId"] = "12", ["amount"] = "3" }, rows[1]);
        Assert.Equal(new Dictionary<string, string> { ["contract"] = A, ["tokenId"] = "9", ["amount"] = "1" }, rows[2]);
        Assert.True(new TokenTransferRequest(TokenTransferRequest.BurnAddress, request.Transfers).IsBurn);
        Assert.True(TokenTransferRequest.BurnAddress.IsValidAccount);
        // Nothing, too much, a bad recipient, contract, id or amount, or a zero amount: not sent.
        Assert.False(new TokenTransferRequest(Them, []).IsValid);
        Assert.False(new TokenTransferRequest(Them, Enumerable.Repeat(request.Transfers[0], TokenTransferRequest.MaxTokens + 1).ToList()).IsValid);
        Assert.False(new TokenTransferRequest(new Address("tz1nope"), request.Transfers).IsValid);
        foreach (var bad in new TokenTransfer[] { new(Them.Value, "1", "1"), new(A, "01", "1"), new(A, "-1", "1"), new(A, "1", "0"), new(A, "1", "1.5"), new(A, "", "1") })
            Assert.False(bad.IsValid, bad.ToString());
        var nft = new NFT($"{A}:5", A, "5", "x", 4, thumbnailURL: null);
        Assert.Equal(new TokenTransfer(A, "5", "2"), new TokenTransfer(nft, 2));
    }

    [Fact]
    public void BatchLimitsRoundTripThroughTheBridgeShape()
    {
        var json = """{"operations":[{"feeMutez":"500","gasLimit":1500,"storageLimit":0},{"feeMutez":"700","gasLimit":2500,"storageLimit":100}],"revealFeeMutez":"374","revealGasLimit":200,"revealStorageLimit":0}""";
        var limits = BatchLimits.From(JSONValue.Parse(json))!;
        Assert.True(limits.Operations.Count == 2 && limits.Operations[1] == new BatchLimits.Operation("700", 2500, 100));
        Assert.True(limits.RevealFeeMutez == "374" && limits.RevealGasLimit == 200);
        Assert.Equal(json, limits.Json);
        Assert.Equal(limits, BatchLimits.From(JSONValue.Parse(limits.Json)));
        // Missing or malformed figures are refused, never defaulted (S20).
        foreach (var broken in new[]
        {
            """{"operations":[]}""", """{"operations":[{"feeMutez":"5e2","gasLimit":1,"storageLimit":0}]}""",
            """{"operations":[{"feeMutez":"500","gasLimit":1.5,"storageLimit":0}]}""", """{"operations":[{"feeMutez":"500","gasLimit":1}]}""",
            """{"operations":[{"feeMutez":"500","gasLimit":1,"storageLimit":0}],"revealFeeMutez":"x"}""",
        })
            Assert.Null(BatchLimits.From(JSONValue.Parse(broken)));
    }

    [Fact]
    public void SoulboundTokensAndArtifactsAreReadFromTheMetadata()
    {
        var fixture = Encoding.UTF8.GetBytes($$"""
        [
          { "balance": "1", "token": { "contract": { "address": "{{A}}" }, "tokenId": "1", "standard": "fa2",
            "metadata": { "name": "Badge", "decimals": "0", "isTransferable": false, "displayUri": "ipfs://QmXxvrULyrWH18e6kUinPNXt5PAwv6h7MPcmWbSw4DwDuP" } } },
          { "balance": "1", "token": { "contract": { "address": "{{A}}" }, "tokenId": "2", "standard": "fa2",
            "metadata": { "name": "Worded", "decimals": "0", "isTransferable": "False", "displayUri": "ipfs://QmXxvrULyrWH18e6kUinPNXt5PAwv6h7MPcmWbSw4DwDuP" } } },
          { "balance": "1", "token": { "contract": { "address": "{{A}}" }, "tokenId": "3", "standard": "fa2",
            "metadata": { "name": "Art", "decimals": "0", "isTransferable": "maybe", "displayUri": "ipfs://QmXxvrULyrWH18e6kUinPNXt5PAwv6h7MPcmWbSw4DwDuP",
                          "artifactUri": "ipfs://Qmaf8m2vtegsvoWybB2AL1wJ4Q79SwornWx8T7DbkYRhv9", "formats": [ { "uri": "ipfs://Qmaf8m2vtegsvoWybB2AL1wJ4Q79SwornWx8T7DbkYRhv9", "mimeType": "video/mp4" } ] } } }
        ]
        """);
        var nfts = TzKTService.Nfts(TzKTService.Decode(fixture));
        Assert.Equal([false, false, true], nfts.Select(n => n.IsTransferable));
        Assert.Empty(nfts[0].ArtifactURLs);
        Assert.True(nfts[2].ArtifactURLs.SequenceEqual(IPFS.CandidateURLs("ipfs://Qmaf8m2vtegsvoWybB2AL1wJ4Q79SwornWx8T7DbkYRhv9")) && nfts[2].IsMoving);
        var ops = new NFTOperation(NFTOperation.OperationKind.Burn, nfts.Take(2).ToList());
        Assert.True(ops.Layer == 1 && ops.Id == $"burn:{A}:1,{A}:2");
        Assert.Equal(2, new NFTOperation(NFTOperation.OperationKind.Transfer, [nfts[2].OnLayer2()]).Layer);
    }

    [Fact]
    public void TokenLinksFollowTheNetwork()
    {
        Assert.Equal($"https://tzkt.io/{A}/tokens/7", Network.Mainnet.ExplorerURLForToken(A, "7")?.AbsoluteString());
        Assert.Equal($"https://objkt.com/tokens/{A}/7", Network.Mainnet.ObjktURL(A, "7")?.AbsoluteString());
        Assert.Null(Network.Shadownet.ObjktURL(A, "7"));
        Assert.Null(Network.Mainnet.L2On().Layer2?.ObjktURL(A, "7"));
        Assert.Contains("etherlink", Network.Mainnet.L2On().Layer2?.ExplorerURLForToken(A, "7")?.Host);
    }

    [Fact]
    public async Task TheModelEstimatesAndSendsOnlyInTheSameContext()
    {
        var store = new InMemoryWalletStore();
        var wallet = new Wallet("Main", new Address("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb"), keyKind: KeyKind.Unencrypted);
        store.AddWithSecretKey(wallet, "edskX");
        var model = new WalletViewModel(chain: new MockChainService(), walletStore: store, stateStore: new InMemoryAppStateStore());
        Assert.Equal(wallet.Address, model.SelectedWallet?.Address);
        Assert.True(model.CanTransferTokens(1) && !model.CanTransferTokens(2));   // Layer 2 is off by default
        var request = new TokenTransferRequest(Them, [new(A, "7", "1"), new(B, "1", "2")]);
        var approved = await model.EstimateTokenTransfer(request, 1);
        Assert.True(approved.Estimate.Limits.Operations.Count == 2 && approved.Estimate.Total == approved.Estimate.Fee + approved.Estimate.Burn);
        // Not estimated, another request, or another layer: refused before any key is touched.
        await Assert.ThrowsAsync<WalletViewModel.WalletError.NotEstimated>(() => model.TransferTokens(request, 1, null, null));
        var other = new TokenTransferRequest(TokenTransferRequest.BurnAddress, request.Transfers);
        await Assert.ThrowsAsync<WalletViewModel.WalletError.ContextChanged>(() => model.TransferTokens(other, 1, approved, null));
        await Assert.ThrowsAsync<WalletViewModel.WalletError.ContextChanged>(() => model.TransferTokens(request, 2, approved, null));
        var (hash, level) = await model.TransferTokens(request, 1, approved, null);
        Assert.True(hash == "ooMockTokens2" && level >= 1);
        // A bad request never reaches the chain service.
        await Assert.ThrowsAsync<ChainError.InvalidTokenTransfer>(() => model.EstimateTokenTransfer(new TokenTransferRequest(Them, []), 1));
    }
}
