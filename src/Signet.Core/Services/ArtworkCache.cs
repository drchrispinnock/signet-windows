using System.Security.Cryptography;
using System.Text;
using Signet.Models;

namespace Signet.Services;

/// <summary>
/// Thumbnails kept on disk between launches, already downsampled, so an account's artwork is
/// fetched from the gateways once rather than on every launch and account switch.
///
/// IPFS content is named by its CID, so an entry keyed by the content path never goes stale
/// and answers for every gateway's URL of that content; artwork at any other HTTPS URL can
/// change behind the URL, so those entries expire after <c>MutableLifetime</c>. Content a gateway
/// reported too large (<c>ImageLoader.Fetch.TooLarge</c>) is remembered as well, so it is skipped
/// without a request next time. Entries are written with <c>PrivateFile</c> (atomic replacement),
/// the folder is capped at <c>MaxBytes</c> with the oldest entries removed first, and nothing here
/// is ever a secret.
///
/// Windows: the folder is <c>%LOCALAPPDATA%\Signet\artwork</c> (upstream
/// <c>~/Library/Caches/Signet/artwork</c>), outside the wallet directory and its backups. An entry
/// holds whatever <c>ImageLoader.Downsample</c> produced: a PNG, or an SVG document kept as it came
/// (Windows renders SVG from its source, where AppKit rasterises it). Core has no image codec, so
/// the size check on store reads the PNG header.
/// </summary>
public sealed record ArtworkCache(string Directory)
{
    /// <summary>The folder's size cap; pruning brings it down to four fifths of this.</summary>
    public long MaxBytes { get; init; } = 256 << 20;
    /// <summary>How long an entry for a mutable (non-IPFS) URL is served.</summary>
    public TimeSpan MutableLifetime { get; init; } = TimeSpan.FromDays(7);
    /// <summary>How long "too large" is remembered; the cap could change with a release.</summary>
    public TimeSpan TooLargeLifetime { get; init; } = TimeSpan.FromDays(30);
    /// <summary>The largest decoded image worth writing (an SVG can declare any size).</summary>
    public const int MaxStoredPixels = 2048 * 2048;

    /// <summary>
    /// The production cache. (Upstream returns nil under the test host, S24; here only the app
    /// reaches it, through <c>ImageLoader.Shared</c>, and the tests build their own.)
    /// </summary>
    public static ArtworkCache Standard { get; } =
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Signet", "artwork"));

    /// <summary>What an entry is keyed on.</summary>
    public abstract record Key
    {
        /// <summary>IPFS content, by its <c>&lt;cid&gt;[/path]</c>: the same on every gateway, and immutable.</summary>
        public sealed record Content(string Path) : Key;
        /// <summary>Anything else, by the exact URL.</summary>
        public sealed record Url(string Text) : Key;

        public bool IsImmutable => this is Content;
        internal string KeyText => this switch
        {
            Content content => "ipfs:" + content.Path,
            Url url => "url:" + url.Text,
            _ => throw new InvalidOperationException(),
        };
    }

    public static Key KeyFor(Uri url) =>
        IPFS.GatewayContentPath(url) is { } path ? new Key.Content(path.Encoded) : new Key.Url(url.AbsoluteString());

    // Entries

    /// <summary>The cached thumbnail for <c>url</c> at <c>maxPixelSize</c>, if there is a live one.</summary>
    public ImageLoader.Thumbnail? ImageFor(Uri url, int maxPixelSize)
    {
        var key = KeyFor(url);
        var file = ImageFile(key, maxPixelSize);
        if (!IsLive(file, key.IsImmutable ? null : MutableLifetime)) return null;
        byte[]? data;
        try { data = PrivateFile.Read(file); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        return data is null ? null : ImageLoader.Thumbnail.From(data);
    }

    /// <summary>
    /// Writes <c>image</c> as the thumbnail for <c>url</c> at <c>maxPixelSize</c>. Failures are silent:
    /// the cache is a convenience and the image is already on screen.
    /// </summary>
    public void Store(ImageLoader.Thumbnail image, Uri url, int maxPixelSize)
    {
        if ((long)image.Width * image.Height > MaxStoredPixels) return;
        try { PrivateFile.Write(ImageFile(KeyFor(url), maxPixelSize), image.Data); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Whether <c>url</c>'s content was reported too large recently enough to still skip it.</summary>
    public bool IsTooLarge(Uri url) => IsLive(TooLargeFile(KeyFor(url)), TooLargeLifetime);

    public void MarkTooLarge(Uri url)
    {
        try { PrivateFile.Write(TooLargeFile(KeyFor(url)), []); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Removes expired entries, leftover temporaries and then, while the folder is over
    /// <c>MaxBytes</c>, the oldest entries. Cheap enough to run once per launch.
    /// </summary>
    public void Prune()
    {
        FileInfo[] files;
        try { files = new DirectoryInfo(Directory).GetFiles(); }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        var live = new List<FileInfo>();
        foreach (var file in files)
        {
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            var name = file.Name;
            TimeSpan? lifetime;
            if (name.Contains(PrivateFile.TemporarySuffix, StringComparison.Ordinal)) lifetime = TimeSpan.FromHours(1);
            else if (name.StartsWith(TooLargePrefix, StringComparison.Ordinal)) lifetime = TooLargeLifetime;
            else if (name.StartsWith(MutablePrefix, StringComparison.Ordinal)) lifetime = MutableLifetime;
            else if (name.StartsWith(ImmutablePrefix, StringComparison.Ordinal)) lifetime = null;
            else continue;
            if (lifetime is { } span && DateTime.UtcNow - file.LastWriteTimeUtc > span)
            {
                try { file.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            else live.Add(file);
        }
        var total = live.Sum(f => f.Length);
        if (total <= MaxBytes) return;
        foreach (var entry in live.OrderBy(f => f.LastWriteTimeUtc))
        {
            if (total <= MaxBytes * 4 / 5) break;
            var length = entry.Length;
            try { entry.Delete(); total -= length; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // Files

    private const string ImmutablePrefix = "i-";
    private const string MutablePrefix = "u-";
    private const string TooLargePrefix = "x-";

    private string ImageFile(Key key, int maxPixelSize) =>
        Path.Combine(Directory, (key.IsImmutable ? ImmutablePrefix : MutablePrefix) + Digest($"{key.KeyText}|{maxPixelSize}") + ".png");

    private string TooLargeFile(Key key) => Path.Combine(Directory, TooLargePrefix + Digest(key.KeyText));

    private static string Digest(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>A regular file that exists and, given a lifetime, is younger than it.</summary>
    private static bool IsLive(string file, TimeSpan? lifetime)
    {
        var info = new FileInfo(file);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
        return lifetime is not { } span || DateTime.UtcNow - info.LastWriteTimeUtc <= span;
    }
}
