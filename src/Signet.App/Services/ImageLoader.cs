using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Signet.App.Services;

/// <summary>
/// Loads remote artwork (avatars, token logos, NFT thumbnails) into an <c>Image</c>, trying each URL
/// in turn: IPFS content comes as one URL per gateway, best first.
///
/// Windows: a first cut of upstream's <c>ImageLoader</c>. WinUI's <c>BitmapImage</c> fetches, decodes
/// (WebP included) and downsamples to the decode size. Decoded images are kept in memory, like
/// upstream's in-memory cache, so a row that is drawn again shows its picture at once instead of
/// blanking while it reloads. Not ported yet: <c>ArtworkCache</c> on disk, the 8 MB "too large" skip,
/// and the public-endpoint policy (<c>PublicHTTPClient</c>).
/// </summary>
public static class ImageLoader
{
    /// <summary>Most recently used decoded images, by URL and decode size. UI thread only.</summary>
    private static readonly Dictionary<(Uri Url, int Pixels), BitmapImage> Cache = [];
    private static readonly LinkedList<(Uri Url, int Pixels)> Recent = [];
    private const int CacheLimit = 300;

    /// <summary>
    /// Shows the first of <c>urls</c> that loads in <c>image</c>; <c>onResult</c> says whether one did, so the
    /// caller can swap its placeholder. A later call on the same image wins. A cached image is shown
    /// and reported straight away.
    /// </summary>
    public static void Load(Image image, IReadOnlyList<Uri> urls, int decodePixels, Action<bool>? onResult = null)
    {
        var token = new object();
        image.Tag = token;
        foreach (var url in urls)
        {
            if (!Cache.TryGetValue((url, decodePixels), out var cached)) continue;
            Touch((url, decodePixels));
            image.Source = cached;
            onResult?.Invoke(true);
            return;
        }
        image.Source = null;
        Try(0);

        void Try(int index)
        {
            if (image.Tag != token) return;
            if (index >= urls.Count)
            {
                onResult?.Invoke(false);
                return;
            }
            var key = (urls[index], decodePixels);
            var bitmap = new BitmapImage { DecodePixelWidth = decodePixels, DecodePixelType = DecodePixelType.Logical };
            bitmap.ImageOpened += (_, _) =>
            {
                Remember(key, bitmap);
                if (image.Tag == token) onResult?.Invoke(true);
            };
            bitmap.ImageFailed += (_, _) => Try(index + 1);
            bitmap.UriSource = urls[index];
            image.Source = bitmap;
        }
    }

    private static void Remember((Uri, int) key, BitmapImage bitmap)
    {
        Cache[key] = bitmap;
        Touch(key);
        while (Recent.Count > CacheLimit)
        {
            Cache.Remove(Recent.Last!.Value);
            Recent.RemoveLast();
        }
    }

    private static void Touch((Uri, int) key)
    {
        Recent.Remove(key);
        Recent.AddFirst(key);
    }
}
