namespace Signet.Services;

/// <summary>A camera the scanner can use.</summary>
public sealed record CameraDevice(string Id, string Name);

/// <summary>
/// The parts of upstream's <c>QRCamera</c> that are not the camera itself. On Windows the camera
/// (<c>MediaCapture</c>), the camera watcher and the clipboard are the app's
/// (<c>Signet.App/Services/QRScanner.cs</c>); what they find is decided here.
/// </summary>
public static class QRCamera
{
    /// <summary>
    /// The camera to start with: the one used last when it is still attached, else the first.
    /// (Upstream asks for the camera chosen in the system's video menu; Windows has no such choice.)
    /// </summary>
    public static CameraDevice? Preferred(IReadOnlyList<CameraDevice> devices, string? lastUsed = null) =>
        devices.FirstOrDefault(d => d.Id == lastUsed) ?? devices.FirstOrDefault();

    /// <summary>
    /// The QR code in an image on the clipboard, or the clipboard's text when it holds no image:
    /// a screenshot of a code and a copied code are both worth reading. The app reads the
    /// clipboard (asynchronously, as Windows' clipboard is) and passes what it holds.
    /// </summary>
    public static string? TextFromPasteboard(QRCode.Bitmap? image, string? text) => TextsFromPasteboard(image, text).FirstOrDefault();

    /// <summary>The same, with every code of an image that shows several (a recovery sheet's page of parts).</summary>
    public static IReadOnlyList<string> TextsFromPasteboard(QRCode.Bitmap? image, string? text)
    {
        if (image is not null)
        {
            var codes = QRCode.DecodeAll(image);
            if (codes.Count > 0) return codes;
        }
        return text is null ? [] : [text];
    }
}
