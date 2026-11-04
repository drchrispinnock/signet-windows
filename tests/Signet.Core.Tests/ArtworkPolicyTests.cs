using Signet.Models;
using Signet.Services;

namespace Signet.Tests;

/// <summary>
/// S13: artwork URLs come from token metadata, so only public HTTPS destinations may be contacted
/// and an IPFS path may not climb out of the gateway's <c>/ipfs/</c>. (Upstream's host classification
/// and image-response tests are with the ImageLoader port, still to come.)
/// </summary>
public class ArtworkPolicyTests
{
    private const string Cid = "QmNrhZHUaEqxhyLfqoq1mtHSipkWHeT31LNHb1QEbDHgnc";

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
}
