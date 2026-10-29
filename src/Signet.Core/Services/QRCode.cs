using QRCoder;

namespace Signet.Services;

/// <summary>QR codes for addresses (QRCoder stands in for Core Image's <c>qrCodeGenerator</c>).</summary>
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
}
