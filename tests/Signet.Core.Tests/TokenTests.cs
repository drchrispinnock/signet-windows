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
      {"balance":"1","token":{"contract":{"address":"KT1NFT"},"tokenId":"7","standard":"fa2","metadata":{"name":"Picture","decimals":"0","displayUri":"ipfs://QmPic"}}}
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
