using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Signet.App.Services;

/// <summary>
/// Loads remote artwork (avatars, token logos, NFT thumbnails) into an <c>Image</c>, trying each URL
/// in turn: IPFS content comes as one URL per gateway, best first.
///
/// Windows: a first cut of upstream's <c>ImageLoader</c>. WinUI's <c>BitmapImage</c> fetches, decodes
/// (WebP included) and downsamples to the decode size. Not ported yet: <c>ArtworkCache</c> on disk,
/// the 8 MB "too large" skip, and the public-endpoint policy (<c>PublicHTTPClient</c>).
/// </summary>
public static class ImageLoader
{
    /// <summary>
    /// Shows the first of <c>urls</c> that loads in <c>image</c>; <c>onResult</c> says whether one did, so the
    /// caller can swap its placeholder. A later call on the same image wins.
    /// </summary>
    public static void Load(Image image, IReadOnlyList<Uri> urls, int decodePixels, Action<bool>? onResult = null)
    {
        var token = new object();
        image.Tag = token;
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
            var bitmap = new BitmapImage { DecodePixelWidth = decodePixels, DecodePixelType = DecodePixelType.Logical };
            bitmap.ImageOpened += (_, _) => { if (image.Tag == token) onResult?.Invoke(true); };
            bitmap.ImageFailed += (_, _) => Try(index + 1);
            bitmap.UriSource = urls[index];
            image.Source = bitmap;
        }
    }
}
