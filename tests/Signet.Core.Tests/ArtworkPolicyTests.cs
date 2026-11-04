using Signet.Models;
using Signet.Services;

namespace Signet.Tests;

/// <summary>
/// S13: artwork URLs come from token metadata, so only public HTTPS destinations may be contacted
/// and an IPFS path may not climb out of the gateway's <c>/ipfs/</c>.
/// </summary>
public class ArtworkPolicyTests
{
    private const string Cid = "QmNrhZHUaEqxhyLfqoq1mtHSipkWHeT31LNHb1QEbDHgnc";

    [Theory]
    [InlineData("127.0.0.1")] [InlineData("10.1.2.3")] [InlineData("192.168.1.9")] [InlineData("169.254.169.254")] [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")] [InlineData("100.64.0.1")] [InlineData("0.0.0.0")] [InlineData("::1")] [InlineData("::")] [InlineData("fc00::1")]
    [InlineData("fd12::3")] [InlineData("fe80::1")] [InlineData("::ffff:10.0.0.1")] [InlineData("[::1]")] [InlineData("localhost")]
    [InlineData("foo.local")] [InlineData("api.internal")] [InlineData("printer")] [InlineData("router.lan")]
    public void ClassifiesPrivateHosts(string host) => Assert.True(EndpointPolicy.IsLocalOrPrivateHost(host));

    [Theory]
    [InlineData("8.8.8.8")] [InlineData("example.org")] [InlineData("ipfs.filebase.io")] [InlineData("2606:4700:4700::1111")] [InlineData("172.32.0.1")] [InlineData("11.0.0.1")]
    public void ClassifiesPublicHosts(string host) => Assert.False(EndpointPolicy.IsLocalOrPrivateHost(host));

    [Fact]
    public void ChecksSchemeCredentialsAndHost()
    {
        Assert.IsType<EndpointPolicy.Verdict.Allowed>(EndpointPolicy.Check(new Uri("https://example.org/a.png")));
        Assert.IsType<EndpointPolicy.Verdict.NotHTTPS>(EndpointPolicy.Check(new Uri("http://example.org/a.png")));
        Assert.IsType<EndpointPolicy.Verdict.Allowed>(EndpointPolicy.Check(new Uri("http://example.org/a.png"), allowHTTP: true));
        Assert.IsType<EndpointPolicy.Verdict.HasUserInfo>(EndpointPolicy.Check(new Uri("https://user:pw@example.org/a.png")));
        Assert.Equal(new EndpointPolicy.Verdict.LocalOrPrivate("127.0.0.1"), EndpointPolicy.Check(new Uri("https://127.0.0.1:8732/x")));
        Assert.Equal(new EndpointPolicy.Verdict.LocalOrPrivate("::1"), EndpointPolicy.Check(new Uri("https://[::1]/x")));
        Assert.IsType<EndpointPolicy.Verdict.NotHTTPS>(EndpointPolicy.Check(new Uri("ftp://example.org/x")));
        Assert.False(EndpointPolicy.IsPublicHTTPS(new Uri("https://localhost/x")));
    }

    [Theory]
    [InlineData("http://example.org/a.png")]                         // not https
    [InlineData("https://127.0.0.1/a.png")]                          // loopback
    [InlineData("https://[::1]:8080/a.png")]                         // loopback v6
    [InlineData("https://10.0.0.5/a.png")]                           // private
    [InlineData("https://192.168.0.1/a.png")]                        // private
    [InlineData("https://169.254.169.254/latest/meta-data")]         // link local
    [InlineData("https://localhost:8732/chains/main")]               // local name
    [InlineData("https://nas.local/a.png")]                          // mDNS
    [InlineData("https://user:secret@example.org/a.png")]            // credentials
    [InlineData("file:///etc/passwd")]
    [InlineData("data:image/png;base64,AAAA")]
    public void DropsUnsafeArtworkURLs(string uri)
    {
        Assert.Empty(IPFS.CandidateURLs(uri));
        Assert.Null(IPFS.HttpURL(uri));
    }

    [Fact]
    public void KeepsPublicHTTPSArtwork()
    {
        Assert.Equal(["https://example.org/a.png"], IPFS.CandidateURLs("https://example.org/a.png").Select(u => u.AbsoluteString()));
        Assert.Equal(IPFS.Gateways.Count, IPFS.CandidateURLs($"ipfs://{Cid}").Count);
        Assert.Equal($"https://ipfs.filebase.io/ipfs/{Cid}/images/one.png", IPFS.CandidateURLs($"ipfs://{Cid}/images/one.png")[0].AbsoluteString());
        // A gateway URL on a private host still yields our gateways, never the private one.
        var widened = IPFS.CandidateURLs($"https://10.0.0.1/ipfs/{Cid}");
        Assert.Equal(IPFS.Gateways.Count, widened.Count);
        Assert.DoesNotContain(widened, u => u.Host == "10.0.0.1");
    }

    [Theory]
    [InlineData($"ipfs://{Cid}/../../etc/passwd")]
    [InlineData($"ipfs://{Cid}/a/../b")]
    [InlineData($"ipfs://{Cid}/%2e%2e/x")]
    [InlineData($"ipfs://{Cid}//x")]
    [InlineData($"ipfs://{Cid}/a%2Fb/../c")]
    [InlineData($"ipfs://{Cid}/a\\..\\b")]
    [InlineData("ipfs://QmShort")]
    [InlineData("ipfs://not-a-cid/x")]
    [InlineData($"ipfs://../../{Cid}")]
    [InlineData($"https://gateway.pinata.cloud/ipfs/{Cid}/../other")]
    public void RefusesTraversalAndBadCIDs(string uri) => Assert.Empty(IPFS.CandidateURLs(uri));

    [Fact]
    public void EncodesPathComponentsSoTheyStayUnderTheCID()
    {
        var first = IPFS.CandidateURLs($"ipfs://{Cid}/a b/c;d.png?x=1")[0];
        Assert.Equal($"https://ipfs.filebase.io/ipfs/{Cid}/a%20b/c%3Bd.png?x=1", first.AbsoluteString());
        Assert.Equal(["ipfs", Cid], first.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Take(2));
        Assert.Equal(Cid + "/", IPFS.PathOf($"ipfs://{Cid}/"));
        Assert.Equal(Cid, IPFS.PathOf($"ipfs://ipfs/{Cid}"));
        Assert.True(IPFS.LooksLikeCID(Cid));
        Assert.True(IPFS.LooksLikeCID("bafybeigqka2ynrib6ytxku3nvakork5smsxni5xdqro56kd7ecsfos7z7a"));
        Assert.False(IPFS.LooksLikeCID("QmNrhZHUaEqxhyLfqoq1mtHSipkWHeT31LNHb1QEbDHgn0"));   // 0 is not base58
        Assert.False(IPFS.LooksLikeCID("bafy/../x"));
    }

    [Fact]
    public void AcceptsOnlyImageResponsesWithinTheByteLimit()
    {
        Assert.True(ImageLoader.AcceptsResponse(200, "image/png", 1000));
        Assert.True(ImageLoader.AcceptsResponse(200, "image/svg+xml; charset=utf-8", null));
        Assert.True(ImageLoader.AcceptsResponse(200, null, null));
        Assert.False(ImageLoader.AcceptsResponse(200, "text/html", 10));
        Assert.False(ImageLoader.AcceptsResponse(200, "application/json", 10));
        Assert.False(ImageLoader.AcceptsResponse(302, "image/png", 10));
        Assert.False(ImageLoader.AcceptsResponse(429, "image/png", 10));
        Assert.False(ImageLoader.AcceptsResponse(200, "image/png", (long)ImageLoader.MaxBytes + 1));
        Assert.Null(ImageLoader.Downsample(new byte[ImageLoader.MaxBytes + 1], 10));
    }
}
