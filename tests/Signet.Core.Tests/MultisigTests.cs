using System.Text.Json;
using Signet.Models;
using Signet.Services;
using Signet.ViewModels;

namespace Signet.Tests;

/// <summary>
/// The embedded generic multisig script is byte-for-byte octez-client's: its hash is the one
/// octez-client recognises (<c>octez-client hash script</c>), so contracts deployed by either tool
/// are multisigs to the other.
/// </summary>
public class MultisigScriptTests
{
    [Fact]
    public async Task EmbeddedScriptHashesLikeOctezClient()
    {
        var r = await TaquitoBridge.Shared.Call("multisigScriptHash");
        Assert.Equal("exprub9UzpxmhedNQnsv1J1DazWGJnj1dLhtG1fxkUoWSdFLBGLqJ4", r["hash"]?.StringValue);
    }

    /// <summary>
    /// The bytes we ask signers to sign are the ones <c>octez-client prepare multisig transaction on
    /// KT1UqzPQCEb6bVYKeab9EXjVcQsUGT8iLC2m … --bytes-only</c> printed on Shadownet (chain
    /// NetXsqzbfFenSTS) at counter 0, for a transfer, a new delegate and a withdrawn delegate.
    /// </summary>
    [Theory]
    [InlineData("""{"kind":"transfer","amountMutez":"1000000","destination":"tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb"}""",
        "05070707070a00000004d052218e0a0000001601de437531484fc04f7bebc2f6a90580b8e0b5de8e0007070000050502000000340320053d036d0743035d0a00000015006b82198cb179e8306c1bedd08f12dc863f328886031e0743036a0080897a034f034d031b")]
    [InlineData("""{"kind":"delegate","delegate":"tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb"}""",
        "05070707070a00000004d052218e0a0000001601de437531484fc04f7bebc2f6a90580b8e0b5de8e00070700000505020000002a0320053d036d0743035d0a00000015006b82198cb179e8306c1bedd08f12dc863f3288860346034e031b")]
    [InlineData("""{"kind":"delegate","delegate":null}""",
        "05070707070a00000004d052218e0a0000001601de437531484fc04f7bebc2f6a90580b8e0b5de8e00070700000505020000000e0320053d036d053e035d034e031b")]
    public async Task SignedBytesMatchOctezClient(string action, string expected)
    {
        var r = await TaquitoBridge.Shared.Call("multisigPayloadLocal", "NetXsqzbfFenSTS", "KT1UqzPQCEb6bVYKeab9EXjVcQsUGT8iLC2m", 0, action);
        Assert.Equal(expected, r["bytes"]?.StringValue);
    }

    /// <summary>Windows-only: the C# action JSON is what <c>multisig.js</c> reads.</summary>
    [Fact]
    public void ActionsSerializeAsTheBridgeExpects()
    {
        Assert.Equal("""{"kind":"transfer","amountMutez":"1000000","destination":"tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb"}""",
            BridgeMultisigService.Json(new MultisigAction.Transfer("1000000", "tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb")));
        Assert.Equal("""{"kind":"delegate","delegate":null}""", BridgeMultisigService.Json(new MultisigAction.SetDelegate(null)));
    }
}

public class MultisigStorageTests
{
    [Fact]
    public void ContractsFileIsOctezClientFormat()
    {
        var dir = TestSupport.TempDir("multisig");
        var store = new TezosClientStore(dir);
        Assert.Empty(store.LoadContracts());
        store.AddContract(new MultisigContract("treasury", new Address("KT1BEqzn5Wx8uJrZNvuS9DVHmLvG9td3fDLi")));
        Assert.ThrowsAny<TezosClientStore.StoreError>(() => store.AddContract(new MultisigContract("treasury", new Address("KT1XXX"))));
        using var raw = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "contracts")));
        Assert.Equal("treasury", raw.RootElement[0].GetProperty("name").GetString());
        Assert.Equal("KT1BEqzn5Wx8uJrZNvuS9DVHmLvG9td3fDLi", raw.RootElement[0].GetProperty("value").GetString());
        Assert.Equal([new MultisigContract("treasury", new Address("KT1BEqzn5Wx8uJrZNvuS9DVHmLvG9td3fDLi"))], store.LoadContracts());
    }

    /// <summary>
    /// Named contracts are the KT1 half of the address book: <c>Load()</c> lists them as watch-only, a
    /// KT1 added as an address lands in <c>contracts</c> (octez-client refuses a <c>public_key_hashs</c>
    /// holding one), aliases are unique across both, and rename/forget work on them.
    /// </summary>
    [Fact]
    public void ContractsAreAddressBookEntries()
    {
        var dir = TestSupport.TempDir("multisig");
        var store = new TezosClientStore(dir);
        var alice = new Wallet("alice", new Address("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb"), publicKey: "edpkALICE", keyKind: KeyKind.Unencrypted);
        store.AddWithSecretKey(alice, "edskALICE");
        store.AddWatchOnly(new Wallet("treasury", new Address("KT1BEqzn5Wx8uJrZNvuS9DVHmLvG9td3fDLi")));
        store.AddContract(new MultisigContract("vault", new Address("KT1UqzPQCEb6bVYKeab9EXjVcQsUGT8iLC2m")));
        using (var hashes = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "public_key_hashs"))))
            Assert.Equal(1, hashes.RootElement.GetArrayLength());
        Assert.Equal(["treasury", "vault"], store.LoadContracts().Select(c => c.Alias));
        var loaded = store.Load();
        Assert.Equal(["alice", "treasury", "vault"], loaded.Select(w => w.Alias));
        Assert.True(loaded[1].KeyKind == KeyKind.None && loaded[1].Address.IsContract && loaded[1].PublicKey is null);

        Assert.ThrowsAny<TezosClientStore.StoreError>(() => store.AddContract(new MultisigContract("alice", new Address("KT1XXX"))));
        Assert.ThrowsAny<TezosClientStore.StoreError>(() => store.AddWatchOnly(new Wallet("vault", new Address("tz3WXYtyDUNL91qfiCJtVUX746QpNv5i5ve5"))));
        Assert.ThrowsAny<TezosClientStore.StoreError>(() => store.AddWithSecretKey(new Wallet("treasury", alice.Address, publicKey: "edpkX", keyKind: KeyKind.Unencrypted), "edskX"));
        Assert.ThrowsAny<TezosClientStore.StoreError>(() => store.Rename("alice", "vault"));

        store.Rename("treasury", "funds");
        Assert.Equal(["funds", "vault"], store.LoadContracts().Select(c => c.Alias));
        var funds = store.Load().First(w => w.Alias == "funds");
        store.Remove(funds);
        Assert.Equal(["alice", "vault"], store.Load().Select(w => w.Alias));
        Assert.ThrowsAny<TezosClientStore.StoreError>(() => store.Remove(funds));
    }

    [Fact]
    public void ProposalsRoundTripThroughTheFile()
    {
        var dir = TestSupport.TempDir("multisig");
        Directory.CreateDirectory(dir);
        var store = new FileMultisigProposalStore(dir);
        Assert.Empty(store.Load());
        var p = new MultisigProposal("treasury", "KT1BEqzn5Wx8uJrZNvuS9DVHmLvG9td3fDLi", "Shadownet", "NetXnHfVqm9iesp",
            4, 2, ["edpkA", "edpkB", "edpkC"], new MultisigAction.Transfer("5000000", "tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb"), "0507")
        {
            Signatures = [new MultisigSignature("edpkA", "edsigA")],
            Created = DateTimeOffset.FromUnixTimeSeconds(1_760_000_000),  // whole seconds survive ISO 8601
        };
        store.Update(list => { list.Clear(); list.Add(p); });
        var back = store.Load();
        Assert.Equal([p], back);
        Assert.Equal(5, back[0].Amount);
        Assert.True(back[0].Missing == 1 && !back[0].IsReady);
    }

    /// <summary>Windows-only: a proposals file the Mac wrote (Swift's JSONEncoder form) reads here, and ours reads there.</summary>
    [Fact]
    public void ProposalsFileMatchesTheMacFormat()
    {
        var dir = TestSupport.TempDir("multisig");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, FileMultisigProposalStore.FileName), """
        [
          {
            "action" : { "setDelegate" : {} },
            "bytes" : "0507",
            "chainID" : "NetXsqzbfFenSTS",
            "contractAddress" : "KT1BEqzn5Wx8uJrZNvuS9DVHmLvG9td3fDLi",
            "contractAlias" : "treasury",
            "counter" : 1,
            "created" : "2025-10-09T08:53:20Z",
            "id" : "E621E1F8-C36C-495A-93FC-0C247A3E6E5F",
            "keys" : [ "edpkA" ],
            "networkName" : "Shadownet",
            "signatures" : [ { "signature" : "edsigP" } ],
            "threshold" : 1
          }
        ]
        """);
        var store = new FileMultisigProposalStore(dir);
        var p = Assert.Single(store.Load());
        Assert.Equal(new MultisigAction.SetDelegate(null), p.Action);
        Assert.Equal(Guid.Parse("E621E1F8-C36C-495A-93FC-0C247A3E6E5F"), p.Id);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_760_000_000), p.Created);
        Assert.Null(Assert.Single(p.Signatures).PublicKey);
        store.Update(_ => { });
        var written = File.ReadAllText(Path.Combine(dir, FileMultisigProposalStore.FileName));
        Assert.Contains("\"id\": \"E621E1F8-C36C-495A-93FC-0C247A3E6E5F\"", written);
        Assert.Contains("\"created\": \"2025-10-09T08:53:20Z\"", written);
        Assert.DoesNotContain("\r", written);
        Assert.Equal([p], store.Load());
    }
}

public class MultisigFlowTests
{
    static readonly OperationLimits Limits = new("1000", 1000, 0);
    static readonly Wallet Alice = new("alice", new Address("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb"), publicKey: "edpkALICE", keyKind: KeyKind.Unencrypted);
    static readonly Wallet Bob = new("bob", new Address("tz2BFTyPeYRzxd5aiBchbXN3WCZhx7BqbMBq"), publicKey: "sppkBOB", keyKind: KeyKind.Encrypted);
    static readonly Wallet Watch = new("watch", new Address("tz3WXYtyDUNL91qfiCJtVUX746QpNv5i5ve5"));

    private static WalletViewModel MakeModel(MockMultisigService service, IReadOnlyList<MultisigProposal>? proposals = null)
    {
        var proposalStore = new InMemoryMultisigProposalStore();
        proposalStore.Update(list => list.AddRange(proposals ?? []));
        var store = new InMemoryWalletStore();
        var model = new WalletViewModel([Alice, Bob, Watch], new MockChainService(), walletStore: store, multisig: service, proposalStore: proposalStore);
        store.AddWithSecretKey(Alice, "edskALICE");
        store.AddWithSecretKey(Bob, "edeskBOB");
        store.AddWatchOnly(Watch);
        return model;
    }

    [Fact]
    public async Task AddRefusesForeignOrNonGenericContracts()
    {
        var service = new MockMultisigService(["edpkSOMEONE", "edpkELSE"], 1);
        var model = MakeModel(service);
        await Assert.ThrowsAnyAsync<WalletViewModel.MultisigError>(() => model.AddMultisig("x", "KT1BEqzn5Wx8uJrZNvuS9DVHmLvG9td3fDLi"));
        service.Keys = ["edpkALICE", "edpkELSE"];
        service.IsGeneric = false;
        await Assert.ThrowsAnyAsync<WalletViewModel.MultisigError>(() => model.AddMultisig("x", "KT1BEqzn5Wx8uJrZNvuS9DVHmLvG9td3fDLi"));
        await Assert.ThrowsAnyAsync<WalletViewModel.MultisigError>(() => model.AddMultisig("x", "tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb"));
        service.IsGeneric = true;
        var added = await model.AddMultisig(" treasury ", "KT1BEqzn5Wx8uJrZNvuS9DVHmLvG9td3fDLi");
        Assert.Equal("treasury", added.Alias);
        Assert.Equal([added], model.MultisigContracts);
        // It joins the address book under its name and is shown.
        Assert.Equal(["alice", "bob", "watch", "treasury"], model.Wallets.Select(w => w.Alias));
        Assert.True(model.SelectedWallet?.Alias == "treasury" && model.SelectedWallet?.KeyKind == KeyKind.None);
        await Assert.ThrowsAnyAsync<WalletViewModel.WalletError>(() => model.AddMultisig("alice", "KT1UqzPQCEb6bVYKeab9EXjVcQsUGT8iLC2m"));
        await Assert.ThrowsAnyAsync<WalletViewModel.WalletError>(() => model.AddMultisig("again", "KT1BEqzn5Wx8uJrZNvuS9DVHmLvG9td3fDLi"));
        await Assert.ThrowsAnyAsync<WalletViewModel.WalletError>(() => model.AddMultisig("treasury", "KT1BEqzn5Wx8uJrZNvuS9DVHmLvG9td3fDLi"));
    }

    [Fact]
    public async Task CreateMovesTheInitialBalanceIntoTheContract()
    {
        var service = new MockMultisigService();
        var model = MakeModel(service);
        model.Select(Alice);
        string[] keys = ["edpkALICE", "sppkBOB"];
        await Assert.ThrowsAnyAsync<WalletViewModel.MultisigError>(() => model.CreateMultisig("t", 1, keys, -1, Limits, null));
        await Assert.ThrowsAnyAsync<WalletViewModel.MultisigError>(() => model.EstimateCreateMultisig(1, keys, -0.5m));
        await model.CreateMultisig("empty", 1, keys, 0, Limits, null);
        model.Select(Alice);   // a new contract is shown once created; the payer must be selected again
        await model.CreateMultisig("funded", 1, keys, 2.5m, Limits, null);
        Assert.Equal([0m, 2.5m], service.FundedWith);
    }

    [Fact]
    public async Task CreateChecksThresholdAndKeysThenRecordsTheAlias()
    {
        var service = new MockMultisigService();
        var model = MakeModel(service);
        model.Select(Alice);
        await Assert.ThrowsAnyAsync<WalletViewModel.MultisigError>(() => model.CreateMultisig("t", 3, ["edpkALICE", "sppkBOB"], 0, Limits, null));
        await Assert.ThrowsAnyAsync<WalletViewModel.MultisigError>(() => model.CreateMultisig("t", 1, ["edpkALICE", "notakey"], 0, Limits, null));
        var contract = await model.CreateMultisig("treasury", 2, ["edpkALICE", "sppkBOB", "edpkOTHERpublickeyxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"], 0, Limits, null);
        Assert.Equal(MockMultisigService.SampleAddress, contract.Address);
        Assert.True(service.Threshold == 2 && service.Keys.Count == 3);
        Assert.Equal(["treasury"], model.MultisigContracts.Select(c => c.Alias));
    }

    [Fact]
    public async Task ProposeSignGatherAndSubmit()
    {
        var service = new MockMultisigService(["edpkALICE", "sppkBOB", "edpkOTHER"], 2);
        var model = MakeModel(service);
        var treasury = await model.AddMultisig("treasury", MockMultisigService.SampleAddress.Value);
        var info = await model.MultisigInfo(treasury);
        Assert.Equal(["alice", "bob"], info.Signers(model.Wallets).Select(w => w.Alias));

        var proposal = await model.ProposeMultisigTransfer(treasury, 5, Watch.Address);
        Assert.True(proposal.Counter == 3 && proposal.Threshold == 2 && proposal.Action == new MultisigAction.Transfer("5000000", Watch.Address.Value));
        Assert.Equal("5 tz to tz3WXYtyDUN...Nv5i5ve5", proposal.Summary);
        Assert.Single(model.CurrentMultisigProposals);
        // Proposing the same transfer again is the same proposal.
        Assert.Equal(proposal.Id, (await model.ProposeMultisigTransfer(treasury, 5, Watch.Address)).Id);

        // Watch-only cannot sign; a non-member cannot sign; alice signs once.
        await Assert.ThrowsAnyAsync<WalletViewModel.MultisigError>(() => model.SignMultisigProposal(proposal, Watch, null));
        var sig = await model.SignMultisigProposal(proposal, Alice, null);
        Assert.StartsWith("edsigMock", sig.Signature);
        var current = model.MultisigProposals.First(p => p.Id == proposal.Id);
        Assert.True(current.Signatures.Count == 1 && !current.IsReady);
        await Assert.ThrowsAnyAsync<WalletViewModel.MultisigError>(() => model.SignMultisigProposal(current, Alice, null));

        // Bob's signature arrives by paste; blank and duplicate pastes are ignored.
        model.AddMultisigSignature("  spsigBOBBOB  ", current);
        model.AddMultisigSignature("spsigBOBBOB", current);
        model.AddMultisigSignature("   ", current);
        current = model.MultisigProposals.First(p => p.Id == proposal.Id);
        Assert.Equal([sig.Signature, "spsigBOBBOB"], current.Signatures.Select(s => s.Signature));
        Assert.True(current.IsReady);

        var hash = await model.SubmitMultisig(current, Alice, Limits, null);
        Assert.Equal("ooMockSubmit", hash);
        Assert.True(service.Submitted.Count == 1 && service.Submitted[0].Signatures.Count == 2);
        Assert.Empty(model.MultisigProposals);
    }

    [Fact]
    public async Task SignersComeFromKeysOurAccountsTheAddressBookOrTheChain()
    {
        var service = new MockMultisigService
        {
            Revealed = new() { ["tz3WXYtyDUNL91qfiCJtVUX746QpNv5i5ve5"] = "p2pkWATCHrevealed" },
            KeyAddresses = new() { ["p2pkWATCHrevealed"] = "tz3WXYtyDUNL91qfiCJtVUX746QpNv5i5ve5" },
        };
        var model = MakeModel(service);
        Assert.Equal("edpkPASTEDpublickey", await model.MultisigSignerKey(" edpkPASTEDpublickey "));
        Assert.Equal("edpkALICE", await model.MultisigSignerKey(Alice.Address.Value));          // ours: no lookup
        Assert.Equal("p2pkWATCHrevealed", await model.MultisigSignerKey(Watch.Address.Value));  // address book: revealed key
        // S29: a node that answers with a valid key belonging to someone else is caught locally.
        service.KeyAddresses = new() { ["p2pkWATCHrevealed"] = "tz1dCaMnnMJk76UodjewCK67ABiXWjcKj73N" };
        await Assert.ThrowsAnyAsync<WalletViewModel.MultisigError>(() => model.MultisigSignerKey(Watch.Address.Value));
        service.KeyAddresses = [];   // unreadable key
        await Assert.ThrowsAnyAsync<Exception>(() => model.MultisigSignerKey(Watch.Address.Value));
        service.Revealed = new() { ["tz3WXYtyDUNL91qfiCJtVUX746QpNv5i5ve5"] = "garbage" };
        await Assert.ThrowsAnyAsync<WalletViewModel.MultisigError>(() => model.MultisigSignerKey(Watch.Address.Value));
        service.Revealed = [];
        await Assert.ThrowsAnyAsync<WalletViewModel.MultisigError>(() => model.MultisigSignerKey(Watch.Address.Value));
        await Assert.ThrowsAnyAsync<WalletViewModel.MultisigError>(() => model.MultisigSignerKey("KT1BEqzn5Wx8uJrZNvuS9DVHmLvG9td3fDLi"));
        await Assert.ThrowsAnyAsync<WalletViewModel.MultisigError>(() => model.MultisigSignerKey("hello"));
    }

    /// <summary>A multisig delegates like it spends: a proposal the signers sign, then one submission.</summary>
    [Fact]
    public async Task DelegateChangesAreProposalsToo()
    {
        var service = new MockMultisigService(["edpkALICE", "sppkBOB"], 2);
        var model = MakeModel(service);
        var treasury = await model.AddMultisig("treasury", MockMultisigService.SampleAddress.Value);
        const string baker = "tz1dCaMnnMJk76UodjewCK67ABiXWjcKj73N";
        var setDelegate = await model.ProposeMultisig(new MultisigAction.SetDelegate(baker), treasury);
        var withdraw = await model.ProposeMultisig(new MultisigAction.SetDelegate(null), treasury);
        var transfer = await model.ProposeMultisigTransfer(treasury, 1, Watch.Address);
        Assert.Equal(3, new[] { setDelegate.Bytes, withdraw.Bytes, transfer.Bytes }.Distinct().Count());
        Assert.True(setDelegate.Summary == "Delegate to tz1dCaMnnMJ...WjcKj73N" && withdraw.Summary == "Remove delegate");
        Assert.True(setDelegate.Amount == 0 && withdraw.Amount == 0);
        Assert.Equal(3, model.CurrentMultisigProposals.Count);

        await model.SignMultisigProposal(setDelegate, Alice, null);
        await model.SignMultisigProposal(setDelegate, Bob, "pw");
        var ready = model.MultisigProposals.First(p => p.Id == setDelegate.Id);
        Assert.True(ready.IsReady);
        await model.SubmitMultisig(ready, Alice, Limits, null);
        Assert.Equal(new MultisigAction.SetDelegate(baker), service.Submitted[^1].Proposal.Action);
        Assert.Equal(2, model.CurrentMultisigProposals.Count);
    }

    /// <summary>
    /// S27: a proposal file is untrusted. The signed bytes are rebuilt from the displayed fields
    /// and must equal both the stored bytes and what the chain packs now; otherwise nothing is signed.
    /// </summary>
    [Fact]
    public async Task SigningRebuildsTheBytesAndRefusesTamperedProposals()
    {
        var service = new MockMultisigService(["edpkALICE", "sppkBOB"], 2);
        var contract = MockMultisigService.SampleAddress.Value;
        var action = new MultisigAction.Transfer("1000000", Watch.Address.Value);
        var good = new MultisigProposal("t", contract, Network.Mainnet.Name, "NetXMock", 3, 2, ["edpkALICE", "sppkBOB"], action,
            MockMultisigService.MockBytes("NetXMock", contract, 3, action));
        // Displays "1 tz to watch" but carries the bytes of a delegation (or anything else).
        var otherBytes = good with { Id = Guid.NewGuid(), Bytes = MockMultisigService.MockBytes("NetXMock", contract, 3, new MultisigAction.SetDelegate(Watch.Address.Value)) };
        var operationBytes = good with { Id = Guid.NewGuid(), Bytes = "03deadbeef" };
        var oldCounter = good with { Id = Guid.NewGuid(), Counter = 2 };
        var otherChain = good with { Id = Guid.NewGuid(), ChainID = "NetXOther" };
        var otherKeys = good with { Id = Guid.NewGuid(), Keys = ["edpkALICE", "sppkBOB", "edpkEVE"] };
        var otherNetwork = good with { Id = Guid.NewGuid(), NetworkName = Network.Shadownet.Name };
        var model = MakeModel(service, [good, otherBytes, operationBytes, oldCounter, otherChain, otherKeys, otherNetwork]);
        await model.AddMultisig("t", contract);

        foreach (var bad in new[] { otherBytes, operationBytes, oldCounter, otherChain, otherKeys, otherNetwork })
            await Assert.ThrowsAnyAsync<WalletViewModel.MultisigError>(() => model.SignMultisigProposal(bad, Alice, null));
        Assert.Empty(service.Signed);   // refused before any key was touched

        var signature = await model.SignMultisigProposal(good, Alice, null);
        Assert.Single(service.Signed);
        Assert.True(service.Signed[0].Bytes == good.Bytes && service.Signed[0].Action == action);
        Assert.Equal([signature], model.MultisigProposals.First(p => p.Id == good.Id).Signatures);

        // The chain moving on (counter) or the keys changing makes the stored proposal stale.
        service.Counter = 4;
        await Assert.ThrowsAnyAsync<WalletViewModel.MultisigError>(() => model.SignMultisigProposal(good, Bob, "pw"));
        service.Counter = 3;
        service.Keys = ["edpkALICE", "sppkBOB", "edpkEVE"];
        await Assert.ThrowsAnyAsync<WalletViewModel.MultisigError>(() => model.SignMultisigProposal(good, Bob, "pw"));
        Assert.Single(service.Signed);
    }

    [Fact]
    public async Task ProposalsAreKeptPerNetwork()
    {
        var service = new MockMultisigService(["edpkALICE"], 1);
        var model = MakeModel(service);
        var treasury = await model.AddMultisig("t", MockMultisigService.SampleAddress.Value);
        await model.ProposeMultisigTransfer(treasury, 1, Watch.Address);
        Assert.Single(model.CurrentMultisigProposals);
        model.SwitchNetwork(Network.Shadownet);
        Assert.Empty(model.CurrentMultisigProposals);
    }
}

/// <summary>
/// Live, on Shadownet: a 2-of-3 multisig deployed by Signet on 2026-10-10 reads back as a generic
/// multisig with its three keys (one per curve).
/// </summary>
public class ShadownetMultisigReadTests
{
    [Fact(Timeout = 60_000), Trait(Category.Name, Category.Network)]
    public async Task ReadsADeployedMultisig()
    {
        var service = new BridgeMultisigService(network: Network.Shadownet);
        var info = await service.Info(Network.Shadownet.RpcURL, new Address("KT1UqzPQCEb6bVYKeab9EXjVcQsUGT8iLC2m")).WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(info.IsGenericMultisig);
        Assert.Equal(2, info.Threshold);
        Assert.Equal(["edpktnKyTBhma1YNJdJz63gPJSf41FnreC1XmFDwUtUiDHBQpUPscy", "sppk7bh9rrWgzanNhBqCwyKeEHDKXBdGFcJw1F9Eixt1prn8oB6ot5Z", "p2pk68NJBPLa6KmK3Yk4NoKjMuT9j8F6oknXRmKqwNCErwqyiS8YjiD"], info.Keys);
        Assert.True(info.Counter >= 0);
        var (_, _, bytes) = await service.Prepare(Network.Shadownet.RpcURL, info.Address, new MultisigAction.Transfer("1000000", "tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb"));
        // A contract's balance reads like an account's (the node refuses full_balance for KT1s).
        var balance = await new TaquitoChainService(Network.Shadownet).TezBalanceFor(info.Address);
        Assert.True(balance.Total == info.Balance && balance.Staked == 0);
        TestContext.Current.SendDiagnosticMessage($"bytes to sign (counter {info.Counter}): 0x{bytes}");
    }
}

/// <summary>
/// Windows-only, live on Quantumnet in place of upstream's <c>ShadownetMultisigTests</c> (whose
/// faucet needs proof of work): deploy a 2-of-3 multisig from a faucet-funded key with 1 tez in
/// it, top it up, propose a transfer, sign with two keys, submit and see the tez arrive; then
/// delegate it the same way. Also checks our local packing against the node's <c>pack_data</c>.
/// </summary>
[Collection(Quantumnet.FaucetCollection)]
public class QuantumnetMultisigTests
{
    [Fact, Trait(Category.Name, Category.Network)]
    public async Task TwoOfThreeTransferEndToEnd()
    {
        var network = Network.Quantumnet;
        var rpc = network.RpcURL;
        var service = new BridgeMultisigService(network: network);
        var (payerWallet, payer, chain) = await Quantumnet.FundedTz1("payer");
        var payerKey = new SigningKey.Secret(payer.SecretKey, null, payerWallet.Address);
        var generator = new KeyGenerator();
        // Quantumnet accepts only post-quantum keys in a contract (non_quantum_resistant_signature_algorithm
        // otherwise), so the three signers are tz5; the tz1 just pays.
        var first = await generator.Generate(AddressScheme.Tz5);
        var firstKey = new SigningKey.Secret(first.SecretKey, null, new Address(first.Address));
        var second = await generator.Generate(AddressScheme.Tz5);
        var third = await generator.Generate(AddressScheme.Tz5);
        var recipient = await generator.Generate(AddressScheme.Tz1);

        string[] keys = [first.PublicKey, second.PublicKey, third.PublicKey];
        // Created with 1 tez in it: the initial balance travels with the origination.
        var estimate = await service.EstimateOriginate(rpc, payerWallet, 2, keys, 1);
        Assert.True(estimate.Fee > 0 && estimate.Burn > 0);
        var (originationHash, contract) = await service.Originate(rpc, payerKey, 2, keys, 1, estimate.Limits!);
        await chain.WaitForConfirmation(originationHash);
        Assert.True(contract.IsContract);
        TestContext.Current.SendDiagnosticMessage($"multisig deployed at {contract.Value}");

        var info = await service.Info(rpc, contract);
        Assert.True(info.IsGenericMultisig && info.Threshold == 2 && info.Keys.SequenceEqual(keys) && info.Counter == 0);
        Assert.Equal(1, info.Balance);
        // Originating revealed the payer's key; the recipient has never sent anything.
        Assert.Equal(payer.PublicKey, await service.RevealedPublicKey(rpc, payerWallet.Address));
        Assert.Null(await service.RevealedPublicKey(rpc, new Address(recipient.Address)));

        // Top it up (its default entrypoint takes tez): 1 + 3 = 4 tez.
        var fundEstimate = await chain.EstimateTransfer(payerWallet, contract, 3);
        var funding = await chain.SendTransfer(payerWallet, payerKey, contract, 3, fundEstimate.Limits!);
        await chain.WaitForConfirmation(funding.Hash);

        // Propose 1 tez to the recipient; our packed bytes must equal the node's packing.
        var transfer = new MultisigAction.Transfer("1000000", recipient.Address);
        var (state, chainID, bytes) = await service.Prepare(rpc, contract, transfer);
        Assert.True(state.Counter == 0 && bytes.StartsWith("05", StringComparison.Ordinal));
        var viaNode = await TaquitoBridge.Shared.Call("multisigPayloadViaNode", rpc.AbsoluteString(), chainID, contract.Value, 0, BridgeMultisigService.Json(transfer));
        Assert.Equal(bytes, viaNode["bytes"]?.StringValue);
        Assert.Equal(bytes, viaNode["local"]?.StringValue);

        // Sign with two of the three keys (different curves), then submit from the payer. The
        // payer signs through the multisig API, which rebuilds the bytes rather than taking ours.
        var proposal = new MultisigProposal("test", contract.Value, network.Name, chainID, 0, 2, keys, transfer, "05" + new string('0', 16));  // stored bytes are ignored
        var signed1 = await service.Sign(firstKey, proposal);
        Assert.Equal(bytes, signed1.Bytes);
        var sig3 = await chain.SignPayload(new SigningKey.Secret(third.SecretKey, null, new Address(third.Address)), bytes);
        Assert.Equal(new Address(second.Address), await service.AddressForPublicKey(second.PublicKey));
        // One signature is not enough, and a signature over other bytes does not count.
        await Assert.ThrowsAnyAsync<Exception>(() => service.EstimateSubmit(rpc, payerWallet, proposal, [signed1.Signature]));
        var bogus = await chain.SignPayload(firstKey, "0501000000026869");
        await Assert.ThrowsAnyAsync<Exception>(() => service.EstimateSubmit(rpc, payerWallet, proposal, [signed1.Signature, bogus.Signature]));

        var fee = await service.EstimateSubmit(rpc, payerWallet, proposal, [sig3.Signature, signed1.Signature]);
        Assert.True(fee.Fee > 0);
        var hash = await service.Submit(rpc, payerKey, proposal, [sig3.Signature, signed1.Signature], fee.Limits!);
        TestContext.Current.SendDiagnosticMessage($"multisig transfer {hash}");
        await chain.WaitForConfirmation(hash);

        await Task.Delay(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);
        var after = await service.Info(rpc, contract);
        Assert.Equal(1, after.Counter);
        Assert.Equal(3, after.Balance);
        Assert.Equal(1, (await chain.TezBalanceFor(new Address(recipient.Address))).Spendable);
        // The same signatures cannot be replayed: the counter moved on.
        await Assert.ThrowsAnyAsync<Exception>(() => service.EstimateSubmit(rpc, payerWallet, proposal, [sig3.Signature, signed1.Signature]));
        var contractBalance = await chain.TezBalanceFor(contract);
        Assert.True(contractBalance.Total == 3 && contractBalance.Spendable == 3);

        // Now delegate the multisig to a baker, signed by the other two keys.
        var baker = (await chain.Bakers(10)).FirstOrDefault()?.Address.Value;
        Assert.NotNull(baker);
        var delegateAction = new MultisigAction.SetDelegate(baker);
        var (state2, _, bytes2) = await service.Prepare(rpc, contract, delegateAction);
        Assert.Equal(1, state2.Counter);
        var viaNode2 = await TaquitoBridge.Shared.Call("multisigPayloadViaNode", rpc.AbsoluteString(), chainID, contract.Value, 1, BridgeMultisigService.Json(delegateAction));
        Assert.Equal(bytes2, viaNode2["bytes"]?.StringValue);
        var dsig2 = await chain.SignPayload(new SigningKey.Secret(second.SecretKey, null, new Address(second.Address)), bytes2);
        var dsig3 = await chain.SignPayload(new SigningKey.Secret(third.SecretKey, null, new Address(third.Address)), bytes2);
        var delegation = new MultisigProposal("test", contract.Value, network.Name, chainID, 1, 2, keys, delegateAction, bytes2);
        var delegateEstimate = await service.EstimateSubmit(rpc, payerWallet, delegation, [dsig2.Signature, dsig3.Signature]);
        var delegateHash = await service.Submit(rpc, payerKey, delegation, [dsig2.Signature, dsig3.Signature], delegateEstimate.Limits!);
        TestContext.Current.SendDiagnosticMessage($"multisig delegation {delegateHash}");
        await chain.WaitForConfirmation(delegateHash);
        await Task.Delay(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);
        Assert.Equal(baker, (await chain.DelegateInfoFor(contract)).Delegate?.Value);
        Assert.Equal(2, (await service.Info(rpc, contract)).Counter);
    }
}
