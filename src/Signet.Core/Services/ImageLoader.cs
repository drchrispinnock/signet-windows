using System.Buffers.Binary;
using System.Text;
using Signet.Models;

namespace Signet.Services;

/// <summary>
/// Downloads an image from the first URL that works and downsamples it for display.
/// Thumbnails are cached in memory, and the production loader keeps them on disk too
/// (<c>ArtworkCache</c>), so artwork is fetched once, not on every launch.
///
/// Artwork URLs come from token metadata, so they are untrusted (S13): only public HTTPS
/// destinations are contacted (checked by name and again after DNS resolution), redirects
/// may only lead to other public HTTPS destinations, the response must be an image, and the
/// download is cut off at <c>MaxBytes</c> while it streams. At most <c>MaxConcurrent</c> loads run at once.
///
/// Windows: Core has no image codec, so the loader deals in encoded thumbnails (<c>Thumbnail</c>,
/// a PNG or an SVG document) and the app installs the decoder that makes them
/// (<c>Downsample</c>, Windows Imaging Component) and turns them into a picture on screen.
/// </summary>
public sealed class ImageLoader
{
    private static ImageLoader? shared;
    /// <summary>The app's loader, on the standard disk cache.</summary>
    public static ImageLoader Shared => shared ??= new ImageLoader(disk: ArtworkCache.Standard);

    /// <summary>The most a single artwork may download; a thumbnail never needs more.</summary>
    public const int MaxBytes = 8 << 20;
    public const int MaxConcurrent = 6;

    /// <summary>A downsampled image ready to show: PNG bytes (or an SVG document) and its size in pixels (0 for an SVG).</summary>
    public sealed record Thumbnail(byte[] Data, int Width, int Height)
    {
        public bool IsSVG => Width == 0 && Height == 0;

        /// <summary>A stored thumbnail read back: a PNG by its header, or an SVG document.</summary>
        public static Thumbnail? From(byte[] data)
        {
            if (PNGSize(data) is var (width, height)) return new Thumbnail(data, width, height);
            return LooksLikeSVG(data) ? new Thumbnail(data, 0, 0) : null;
        }
    }

    /// <summary>What one download attempt came to.</summary>
    public abstract record Fetch
    {
        public sealed record DataResult(byte[] Bytes) : Fetch;
        /// <summary>
        /// The content is larger than <c>MaxBytes</c>: it will be on every gateway, so the other
        /// URLs naming the same IPFS content are not worth a request.
        /// </summary>
        public sealed record TooLarge : Fetch;
        /// <summary>Refused, unreachable, rate-limited or not an image: the next URL may still work.</summary>
        public sealed record Unavailable : Fetch;
    }

    /// <summary>
    /// Decodes at most <c>maxPixelSize</c> on the long edge and encodes the result as a PNG, or null
    /// when the data is not an image. The app installs Windows Imaging Component here; without it
    /// (tests) a PNG passes through as it is and an SVG document as itself.
    /// </summary>
    public static Func<byte[], int, Thumbnail?> Decoder { get; set; } = (data, _) => Thumbnail.From(data);

    private readonly Dictionary<string, Thumbnail> cache = [];
    private readonly LinkedList<string> recent = [];
    private long cachedBytes;
    private const int CacheCount = 300;
    private const long CacheBytes = 96 << 20;
    private readonly ArtworkCache? disk;
    private bool pruned;
    private readonly Func<Uri, Task<Fetch>> fetcher;
    private readonly Dictionary<string, Task<Thumbnail?>> inFlight = [];
    private readonly SemaphoreSlim slots = new(MaxConcurrent);
    private readonly Lock gate = new();

    /// <summary>A loader with no disk cache (tests, previews) unless <c>disk</c> is given.</summary>
    public ImageLoader(ArtworkCache? disk = null, Func<Uri, Task<Fetch>>? fetch = null)
    {
        this.disk = disk;
        fetcher = fetch ?? FetchURL;
    }

    /// <summary>A loader whose downloads are answered by <c>download</c> (null is <c>Unavailable</c>); for tests.</summary>
    public static ImageLoader WithDownload(Func<Uri, Task<byte[]?>> download) =>
        new(fetch: async url => await download(url) is { } data ? new Fetch.DataResult(data) : new Fetch.Unavailable());

    /// <summary>
    /// Tries <c>candidates</c> in order; returns the first that yields a decodable image. The
    /// candidates are a token's images best first, one URL per gateway for each (display
    /// render, then thumbnail, then artifact): when a gateway says the content is too large,
    /// every other gateway's copy of that content is skipped and the next image is tried.
    /// The disk cache is consulted in that same order, candidate by candidate, so a cached
    /// lesser image never stands in for a better one that was never fetched: marketplaces
    /// give many tokens one generic thumbnail, and once it is on disk it must not become
    /// every such token's tile.
    /// </summary>
    public Task<Thumbnail?> ImageFor(IReadOnlyList<Uri> candidates, int maxPixelSize = 400)
    {
        if (candidates.Count == 0) return Task.FromResult<Thumbnail?>(null);
        maxPixelSize = Math.Clamp(maxPixelSize, 1, 2048);
        var key = $"{candidates[0].AbsoluteString()}|{maxPixelSize}";
        lock (gate)
        {
            if (cache.TryGetValue(key, out var cached))
            {
                recent.Remove(key);
                recent.AddFirst(key);
                return Task.FromResult<Thumbnail?>(cached);
            }
            if (inFlight.TryGetValue(key, out var running)) return running;
            if (inFlight.Count >= 128 || candidates.Count > 16) return Task.FromResult<Thumbnail?>(null);
            var task = Load(candidates, maxPixelSize, key);
            if (!task.IsCompleted) inFlight[key] = task;
            return task;
        }
    }

    private async Task<Thumbnail?> Load(IReadOnlyList<Uri> candidates, int maxPixelSize, string key)
    {
        Thumbnail? image = null;
        try
        {
            await slots.WaitAsync().ConfigureAwait(false);
            // Disk reads and decoding stay off the caller's (UI) thread.
            try { image = await Task.Run(() => Candidates(candidates, maxPixelSize)).ConfigureAwait(false); }
            finally { slots.Release(); }
        }
        finally
        {
            lock (gate)
            {
                inFlight.Remove(key);
                if (image is not null) Remember(key, image);
            }
        }
        return image;
    }

    private async Task<Thumbnail?> Candidates(IReadOnlyList<Uri> candidates, int maxPixelSize)
    {
        var tooLarge = new List<IPFS.ContentPath>();
        foreach (var url in candidates)
        {
            if (!EndpointPolicy.IsPublicHTTPS(url)) continue;
            // This image already on disk, from whichever gateway, needs no request.
            if (disk?.ImageFor(url, maxPixelSize) is { } stored) return stored;
            var content = IPFS.GatewayContentPath(url);
            if (content is not null && tooLarge.Contains(content)) continue;
            if (disk?.IsTooLarge(url) == true) continue;
            switch (await fetcher(url).ConfigureAwait(false))
            {
                case Fetch.DataResult { Bytes: var data }:
                    if (Downsample(data, maxPixelSize) is { } image)
                    {
                        Store(image, url, maxPixelSize);
                        return image;
                    }
                    break;
                case Fetch.TooLarge:
                    if (content is not null) tooLarge.Add(content);
                    disk?.MarkTooLarge(url);
                    break;
            }
        }
        return null;
    }

    /// <summary>Writes a fetched thumbnail to the disk cache, pruning the folder first once per process.</summary>
    private void Store(Thumbnail image, Uri url, int maxPixelSize)
    {
        if (disk is null) return;
        lock (gate)
        {
            if (!pruned) { pruned = true; disk.Prune(); }
        }
        disk.Store(image, url, maxPixelSize);
    }

    private void Remember(string key, Thumbnail image)
    {
        if (cache.Remove(key, out var old)) { cachedBytes -= old.Data.Length; recent.Remove(key); }
        cache[key] = image;
        cachedBytes += image.Data.Length;
        recent.AddFirst(key);
        while (recent.Count > CacheCount || cachedBytes > CacheBytes)
        {
            var last = recent.Last!.Value;
            recent.RemoveLast();
            if (cache.Remove(last, out var dropped)) cachedBytes -= dropped.Data.Length;
        }
    }

    // Bounded download

    /// <summary>Is this response worth reading? 2xx, an image (or SVG) and not declared larger than the limit.</summary>
    public static bool AcceptsResponse(int status, string? contentType, long? contentLength, int maxBytes = MaxBytes)
    {
        if (status is < 200 or >= 300) return false;
        if (contentLength is { } length && length > maxBytes) return false;
        var type = (contentType ?? "").ToLowerInvariant().Split(';')[0].Trim();
        // Gateways sometimes serve artwork as octet-stream; the decoder is the final judge then.
        return type.StartsWith("image/", StringComparison.Ordinal) || type is "application/octet-stream" or "application/xml" or "text/xml" or "";
    }

    /// <summary>
    /// Downloads <c>url</c> through the pinned public client; <c>Unavailable</c> on any refusal or
    /// failure, <c>TooLarge</c> when the content passes <c>MaxBytes</c> (declared, or while it streams).
    /// </summary>
    private static async Task<Fetch> FetchURL(Uri url)
    {
        try
        {
            var reply = await PublicHTTPClient.Data(url, MaxBytes, followRedirects: true).ConfigureAwait(false);
            reply.Headers.TryGetValue("content-type", out var type);
            return AcceptsResponse(reply.Status, type, reply.Data.Length) ? new Fetch.DataResult(reply.Data) : new Fetch.Unavailable();
        }
        catch (PublicHTTPClient.TooLargeError) { return new Fetch.TooLarge(); }
        catch (Exception) { return new Fetch.Unavailable(); }
    }

    /// <summary>Decodes at most <c>maxPixelSize</c> on the long edge so a grid of large artworks stays cheap.</summary>
    public static Thumbnail? Downsample(byte[] data, int maxPixelSize)
    {
        if (data.Length > MaxBytes) return null;
        try { return Decoder(data, maxPixelSize); }
        catch (Exception) { return null; }
    }

    /// <summary>The width and height in a PNG's header, or null if <c>data</c> is not a PNG.</summary>
    public static (int Width, int Height)? PNGSize(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
        if (data.Length < 24 || !data[..8].SequenceEqual(signature) || !data[12..16].SequenceEqual("IHDR"u8)) return null;
        var width = BinaryPrimitives.ReadInt32BigEndian(data[16..20]);
        var height = BinaryPrimitives.ReadInt32BigEndian(data[20..24]);
        return width > 0 && height > 0 ? (width, height) : null;
    }

    /// <summary>An SVG document by its first 512 bytes (upstream's <c>svg(_:)</c> test).</summary>
    public static bool LooksLikeSVG(byte[] data)
    {
        var head = Encoding.UTF8.GetString(data, 0, Math.Min(512, data.Length)).ToLowerInvariant();
        return head.Contains("<svg") || head.Contains("<?xml");
    }
}
