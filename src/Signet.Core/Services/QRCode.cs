using QRCoder;
using ZXing;
using ZXing.Common;

namespace Signet.Services;

/// <summary>
/// QR codes: QRCoder stands in for Core Image's <c>qrCodeGenerator</c>, and ZXing.Net for its
/// detector (and for Vision on camera frames).
/// </summary>
public static class QRCode
{
    /// <summary>
    /// A crisp QR code for <c>text</c> as PNG bytes, black on white, about <c>size</c> pixels square.
    /// Modules are whole pixels (no smoothing), so the image is scaled with nearest-neighbour by the view.
    /// </summary>
    public static byte[] Png(string text, int size = 240, QRCodeGenerator.ECCLevel correction = QRCodeGenerator.ECCLevel.M)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, correction);
        // Core Image adds no quiet zone; the sheet draws a white margin around the image instead.
        var modules = data.ModuleMatrix.Count - 8;
        var pixelsPerModule = Math.Max(1, size / Math.Max(modules, 1));
        return new PngByteQRCode(data).GetGraphic(pixelsPerModule, drawQuietZones: false);
    }

    /// <summary>
    /// Windows-only: a picture in memory, as the app hands one over from a camera frame, the
    /// clipboard or an image file (Core has no codec; Windows Imaging Component decodes in the app).
    /// </summary>
    public sealed record Bitmap(byte[] Pixels, int Width, int Height, Bitmap.Layout Format)
    {
        /// <summary>One byte of grey per pixel, or Windows' usual four bytes of blue, green, red and alpha.</summary>
        public enum Layout { Gray8, Bgra32 }
    }

    /// <summary>
    /// A QR code for <c>text</c> as a grey bitmap about <c>size</c> pixels square, with its white
    /// quiet zone, or <c>null</c> when the text is too long for one code (a tz5 key is).
    /// Upstream's <c>image(for:size:correction:)</c>.
    /// </summary>
    public static Bitmap? Image(string text, int size = 240, QRCodeGenerator.ECCLevel correction = QRCodeGenerator.ECCLevel.M)
    {
        if (Matrix(text, correction) is not { } matrix) return null;
        var count = matrix.Count;
        var scale = Math.Max(1, size / count);
        var side = count * scale;
        var pixels = new byte[side * side];
        for (var y = 0; y < side; y++)
        {
            var row = matrix[y / scale];
            for (var x = 0; x < side; x++) pixels[y * side + x] = row[x / scale] ? (byte)0 : (byte)255;
        }
        return new Bitmap(pixels, side, side, Bitmap.Layout.Gray8);
    }

    /// <summary>
    /// The code's modules, quiet zone included (true is dark), or <c>null</c> when the text does
    /// not fit one QR code at this correction level.
    /// </summary>
    public static IReadOnlyList<bool[]>? Matrix(string text, QRCodeGenerator.ECCLevel correction = QRCodeGenerator.ECCLevel.M)
    {
        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(text, correction);
            return data.ModuleMatrix.Select(row =>
            {
                var bits = new bool[row.Length];
                for (var i = 0; i < row.Length; i++) bits[i] = row[i];
                return bits;
            }).ToList();
        }
        catch (QRCoder.Exceptions.DataTooLongException)
        {
            return null;
        }
    }

    /// <summary>Decodes the first QR code found in <c>image</c>.</summary>
    public static string? Decode(Bitmap image, bool thorough = true) => DecodeAll(image, thorough).FirstOrDefault();

    /// <summary>
    /// Every QR code found in <c>image</c>: a page of a recovery sheet can carry several.
    /// <c>thorough</c> spends longer looking, which an image or a screenshot deserves and a
    /// camera frame (another follows in a moment) does not.
    /// </summary>
    public static IReadOnlyList<string> DecodeAll(Bitmap image, bool thorough = true)
    {
        if (image.Width <= 0 || image.Height <= 0) return [];
        var bytesPerPixel = image.Format == Bitmap.Layout.Gray8 ? 1 : 4;
        if (image.Pixels.Length < image.Width * image.Height * bytesPerPixel) return [];
        LuminanceSource source = new RGBLuminanceSource(image.Pixels, image.Width, image.Height,
            image.Format == Bitmap.Layout.Gray8 ? RGBLuminanceSource.BitmapFormat.Gray8 : RGBLuminanceSource.BitmapFormat.BGRA32);
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = thorough,
            Options = new DecodingOptions
            {
                PossibleFormats = [BarcodeFormat.QR_CODE],
                TryHarder = thorough,
                CharacterSet = "UTF-8",
            },
        };
        var results = reader.DecodeMultiple(source) ?? (reader.Decode(source) is { } one ? [one] : []);
        return results.Select(r => r.Text).Where(t => !string.IsNullOrEmpty(t)).Distinct().ToList();
    }
}
