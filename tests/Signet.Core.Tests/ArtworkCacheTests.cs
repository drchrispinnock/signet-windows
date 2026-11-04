using System.Buffers.Binary;
using System.IO.Compression;
using Signet.Models;
using Signet.Services;

namespace Signet.Tests;

/// <summary>
/// The on-disk thumbnail cache: keyed by IPFS content across gateways, expiring for mutable
/// URLs, remembering oversized content, pruned by age and size, and feeding <c>ImageLoader</c>.
/// (Core has no codec: the loader's default decoder passes a PNG through, which is all these
/// tests need; upstream's 0600/0700 permission checks are dropped, see <c>TestSupport.cs</c>.)
/// </summary>
public class ArtworkCacheTests
{
    private const string Cid = "QmNrhZHUaEqxhyLfqoq1mtHSipkWHeT31LNHb1QEbDHgnc";
    private const string ThumbCID = "QmXxvrULyrWH18e6kUinPNXt5PAwv6h7MPcmWbSw4DwDuP";

    private static ArtworkCache TemporaryCache() =>
        new(Path.Combine(Path.GetTempPath(), "signet-artwork-" + Guid.NewGuid().ToString("N")));

    private static void Remove(ArtworkCache cache)
    {
        try { Directory.Delete(cache.Directory, recursive: true); } catch (IOException) { }
    }

    /// <summary>A solid green PNG of the given size.</summary>
    internal static byte[] SamplePNG(int width = 40, int height = 20)
    {
        var raw = new byte[height * (1 + width * 4)];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var at = y * (1 + width * 4) + 1 + x * 4;
                raw[at + 1] = 0xFF;
                raw[at + 3] = 0xFF;
            }
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true)) zlib.Write(raw);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; header[9] = 6;
        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();

        static void Chunk(Stream stream, string type, byte[] data)
        {
            var length = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
            stream.Write(length);
            var body = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            stream.Write(body);
            var crc = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(body));
            stream.Write(crc);
        }

        static uint Crc32(byte[] data)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var b in data)
            {
                crc ^= b;
                for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
            return ~crc;
        }
    }

    [Fact]
    public void KeysIPFSContentByPathAndOtherURLsByURL()
    {
        var filebase = new Uri($"https://ipfs.filebase.io/ipfs/{Cid}/a.png");
        var pinata = new Uri($"https://gateway.pinata.cloud/ipfs/{Cid}/a.png");
        var subdomain = new Uri($"https://{Cid}.ipfs.dweb.link/a.png");
        Assert.Equal(new ArtworkCache.Key.Content($"{Cid}/a.png"), ArtworkCache.KeyFor(filebase));
        Assert.Equal(ArtworkCache.KeyFor(filebase), ArtworkCache.KeyFor(pinata));
        Assert.Equal(ArtworkCache.KeyFor(filebase), ArtworkCache.KeyFor(subdomain));
        var avatar = new Uri("https://services.tzkt.io/v1/avatars/tz1a");
        Assert.Equal(new ArtworkCache.Key.Url(avatar.AbsoluteString()), ArtworkCache.KeyFor(avatar));
        Assert.False(ArtworkCache.KeyFor(avatar).IsImmutable);
    }

    [Fact]
    public void ServesAStoredThumbnailForEveryGatewayOfTheContent()
    {
        var cache = TemporaryCache();
        try
        {
            var image = ImageLoader.Downsample(SamplePNG(), 100)!;
            var urls = IPFS.CandidateURLs($"ipfs://{Cid}");
            cache.Store(image, urls[1], 100);
            foreach (var url in urls)
            {
                var hit = cache.ImageFor(url, 100)!;
                Assert.True(hit.Width == 40 && hit.Height == 20);
            }
            // Another size is another entry; other content is a miss.
            Assert.Null(cache.ImageFor(urls[0], 50));
            Assert.Null(cache.ImageFor(IPFS.CandidateURLs($"ipfs://{ThumbCID}")[0], 100));
        }
        finally { Remove(cache); }
    }

    [Fact]
    public void MutableURLsExpireAndContentDoesNot()
    {
        var cache = TemporaryCache();
        try
        {
            var image = ImageLoader.Downsample(SamplePNG(), 100)!;
            var avatar = new Uri("https://services.tzkt.io/v1/avatars/tz1a");
            var content = IPFS.CandidateURLs($"ipfs://{Cid}")[0];
            cache.Store(image, avatar, 100);
            cache.Store(image, content, 100);
            Assert.NotNull(cache.ImageFor(avatar, 100));
            // The same folder read with a lifetime already passed.
            var expired = cache with { MutableLifetime = TimeSpan.FromSeconds(-1) };
            Assert.Null(expired.ImageFor(avatar, 100));
            Assert.NotNull(expired.ImageFor(content, 100));
            expired.Prune();
            Assert.Single(Directory.GetFiles(cache.Directory));
        }
        finally { Remove(cache); }
    }

    [Fact]
    public void RemembersContentTooLargeAcrossInstances()
    {
        var cache = TemporaryCache();
        try
        {
            var urls = IPFS.CandidateURLs($"ipfs://{Cid}");
            Assert.False(cache.IsTooLarge(urls[0]));
            cache.MarkTooLarge(urls[0]);
            var again = new ArtworkCache(cache.Directory);
            Assert.True(again.IsTooLarge(urls[2]));
            Assert.False(again.IsTooLarge(IPFS.CandidateURLs($"ipfs://{ThumbCID}")[0]));
            var expired = again with { TooLargeLifetime = TimeSpan.FromSeconds(-1) };
            Assert.False(expired.IsTooLarge(urls[0]));
        }
        finally { Remove(cache); }
    }

    [Fact]
    public void PrunesTheOldestEntriesPastTheCap()
    {
        var cache = TemporaryCache();
        try
        {
            var image = ImageLoader.Downsample(SamplePNG(200, 200), 200)!;
            var size = image.Data.Length;
            var cids = new[] { "a", "b", "c", "d" }.Select(s => $"ipfs://bafybeigdyrzt5sfp7udm7hu76uh7y26nf3efuylqabf3oclgtqy55fbzd{s}").ToList();
            var seen = new HashSet<string>();
            for (var index = 0; index < cids.Count; index++)
            {
                var url = IPFS.CandidateURLs(cids[index])[0];
                cache.Store(image, url, 200);
                // Give each entry its own modification time, oldest first.
                var names = Directory.GetFiles(cache.Directory).ToHashSet();
                var added = names.Except(seen).Single();
                seen = names;
                File.SetLastWriteTimeUtc(added, DateTime.UtcNow.AddMinutes(index - 10));
            }
            (cache with { MaxBytes = size * 2 }).Prune();
            var kept = cids.Where(c => cache.ImageFor(IPFS.CandidateURLs(c)[0], 200) is not null).ToList();
            Assert.True(kept.Count <= 2);
            Assert.Contains(cids[3], kept);
            Assert.DoesNotContain(cids[0], kept);
        }
        finally { Remove(cache); }
    }

    [Fact]
    public async Task ALoaderWithTheCacheFetchesOnceAndSkipsKnownOversizedContent()
    {
        var cache = TemporaryCache();
        try
        {
            var png = SamplePNG();
            var urls = IPFS.CandidateURLs($"ipfs://{Cid}").Concat(IPFS.CandidateURLs($"ipfs://{ThumbCID}")).ToList();
            var log = new List<Uri>();
            ImageLoader Loader() => new(disk: cache, fetch: url =>
            {
                lock (log) log.Add(url);
                return Task.FromResult<ImageLoader.Fetch>(url.AbsolutePath.Contains(Cid) ? new ImageLoader.Fetch.TooLarge() : new ImageLoader.Fetch.DataResult(png));
            });
            Assert.Equal(40, (await Loader().ImageFor(urls, 100))?.Width);
            Assert.Equal(2, log.Count);
            // A new loader (a new launch) has the thumbnail on disk and asks nothing.
            Assert.Equal(40, (await Loader().ImageFor(urls, 100))?.Width);
            Assert.Equal(2, log.Count);
            // Another size is not on disk, but the oversized display render is still skipped.
            Assert.Equal(40, (await Loader().ImageFor(urls, 50))?.Width);
            Assert.Equal(3, log.Count);
            Assert.Contains(ThumbCID, log[^1].AbsolutePath);
        }
        finally { Remove(cache); }
    }

    /// <summary>
    /// Marketplaces give many tokens one generic thumbnail. Once another token has put it on
    /// disk, a token whose own display render was never fetched must still get that render,
    /// not the cached placeholder (seen live: hic et nunc's black disc on two objkt tiles).
    /// </summary>
    [Fact]
    public async Task ACachedSharedThumbnailDoesNotStandInForAnUnfetchedDisplayRender()
    {
        var cache = TemporaryCache();
        try
        {
            var display = SamplePNG(60, 30);
            var thumbnail = SamplePNG(40, 20);
            var thumbURLs = IPFS.CandidateURLs($"ipfs://{ThumbCID}");
            var log = new List<Uri>();
            ImageLoader Loader() => new(disk: cache, fetch: url =>
            {
                lock (log) log.Add(url);
                return Task.FromResult<ImageLoader.Fetch>(new ImageLoader.Fetch.DataResult(url.AbsolutePath.Contains(ThumbCID) ? thumbnail : display));
            });
            // Some token's tile was the shared thumbnail alone, so it is now on disk.
            Assert.Equal(40, (await Loader().ImageFor(thumbURLs, 100))?.Width);
            Assert.Single(log);
            // Another token: its own display render first, then that same thumbnail.
            var urls = IPFS.CandidateURLs($"ipfs://{Cid}").Concat(thumbURLs).ToList();
            Assert.Equal(60, (await Loader().ImageFor(urls, 100))?.Width);   // the display render, fetched now, not the cached placeholder
            Assert.True(log.Count == 2 && log[^1].AbsolutePath.Contains(Cid));
            // From then on the render is on disk and nothing is asked.
            Assert.Equal(60, (await Loader().ImageFor(urls, 100))?.Width);
            Assert.Equal(2, log.Count);
        }
        finally { Remove(cache); }
    }
}
