using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Thumbnail = Signet.Services.ImageLoader.Thumbnail;

namespace Signet.App.Services;

/// <summary>
/// Puts remote artwork (avatars, token logos, NFT thumbnails) into an <c>Image</c> through
/// Core's <c>Signet.Services.ImageLoader</c> (upstream's loader: public HTTPS only, the 8 MB cut-off,
/// six at a time, the on-disk <c>ArtworkCache</c>), which tries each URL in turn: IPFS content
/// comes as one URL per gateway, best first.
///
/// Windows-only glue (upstream's views await <c>ImageLoader.shared.image(for:)</c> and show the
/// NSImage). It installs Windows Imaging Component as the loader's decoder (any format WIC
/// reads, WebP included, downsampled and encoded as PNG; an SVG document is kept and shown
/// with <c>SvgImageSource</c>), and keeps the pictures it made in memory so a row drawn again shows
/// its picture at once instead of blanking while it reloads.
/// </summary>
public static class ArtworkImages
{
    /// <summary>Most recently used pictures, by first URL and decode size. UI thread only.</summary>
    private static readonly Dictionary<(Uri Url, int Pixels), ImageSource> Cache = [];
    private static readonly LinkedList<(Uri Url, int Pixels)> Recent = [];
    private const int CacheLimit = 300;

    static ArtworkImages() => Signet.Services.ImageLoader.Decoder = Decode;

    /// <summary>
    /// Shows the first of <c>urls</c> that loads in <c>image</c>; <c>onResult</c> says whether one did, so the
    /// caller can swap its placeholder. A later call on the same image wins. A picture already in
    /// memory is shown and reported straight away.
    /// </summary>
    public static async void Load(Image image, IReadOnlyList<Uri> urls, int decodePixels, Action<bool>? onResult = null)
    {
        var token = new object();
        image.Tag = token;
        if (urls.Count == 0)
        {
            image.Source = null;
            onResult?.Invoke(false);
            return;
        }
        var key = (urls[0], decodePixels);
        if (Cache.TryGetValue(key, out var cached))
        {
            Touch(key);
            image.Source = cached;
            onResult?.Invoke(true);
            return;
        }
        image.Source = null;
        ImageSource? source = null;
        try
        {
            if (await Signet.Services.ImageLoader.Shared.ImageFor(urls, decodePixels) is { } thumbnail)
                source = await SourceFor(thumbnail);
        }
        catch (Exception) { source = null; }
        if (source is not null) Remember(key, source);
        if (image.Tag != token) return;
        image.Source = source;
        onResult?.Invoke(source is not null);
    }

    /// <summary>A thumbnail as something an <c>Image</c> can show. UI thread.</summary>
    private static async Task<ImageSource> SourceFor(Thumbnail thumbnail)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(thumbnail.Data.AsBuffer());
        stream.Seek(0);
        if (thumbnail.IsSVG)
        {
            var svg = new SvgImageSource();
            if (await svg.SetSourceAsync(stream) != SvgImageSourceLoadStatus.Success) throw new InvalidDataException("Not a usable SVG document.");
            return svg;
        }
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }

    /// <summary>
    /// The loader's decoder: at most <c>maxPixelSize</c> on the long edge, as a PNG; an SVG
    /// document passes through as itself. Runs on a pool thread.
    /// </summary>
    private static Thumbnail? Decode(byte[] data, int maxPixelSize)
    {
        try { return DecodeAsync(data, maxPixelSize).GetAwaiter().GetResult(); }
        catch (Exception) when (Signet.Services.ImageLoader.LooksLikeSVG(data)) { return new Thumbnail(data, 0, 0); }
        catch (Exception) { return null; }
    }

    private static async Task<Thumbnail> DecodeAsync(byte[] data, int maxPixelSize)
    {
        using var input = new InMemoryRandomAccessStream();
        await input.WriteAsync(data.AsBuffer());
        input.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(input);
        // The transform scales the stored frame; orientation is applied after it.
        double width = decoder.PixelWidth, height = decoder.PixelHeight;
        var scale = Math.Min(1.0, maxPixelSize / Math.Max(width, height));
        var scaledWidth = (uint)Math.Max(1, Math.Round(width * scale));
        var scaledHeight = (uint)Math.Max(1, Math.Round(height * scale));
        var transform = new BitmapTransform { ScaledWidth = scaledWidth, ScaledHeight = scaledHeight, InterpolationMode = BitmapInterpolationMode.Fant };
        var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        var rotated = decoder.OrientedPixelWidth != decoder.PixelWidth;
        var (outWidth, outHeight) = rotated ? (scaledHeight, scaledWidth) : (scaledWidth, scaledHeight);
        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, outWidth, outHeight, 96, 96, pixels.DetachPixelData());
        await encoder.FlushAsync();
        var png = new byte[output.Size];
        output.Seek(0);
        await output.ReadAsync(png.AsBuffer(), (uint)png.Length, InputStreamOptions.None);
        return new Thumbnail(png, (int)outWidth, (int)outHeight);
    }

    private static void Remember((Uri, int) key, ImageSource source)
    {
        Cache[key] = source;
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
