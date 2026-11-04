using System.Globalization;
using System.Text;
using Signet.Models;
using Signet.Services;

namespace Signet.Tests;

public class IPFSTests
{
    [Theory]
    [InlineData("ipfs://QmNrhZHUaEqxhyLfqoq1mtHSipkWHeT31LNHb1QEbDHgnc", "https://ipfs.filebase.io/ipfs/QmNrhZHUaEqxhyLfqoq1mtHSipkWHeT31LNHb1QEbDHgnc")]
    [InlineData("ipfs://QmXxvrULyrWH18e6kUinPNXt5PAwv6h7MPcmWbSw4DwDuP/?fxhash=opPk", "https://ipfs.filebase.io/ipfs/QmXxvrULyrWH18e6kUinPNXt5PAwv6h7MPcmWbSw4DwDuP/?fxhash=opPk")]
    [InlineData("https://gateway.pinata.cloud/ipfs/Qmaf8m2vtegsvoWybB2AL1wJ4Q79SwornWx8T7DbkYRhv9", "https://ipfs.filebase.io/ipfs/Qmaf8m2vtegsvoWybB2AL1wJ4Q79SwornWx8T7DbkYRhv9")]
    [InlineData("https://i.pinimg.com/originals/81/ec/8e/x.jpg", "https://i.pinimg.com/originals/81/ec/8e/x.jpg")]
    public void RewritesToGateway(string uri, string expected)
    {
        Assert.Equal(expected, IPFS.HttpURL(uri)?.AbsoluteString());
    }

    [Fact]
    public void OffersEveryGatewayForIPFSButOnlyOneForHTTPS()
    {
        var ipfs = IPFS.CandidateURLs("ipfs://QmNrhZHUaEqxhyLfqoq1mtHSipkWHeT31LNHb1QEbDHgnc");
        Assert.Equal(IPFS.Gateways.Count, ipfs.Count);
        Assert.Equal(IPFS.Gateways.Select(g => g.Host), ipfs.Select(u => u.Host));
        Assert.Single(IPFS.CandidateURLs("https://example.com/x.png"));
    }

    [Fact]
    public void WidensOtherGatewayURLsToOurs()
    {
        var subdomain = IPFS.CandidateURLs("https://bafybeigqka2ynrib6ytxku3nvakork5smsxni5xdqro56kd7ecsfos7z7a.ipfs.dweb.link/");
        Assert.Equal("https://ipfs.filebase.io/ipfs/bafybeigqka2ynrib6ytxku3nvakork5smsxni5xdqro56kd7ecsfos7z7a", subdomain[0].AbsoluteString());
        Assert.Equal(IPFS.Gateways.Count + 1, subdomain.Count);
        Assert.Equal("bafybeigqka2ynrib6ytxku3nvakork5smsxni5xdqro56kd7ecsfos7z7a.ipfs.dweb.link", subdomain[^1].Host);

        var path = IPFS.CandidateURLs("https://gateway.pinata.cloud/ipfs/Qmaf8m2vtegsvoWybB2AL1wJ4Q79SwornWx8T7DbkYRhv9");
        Assert.Equal("https://ipfs.filebase.io/ipfs/Qmaf8m2vtegsvoWybB2AL1wJ4Q79SwornWx8T7DbkYRhv9", path[0].AbsoluteString());
        Assert.Equal(IPFS.Gateways.Count, path.Count);   // pinata is already one of ours
        Assert.Single(IPFS.CandidateURLs("https://i.pinimg.com/originals/81/ec/8e/x.jpg"));
    }

    [Fact(Skip = "Pending port: ImageLoader (app layer)")]
    public void DecodesSVGLogos() { }

    [Fact]
    public void RejectsOtherSchemes()
    {
        Assert.Null(IPFS.HttpURL("data:image/png;base64,AAAA"));
        Assert.Null(IPFS.HttpURL(""));
    }
}

public class NFTClassificationTests
{
    public static readonly byte[] Fixture = Encoding.UTF8.GetBytes("""
    [
      { "balance": "1", "lastTime": "2026-10-21T16:48:10Z", "token": { "contract": { "address": "KT1RJ6PbjHpwc3M5rw5s2Nbmefwbuwbdxton" }, "tokenId": "634799", "standard": "fa2",
        "metadata": { "name": "United in Baking", "symbol": "OBJKT", "decimals": "0",
                      "thumbnailUri": "ipfs://QmXxvrULyrWH18e6kUinPNXt5PAwv6h7MPcmWbSw4DwDuP", "displayUri": "ipfs://QmNrhZHUaEqxhyLfqoq1mtHSipkWHeT31LNHb1QEbDHgnc", "artifactUri": "ipfs://Qmaf8m2vtegsvoWybB2AL1wJ4Q79SwornWx8T7DbkYRhv9",
                      "formats": [ { "uri": "ipfs://Qmaf8m2vtegsvoWybB2AL1wJ4Q79SwornWx8T7DbkYRhv9", "mimeType": "image/jpeg" } ] } } },
      { "balance": "20", "token": { "contract": { "address": "KT1F139Vd1mcqYejDnzQQkVQcQboPc4EUbqx" }, "tokenId": "0", "standard": "fa2",
        "metadata": { "name": "Captain Stake", "decimals": "0", "artifactUri": "ipfs://QmNNgpMfD227yefhGREQHHYAdB62hrTihUWJJnKuSHNxxx",
                      "formats": [ { "uri": "ipfs://QmNNgpMfD227yefhGREQHHYAdB62hrTihUWJJnKuSHNxxx", "mimeType": "image/png" } ] } } },
      { "balance": "1", "token": { "contract": { "address": "KT1GBZmSxmnKJXGMdMLbugPfLyUPmuLSMwKS" }, "tokenId": "143032", "standard": "fa2",
        "metadata": { "name": "captstake.tez", "decimals": "0", "symbol": "TD" } } },
      { "balance": "30000000000", "token": { "contract": { "address": "KT1FfjhvJZppBFQuUzNAdFPR1Z2jpD4XiXrF" }, "tokenId": "0", "standard": "fa2",
        "metadata": { "name": "Aubergine", "decimals": "6", "symbol": "GINE", "thumbnailUri": "https://gateway.pinata.cloud/ipfs/Qmaf8" } } },
      { "balance": "1", "token": { "contract": { "address": "KT1video" }, "tokenId": "7", "standard": "fa2",
        "metadata": { "name": "Clip", "decimals": "0", "artifactUri": "ipfs://Qmb9QUXYn1PW8e7E2CwpBMgEur7gFAPPpq2Zh7H2D7eQcT",
                      "formats": [ { "uri": "ipfs://Qmb9QUXYn1PW8e7E2CwpBMgEur7gFAPPpq2Zh7H2D7eQcT", "mimeType": "video/mp4" } ] } } },
      { "balance": "1", "firstTime": "2026-10-21T16:00:00Z", "token": { "contract": { "address": "KT1RJ6PbjHpwc3M5rw5s2Nbmefwbuwbdxton" }, "tokenId": "44684", "standard": "fa2",
        "metadata": { "name": "NFT", "decimals": "0", "thumbnailUri": "ipfs://QmNrhZHUaEqxhyLfqoq1mtHSipkWHeT31LNHb1QEbDHgnc", "artifactUri": "ipfs://QmbQHn7wSXcqgzrvJJ8BA1tKzR5n2BWdbXnFTXz2EKWfHM",
                      "formats": [ { "uri": "ipfs://QmbQHn7wSXcqgzrvJJ8BA1tKzR5n2BWdbXnFTXz2EKWfHM", "mimeType": "video/mp4" } ] } } }
    ]
    """);

    [Fact]
    public void PicksNFTsAndBestImages()
    {
        var rows = TzKTService.Decode(Fixture);
        Assert.Equal(6, rows.Count);
        var nfts = TzKTService.Nfts(rows);

        Assert.Equal(["United in Baking", "Captain Stake", "NFT"], nfts.Select(n => n.Name));
        Assert.True(nfts[0].Date == DateTimeOffset.Parse("2026-10-21T16:48:10Z", CultureInfo.InvariantCulture) && nfts[0].ArtifactMime == "image/jpeg" && !nfts[0].IsMoving);
        Assert.True(nfts[1].Date is null && nfts[1].ArtifactMime == "image/png");
        // A video with only a marketplace's placeholder still is shown as that still, marked as a video.
        Assert.True(nfts[2].Date == DateTimeOffset.Parse("2026-10-21T16:00:00Z", CultureInfo.InvariantCulture) && nfts[2].IsMoving && nfts[2].ImageURLs.Count == IPFS.Gateways.Count);
        Assert.Equal("KT1RJ6PbjHpwc3M5rw5s2Nbmefwbuwbdxton:634799", nfts[0].Id);
        // displayUri comes first, then thumbnailUri and the image artifact as fallbacks, one URL per gateway each.
        Assert.Equal("https://ipfs.filebase.io/ipfs/QmNrhZHUaEqxhyLfqoq1mtHSipkWHeT31LNHb1QEbDHgnc", nfts[0].ThumbnailURL?.AbsoluteString());
        Assert.Equal(3 * IPFS.Gateways.Count, nfts[0].ImageURLs.Count);
        Assert.Equal(new[] { "QmNrhZHUaEqxhyLfqoq1mtHSipkWHeT31LNHb1QEbDHgnc", "QmXxvrULyrWH18e6kUinPNXt5PAwv6h7MPcmWbSw4DwDuP", "Qmaf8m2vtegsvoWybB2AL1wJ4Q79SwornWx8T7DbkYRhv9" }.SelectMany(cid => Enumerable.Repeat(cid, IPFS.Gateways.Count)),
                     nfts[0].ImageURLs.Select(IPFS.GatewayPath));
        // An image artifact is acceptable when there is nothing else.
        Assert.Equal("https://ipfs.filebase.io/ipfs/QmNNgpMfD227yefhGREQHHYAdB62hrTihUWJJnKuSHNxxx", nfts[1].ThumbnailURL?.AbsoluteString());
        Assert.Equal(20m, nfts[1].Balance);
        // Fungible tokens, image-less tokens and video-only artifacts are not shown.
    }

    /// <summary>Both layers' NFTs intermingle newest first; undated ones follow in their own order.</summary>
    [Fact]
    public void MergedNFTsAreNewestFirstAcrossLayers()
    {
        static NFT Nft(string name, int? daysAgo) => new(name, "KT1x", name, name, 1, thumbnailURL: null)
        {
            Date = daysAgo is { } d ? DateTimeOffset.FromUnixTimeSeconds(1_790_000_000 - d * 86_400L) : null,
        };
        var layer1 = new Portfolio([], [Nft("a", 1), Nft("c", 10), Nft("u1", null)], new Portfolio.PortfolioState.Complete());
        var layer2 = new Portfolio([], [Nft("b", 0), Nft("d", 5), Nft("u2", null)], new Portfolio.PortfolioState.Complete()).OnLayer2();
        var merged = layer1.Adding(layer2);
        Assert.Equal(["b", "a", "d", "c", "u1", "u2"], merged.Nfts.Select(n => n.Name));
        Assert.Equal([true, false, true, false, false, true], merged.Nfts.Select(n => n.IsLayer2));
        Assert.IsType<Portfolio.PortfolioState.Complete>(merged.State);
    }

    [Fact, Trait(Category.Name, Category.Network)]
    public async Task CaptainStakeHasNFTsWithImages()
    {
        var nfts = await new TzKTService(Network.Mainnet.TzktURL!).NftsFor(MockChainService.CaptainStake);
        Assert.NotEmpty(nfts);
        Assert.All(nfts, n => Assert.Equal("https", n.ThumbnailURL?.Scheme));
        Assert.Contains(nfts, n => n.Name == "Captain Stake");
    }
}

public class ImageLoaderTests
{
    [Fact(Skip = "Pending port: ImageLoader (app layer)")] public void DownsamplesToRequestedSize() { }
    [Fact(Skip = "Pending port: ImageLoader (app layer)"), Trait(Category.Name, Category.Network)] public void FallsThroughRateLimitedGatewaysToAWorkingOne() { }
}
