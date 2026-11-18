using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Signet.Models;

namespace Signet.App.Views;

/// <summary>
/// Help › Getting Started (upstream's <c>GettingStartedView</c>): the book's Getting Started chapter
/// (<c>upstream/manuscript/110-starting.md</c> and its screenshots, linked into the app's
/// <c>Book</c> folder by <c>Signet.App.csproj</c>) rendered by <c>Markua</c> in a window of its own.
/// Links within the chapter scroll; every other link opens in the browser.
///
/// Windows differences: there is no Help menu, so "Getting Started" and "Signet Website" end the
/// burger menu. The page is WebView2 (upstream: WKWebView) with JavaScript off, and the chapter and
/// its pictures are served from memory and the <c>Book</c> folder under a private host name
/// (<c>WebResourceRequested</c>), since a page given as a string cannot read local files.
/// </summary>
public sealed partial class GettingStartedWindow : Window
{
    /// <summary>The Getting Started chapter of the Signet book online, for when the bundled copy is missing.</summary>
    public static readonly Uri GettingStartedURL = new("https://leanpub.com/read/tezossignet/ID110-starting_1_Getting");
    /// <summary>Signet's website, opened from the menu.</summary>
    public static readonly Uri WebsiteURL = new("https://signetwallet.org");
    public const string ChapterResource = "110-starting";

    private const string Host = "https://signet-book.example/";
    private const string PagePath = "getting-started.html";
    private static string BookFolder => Path.Combine(AppContext.BaseDirectory, "Book");
    private static GettingStartedWindow? open;

    private readonly WebView2 web = new();
    private readonly string? html;

    /// <summary>Opens the window, or brings the open one forward.</summary>
    public static void Show(double scale = 1)
    {
        if (open is { } existing)
        {
            existing.Activate();
            return;
        }
        var window = open = new GettingStartedWindow();
        window.AppWindow.SetIcon("Assets/Signet.ico");
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(860 * scale), (int)(780 * scale)));
        if (window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 560;
            presenter.PreferredMinimumHeight = 480;
        }
        window.Activate();
    }

    /// <summary>The chapter as HTML, from the app's <c>Book</c> folder.</summary>
    public static string? ChapterHTML()
    {
        try { return Markua.Html(File.ReadAllText(Path.Combine(BookFolder, ChapterResource + ".md")), "Getting Started", Host.TrimEnd('/')); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private GettingStartedWindow()
    {
        Title = "Getting Started";
        html = ChapterHTML();
        if (html is null)
        {
            var panel = new StackPanel { Spacing = 12, Padding = new Thickness(40), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            panel.Children.Add(new TextBlock { Text = "The Getting Started chapter is missing from this copy of Signet." });
            panel.Children.Add(new HyperlinkButton { Content = "Read it online", NavigateUri = GettingStartedURL, HorizontalAlignment = HorizontalAlignment.Center });
            Content = new Grid { Children = { panel }, RequestedTheme = Services.AppearanceApplier.Current };
        }
        else
        {
            Content = new Grid { Children = { web }, RequestedTheme = Services.AppearanceApplier.Current };
            web.Loaded += async (_, _) => await Start();
        }
        Closed += (_, _) => { if (open == this) open = null; };
    }

    private async Task Start()
    {
        if (web.CoreWebView2 is not null) return;
        try
        {
            Directory.CreateDirectory(BuyWebView.DataFolder);
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, BuyWebView.DataFolder, new CoreWebView2EnvironmentOptions());
            await web.EnsureCoreWebView2Async(environment);
        }
        catch (Exception)
        {
            return;
        }
        if (web.CoreWebView2 is not { } core) return;
        core.Settings.IsScriptEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.AddWebResourceRequestedFilter(Host + "*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, args) => args.Response = Serve(core.Environment, args.Request.Uri);
        core.NavigationStarting += (_, args) =>
        {
            // The page itself (and a link within it) stays; anything else is the browser's.
            if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var url) && url.GetLeftPart(UriPartial.Path) == Host + PagePath) return;
            args.Cancel = true;
            OpenOutside(args.Uri);
        };
        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            OpenOutside(args.Uri);
        };
        core.Navigate(Host + PagePath);
    }

    private static void OpenOutside(string text)
    {
        if (Uri.TryCreate(text, UriKind.Absolute, out var url) && url.Scheme is "https" or "http" or "mailto")
            _ = Windows.System.Launcher.LaunchUriAsync(url);
    }

    /// <summary>The page from memory, a picture from the <c>Book</c> folder, or 404 for anything else.</summary>
    private CoreWebView2WebResourceResponse Serve(CoreWebView2Environment environment, string requested)
    {
        if (Uri.TryCreate(requested, UriKind.Absolute, out var url) && url.GetLeftPart(UriPartial.Authority) + "/" == Host)
        {
            var path = Uri.UnescapeDataString(url.AbsolutePath.TrimStart('/'));
            if (path == PagePath && html is not null)
                return Response(environment, System.Text.Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8");
            var extension = Path.GetExtension(path).ToLowerInvariant();
            var type = extension switch { ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp", _ => null };
            var root = Path.GetFullPath(BookFolder) + Path.DirectorySeparatorChar;
            var file = Path.GetFullPath(Path.Combine(BookFolder, path));
            if (type is not null && !path.Contains("..") && file.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(file))
                return Response(environment, File.ReadAllBytes(file), type);
        }
        return environment.CreateWebResourceResponse(null, 404, "Not Found", "");
    }

    private static CoreWebView2WebResourceResponse Response(CoreWebView2Environment environment, byte[] body, string type) =>
        environment.CreateWebResourceResponse(new MemoryStream(body).AsRandomAccessStream(), 200, "OK", $"Content-Type: {type}");
}
