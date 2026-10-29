using Windows.ApplicationModel.DataTransfer;

namespace Signet.App.Services;

/// <summary>The clipboard, as upstream uses <c>NSPasteboard.general</c>.</summary>
public static class Pasteboard
{
    public static void SetString(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }
}
