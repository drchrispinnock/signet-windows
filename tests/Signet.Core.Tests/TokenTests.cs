using System.Text;
using Signet.Models;
using Signet.Services;

namespace Signet.Tests;

public class FungibleTokenTests
{
    public const string Fixture = """
    [
      {"balance":"1","token":{"contract":{"address":"KT1GBZmSxmnKJXGMdMLbugPfLyUPmuLSMwKS"},"tokenId":"143032","standard":"fa2","metadata":{"name":"captstake.tez","symbol":"TD","decimals":"0"}}},
      {"balance":"30000000000","token":{"contract":{"address":"KT1FfjhvJZppBFQuUzNAdFPR1Z2jpD4XiXrF"},"tokenId":"0","standard":"fa2","metadata":{"name":"Aubergine","symbol":"GINE","decimals":"6","thumbnailUri":"https://gateway.pinata.cloud/ipfs/Qmaf8m2vtegsvoWybB2AL1wJ4Q79SwornWx8T7DbkYRhv9"}}},
      {"balance":"10","token":{"contract":{"address":"KT1BPGyFqmGQqsU4oEL8NUXQpHQGwuFdyjd1"},"tokenId":"0","standard":"fa1.2","metadata":{"name":"FabRicE","symbol":"RICE","decimals":"1","icon":"https://example.com/rice.jpg"}}},
      {"balance":"1047540545999871429","token":{"contract":{"address":"KT1VaEsVNiBoA56eToEK6n6BcPgh1tdx9eXi"},"tokenId":"0","standard":"fa2","metadata":{"name":"Temple Key","symbol":"TKEY","decimals":"18","thumbnailUri":"ipfs://Qmb9QUXYn1PW8e7E2CwpBMgEur7gFAPPpq2Zh7H2D7eQcT"}}},
      {"balance":"500000","token":{"contract":{"address":"KT1MZg99PxMDEENwB4Fi64xkqAVh5d1rv8Z9"},"tokenId":"0","standard":"fa2","metadata":{"name":"Tezos Pepe","symbol":"PEPE","decimals":"2"}}},
      {"balance":"1","token":{"contract":{"address":"KT1NFT"},"tokenId":"7","standard":"fa2","metadata":{"name":"Picture","decimals":"0","displayUri":"ipfs://QmNrhZHUaEqxhyLfqoq1mtHSipkWHeT31LNHb1QEbDHgnc"}}}
    ]
    """;

    [Fact]
    public void PicksFungibleTokensAndScalesByDecimals()
    {
        var rows = TzKTService.Decode(Encoding.UTF8.GetBytes(Fixture));
        var tokens = TzKTService.FungibleTokens(rows);
        AssetBalance Token(string symbol) => tokens.First(t => t.Symbol == symbol);
        Assert.Equal(["GINE", "PEPE", "TKEY", "RICE"], tokens.Select(t => t.Symbol));   // largest balance first
        Assert.Equal(30_000m, Token("GINE").Amount);
        Assert.Equal(5_000m, Token("PEPE").Amount);
        Assert.Equal(1m, Token("RICE").Amount);
        Assert.Equal(1.047540545999871429m, Token("TKEY").Amount);
        Assert.Equal("fa1.2", Token("RICE").Standard);
        Assert.Equal("https://example.com/rice.jpg", Token("RICE").IconURLs[0].AbsoluteString());
        Assert.Equal(IPFS.Gateways.Count, Token("TKEY").IconURLs.Count);
        Assert.Equal(new AssetBalance.AssetKind.Token("KT1FfjhvJZppBFQuUzNAdFPR1Z2jpD4XiXrF", "0"), Token("GINE").Kind);
        // The domain token and the picture are not assets: zero decimals, and the picture is an NFT.
        Assert.DoesNotContain(tokens, t => t.Name == "captstake.tez" || t.Name == "Picture");
        Assert.Equal(["Picture"], TzKTService.Nfts(rows).Select(n => n.Name));
    }

    [Fact, Trait(Category.Name, Category.Network)]
    public async Task LiveMainnetTokens()
    {
        var tokens = await new TzKTService(Network.Mainnet.TzktURL!).FungibleTokensFor(MockChainService.CaptainStake);
        var symbols = tokens.Select(t => t.Symbol).ToHashSet();
        Assert.True(symbols.IsSupersetOf(["PEPE", "TKEY", "GINE"]), $"got {string.Join(", ", symbols)}");
    }
}

/// <summary>
/// Metadata Signet reads for itself when the indexer has none: the <c>token_metadata</c> big map
/// entry and the TZIP-21 document it names (seen live: Etherlink Shadownet's TzKT resolved
/// nothing for a fresh FA2, so its NFT was neither an asset nor an NFT).
/// </summary>
public class TokenMetadataFallbackTests
{
    private const string Contract = "KT1MJPVfyTqycX13mGq8Adpays8yxJrNPjXp";
    private const string Uri = "ipfs://bafkreib5alq3q4zsqokoohi6nrp23uzqnolto67aomapucnr5yk3tj3olu";
    private const string Image = "ipfs://bafybeigqpjyhcy2kkb4f5rpdgs7al6pgaqcpfum2344ttekzxbm7i4qxku";
    private static readonly byte[] Document = Encoding.UTF8.GetBytes($$"""
    {"name":"Layer 2 Elvis","description":"My dog has always wanted to visit Etherlink","decimals":0,"isBooleanAmount":true,"symbol":"NFT",
     "artifactUri":"{{Image}}","displayUri":"{{Image}}","thumbnailUri":"{{Image}}","creators":["tz3Rb5RVucLc6ani23jVdZM4Qm6c4oQ74zhh"],
     "formats":[{"uri":"{{Image}}","mimeType":"image/jpeg"}],"tags":[]}
    """);
    /// <summary>TzKT's reply for <c>/v1/contracts/&lt;KT1&gt;/bigmaps/token_metadata/keys/0</c>, the URI as hex bytes.</summary>
    private static readonly byte[] KeyReply = Encoding.UTF8.GetBytes("""
    {"id":81,"active":true,"key":"0","value":{"token_id":"0","token_info":{"":"URIHEX","symbol":"4e4654","odd":"zz"}}}
    """.Replace("URIHEX", Convert.ToHexStringLower(Encoding.UTF8.GetBytes(Uri))));

    [Fact]
    public void TheBigMapEntryIsHexDecoded()
    {
        var info = TzKTService.ParseTokenInfo(KeyReply)!;
        Assert.True(info[""] == Uri && info["symbol"] == "NFT" && !info.ContainsKey("odd"));
        Assert.Null(TzKTService.ParseTokenInfo("[]"u8.ToArray()));
    }

    [Fact]
    public void ADocumentWithNumericDecimalsReadsAsTheIndexerWouldReportIt()
    {
        var metadata = TokenMetadataResolver.DecodeDocument(Document)!;
        Assert.True(metadata.Name == "Layer 2 Elvis" && metadata.Decimals == "0" && metadata.DecimalPlaces == 0 && metadata.Symbol == "NFT");
        Assert.True(metadata.DisplayUri == Image && metadata.Formats?.FirstOrDefault()?.MimeType == "image/jpeg");
        // Odd types are missing fields, not a failure; a non-object is nothing.
        var odd = TokenMetadataResolver.DecodeDocument("""{"name":7,"decimals":"6","symbol":"X","formats":"no"}"""u8.ToArray())!;
        Assert.True(odd.Decimals == "6" && odd.Symbol == "X" && odd.Formats is null);
        Assert.Null(TokenMetadataResolver.DecodeDocument("[1,2]"u8.ToArray()));
        Assert.True(TokenMetadataResolver.DecodeDocument("{}"u8.ToArray())?.IsEmpty);
    }

    [Fact]
    public void DirectEntriesOverrideTheDocumentAndNothingUsableIsNull()
    {
        var merged = TokenMetadataResolver.MetadataFrom(new Dictionary<string, string> { [""] = Uri, ["name"] = "Elvis on chain", ["decimals"] = "0" }, Document);
        Assert.True(merged?.Name == "Elvis on chain" && merged.Symbol == "NFT" && merged.DisplayUri == Image);
        // No document: the direct entries alone still describe a token.
        var direct = TokenMetadataResolver.MetadataFrom(new Dictionary<string, string> { ["name"] = "Plain", ["symbol"] = "PLN", ["decimals"] = "6" }, null);
        Assert.True(direct?.Name == "Plain" && direct.DecimalPlaces == 6);
        Assert.Null(TokenMetadataResolver.MetadataFrom(new Dictionary<string, string> { [""] = "tezos-storage:content" }, null));
        Assert.Null(TokenMetadataResolver.MetadataFrom(new Dictionary<string, string>(), "{}"u8.ToArray()));
    }

    [Fact]
    public void DocumentsAreFetchedOnlyFromGatewaysOrPublicHTTPS()
    {
        Assert.Equal(IPFS.Gateways.Count, TokenMetadataResolver.DocumentURLs(Uri).Count);
        Assert.Equal(["https://example.com/meta/0.json"], TokenMetadataResolver.DocumentURLs("https://example.com/meta/0.json").Select(u => u.AbsoluteString()));
        Assert.Empty(TokenMetadataResolver.DocumentURLs("http://example.com/meta.json"));
        Assert.Empty(TokenMetadataResolver.DocumentURLs("https://localhost/meta.json"));
        Assert.Empty(TokenMetadataResolver.DocumentURLs("tezos-storage:content"));
    }

    [Fact]
    public async Task RowsWithoutMetadataAreFilledAndThenClassified()
    {
        var rows = TzKTService.Decode(Encoding.UTF8.GetBytes("""
        [
          {"balance":"1","token":{"contract":{"address":"@Contract@"},"tokenId":"0","standard":"fa2","metadata":null}},
          {"balance":"1","token":{"contract":{"address":"KT1BPGyFqmGQqsU4oEL8NUXQpHQGwuFdyjd1"},"tokenId":"0","standard":"fa2","metadata":{"name":"Known","decimals":"0","displayUri":"@Image@"}}},
          {"balance":"5","token":{"contract":{"address":"KT1VaEsVNiBoA56eToEK6n6BcPgh1tdx9eXi"},"tokenId":"3","standard":"fa2","metadata":null}}
        ]
        """.Replace("@Contract@", Contract).Replace("@Image@", Image)));
        var asked = new List<string>();
        var resolver = new TokenMetadataResolver(Guid.NewGuid().ToString(), (contract, tokenId) =>
        {
            lock (asked) asked.Add($"info {contract}:{tokenId}");
            return Task.FromResult(contract == Contract ? TzKTService.ParseTokenInfo(KeyReply) : null);
        }, url =>
        {
            lock (asked) asked.Add($"doc {url.Host}");
            return Task.FromResult(url.AbsoluteUri.Contains("bafkreib5alq3q4z") ? Document : null);
        });
        var filled = await resolver.Fill(rows);
        Assert.True(filled[0].Token.Metadata?.Name == "Layer 2 Elvis" && filled[1].Token.Metadata?.Name == "Known" && filled[2].Token.Metadata is null);
        Assert.Equal(["Layer 2 Elvis", "Known"], TzKTService.Nfts(filled).Select(n => n.Name));
        Assert.Empty(TzKTService.FungibleTokens(filled));
        Assert.Equal([$"info {Contract}:0", "doc ipfs.filebase.io", "info KT1VaEsVNiBoA56eToEK6n6BcPgh1tdx9eXi:3"], asked);
        // The next load answers both from the cache, the hit and the miss alike.
        var again = await resolver.Fill(rows);
        Assert.True(again[0].Token.Metadata?.Name == "Layer 2 Elvis" && again[2].Token.Metadata is null && asked.Count == 3);
    }

    [Fact, Trait(Category.Name, Category.Network)]
    public async Task LiveEtherlinkShadownetTokenMetadata()
    {
        var tzkt = new TzKTService(Network.Shadownet.L2TzKTURL!);
        Assert.Equal(Uri, (await tzkt.TokenInfo(Contract, "0"))?[""]);
        var metadata = await TokenMetadataResolver.Live(tzkt).Resolve(Contract, "0");
        Assert.True(metadata?.Name == "Layer 2 Elvis" && metadata.DecimalPlaces == 0);
    }
}

public class PortfolioStateTests
{
    [Fact]
    public void UnreadableDecimalsAreNotGuessed()
    {
        var rows = TzKTService.Decode("""
        [
          {"balance":"100","token":{"contract":{"address":"KT1A"},"tokenId":"0","standard":"fa2","metadata":{"name":"Huge","symbol":"HUGE","decimals":"400"}}},
          {"balance":"100","token":{"contract":{"address":"KT1B"},"tokenId":"0","standard":"fa2","metadata":{"name":"Neg","symbol":"NEG","decimals":"-3"}}},
          {"balance":"100","token":{"contract":{"address":"KT1C"},"tokenId":"0","standard":"fa2","metadata":{"name":"Text","symbol":"TXT","decimals":"lots"}}},
          {"balance":"100","token":{"contract":{"address":"KT1D"},"tokenId":"0","standard":"fa1.2","metadata":{"name":"Old","symbol":"OLD","decimals":"1e2"}}},
          {"balance":"-5","token":{"contract":{"address":"KT1E"},"tokenId":"0","standard":"fa2","metadata":{"name":"Neg balance","symbol":"NB","decimals":"2"}}},
          {"balance":"250","token":{"contract":{"address":"KT1F"},"tokenId":"0","standard":"fa2","metadata":{"name":"Fine","symbol":"OK","decimals":"2"}}}
        ]
        """u8.ToArray());
        var tokens = TzKTService.FungibleTokens(rows);
        Assert.Equal(["OLD", "OK"], tokens.Select(t => t.Symbol));
        Assert.Equal(100m, tokens.First(t => t.Symbol == "OLD").Amount);   // FA1.2 with unreadable decimals: shown unscaled
        Assert.Equal(2.5m, tokens.First(t => t.Symbol == "OK").Amount);
        Assert.Empty(TzKTService.Nfts(rows));
    }

    private sealed class Flaky : TestChainService
    {
        public bool Fail;
        public override Task<TezBalance> TezBalanceFor(Address address) => Task.FromResult(new TezBalance(1));
        public override Task<Portfolio> PortfolioFor(Address address) => Task.FromResult(Fail
            ? new Portfolio([], [], new Portfolio.PortfolioState.Unavailable("HTTP 503"))
            : new Portfolio([new AssetBalance("t", new AssetBalance.AssetKind.Token("KT1x", "0"), "Tok", "TOK", 1)], [], new Portfolio.PortfolioState.Partial(1000)));
    }

    [Fact]
    public async Task AnUnavailableIndexerKeepsTheLastKnownListAndSaysSo()
    {
        var chain = new Flaky();
        var model = new Signet.ViewModels.WalletViewModel(wallets: [new Wallet("A", new Address("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb"), keyKind: KeyKind.Unencrypted)], chain: chain);
        await model.Refresh();
        Assert.Equal(["TOK"], model.Tokens.Select(t => t.Symbol));
        Assert.Equal(new Portfolio.PortfolioState.Partial(1000), model.PortfolioState);
        chain.Fail = true;
        await model.Refresh();
        Assert.Equal(["TOK"], model.Tokens.Select(t => t.Symbol));   // last known holdings stay on screen
        Assert.Equal(new Portfolio.PortfolioState.Unavailable("HTTP 503"), model.PortfolioState);
    }
}
