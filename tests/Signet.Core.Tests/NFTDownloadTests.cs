using System.Text;
using Signet.Models;
using Signet.Services;

namespace Signet.Tests;

/// <summary>NFT Download (0.9.13): the file name a token gets, the gateway fallback, and the refusals.</summary>
public class NFTDownloadTests
{
    private const string Contract = "KT1RJ6PbjHpwc3M5rw5s2Nbmefwbuwbdxton";

    private static NFT Nft(string name = "Piece", string[]? artifacts = null, string[]? images = null, string? mime = null) =>
        new($"{Contract}:4", Contract, "4", name, 1, (images ?? []).Select(u => new Uri(u)).ToList())
        {
            ArtifactURLs = (artifacts ?? []).Select(u => new Uri(u)).ToList(),
            ArtifactMime = mime,
        };

    private static string TemporaryDirectory() => Path.Combine(Path.GetTempPath(), $"signet-nft-download-{Guid.NewGuid()}");

    [Fact]
    public void NamesTheFileAfterTheTokenAndItsType()
    {
        var token = Nft("  Sun/Moon: a study\n of light ", mime: "video/mp4");
        Assert.Equal("Sun-Moon- a study  of light", NFTDownloader.Sanitised(token.Name));
        Assert.True(NFTDownloader.Sanitised("...") == "NFT" && NFTDownloader.Sanitised("") == "NFT");
        Assert.Equal(80, NFTDownloader.Sanitised(new string('x', 200)).Length);
        // Windows-only: a name with characters Windows refuses in a file name keeps none of them.
        Assert.Equal("a -quote- -b-", NFTDownloader.Sanitised("a \"quote\" <b>"));
        // The gateway's content type wins, then the metadata's, then the URL, then nothing.
        Assert.Equal("png", NFTDownloader.FileExtension("image/png; charset=binary", "video/mp4", null));
        Assert.Equal("mp4", NFTDownloader.FileExtension("application/octet-stream", "video/mp4", null));
        Assert.Equal("glb", NFTDownloader.FileExtension(null, null, new Uri("https://ipfs.io/ipfs/Qm/piece.GLB")));
        Assert.Equal("bin", NFTDownloader.FileExtension(null, null, new Uri("https://ipfs.io/ipfs/QmHash")));
        Assert.Equal("html", NFTDownloader.FileExtension("text/html", null, null));
        Assert.Equal($"Sun-Moon- a study  of light ({Contract} 4).mp4", NFTDownloader.FileName(token, null, token.ArtifactMime, null));
    }

    [Fact]
    public async Task SavesTheArtifactFromTheFirstGatewayThatAnswers()
    {
        var directory = TemporaryDirectory();
        try
        {
            var asked = new List<Uri>();
            var downloader = new NFTDownloader(directory, (url, _) =>
            {
                lock (asked) asked.Add(url);
                if (url.Host == "ipfs.io") throw new HttpRequestException("cannot load from network");
                return Task.FromResult(new NFTDownloader.Fetched(Encoding.UTF8.GetBytes("GIF89a"), "image/gif"));
            });
            var token = Nft(artifacts: ["https://ipfs.io/ipfs/Qm1/art", "https://x.filebase.io/ipfs/Qm1/art"], images: ["https://ipfs.io/ipfs/Qm2"], mime: "image/png");
            var file = await downloader.Download(token, TestContext.Current.CancellationToken);
            Assert.Equal($"Piece ({Contract} 4).gif", Path.GetFileName(file));
            Assert.Equal(Path.GetFullPath(directory), Path.GetDirectoryName(Path.GetFullPath(file)));
            Assert.Equal(Encoding.UTF8.GetBytes("GIF89a"), File.ReadAllBytes(file));
            Assert.Equal(["ipfs.io", "x.filebase.io"], asked.Select(u => u.Host));
            // The display image stands in when there is no artifact, and a second download replaces the file.
            var still = Nft(images: ["https://x.filebase.io/ipfs/Qm2"], mime: "video/mp4");
            var again = await downloader.Download(still, TestContext.Current.CancellationToken);
            Assert.Equal($"Piece ({Contract} 4).gif", Path.GetFileName(again));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task RefusesWhatItCannotSave()
    {
        var directory = TemporaryDirectory();
        try
        {
            var asked = new List<Uri>();
            var downloader = new NFTDownloader(directory, (url, _) =>
            {
                lock (asked) asked.Add(url);
                throw url.AbsolutePath.Contains("big") ? new PublicHTTPClient.TooLargeError(NFTDownloader.MaxBytes) : new HttpRequestException("cannot load from network");
            });
            await Assert.ThrowsAsync<NFTDownloader.Failure.NoArtwork>(() => downloader.Download(Nft(), TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<NFTDownloader.Failure.Unavailable>(() => downloader.Download(Nft(artifacts: ["https://ipfs.io/ipfs/Qm1", "https://x.filebase.io/ipfs/Qm1"]), TestContext.Current.CancellationToken));
            Assert.Equal(2, asked.Count);
            // Too large on one gateway is too large everywhere: no second request.
            await Assert.ThrowsAsync<NFTDownloader.Failure.TooLarge>(() => downloader.Download(Nft(artifacts: ["https://ipfs.io/ipfs/big", "https://x.filebase.io/ipfs/big"]), TestContext.Current.CancellationToken));
            Assert.Equal(3, asked.Count);
            Assert.False(Directory.Exists(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
