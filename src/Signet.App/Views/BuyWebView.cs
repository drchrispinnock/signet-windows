using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Signet.Services;
using static Signet.Services.BuyWebViewPolicy;

namespace Signet.App.Views;

/// <summary>
/// Hosts the on-ramp's widget inside the app (WebView2 where upstream has WKWebView). The embedded
/// page has no address bar, so the policy is strict (S23, <c>BuyWebViewPolicy</c>): the main frame may
/// only ever show HTTPS pages on the provider's own hosts, and the sheet shows which host is up.
/// Links the widget opens elsewhere go to the browser; any other foreign navigation (a redirect, a
/// script, a form posting away) is cancelled. Frames may only load HTTPS. Camera and microphone are
/// granted only to the provider's exact hosts, over HTTPS, from the main frame.
///
/// Windows differences: WebView2 has no <c>WKNavigationType</c>. A navigation it marks as user-initiated
/// (a click or a form the user sent) counts as <c>LinkActivated</c>, a redirect or anything else as
/// <c>Other</c>. Permissions other than camera and microphone are refused rather than prompted, and
/// a frame's own permission requests are refused. Browsing data lives in
/// <c>%LOCALAPPDATA%\Signet\WebView2</c>, so the provider's session survives between purchases as
/// WKWebView's default data store does.
/// </summary>
public sealed partial class BuyWebView : UserControl
{
    private readonly WebView2 web = new();
    private readonly Uri url;
    private readonly BuyProvider provider;

    public event Action<bool>? LoadingChanged;
    public event Action<string?>? LoadErrorChanged;
    /// <summary>The URL the main frame is showing or loading, for the sheet's origin line.</summary>
    public event Action<Uri?>? CurrentURLChanged;

    public static string DataFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Signet", "WebView2");

    public BuyWebView(Uri url, BuyProvider provider)
    {
        this.url = url;
        this.provider = provider;
        Content = web;
        Loaded += async (_, _) => await Start();
    }

    private async Task Start()
    {
        if (web.CoreWebView2 is not null) return;
        try
        {
            Directory.CreateDirectory(DataFolder);
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, DataFolder, new CoreWebView2EnvironmentOptions());
            await web.EnsureCoreWebView2Async(environment);
        }
        catch (Exception e)
        {
            LoadErrorChanged?.Invoke($"The web view could not start ({e.Message}). Use “Open in browser instead”.");
            return;
        }
        if (web.CoreWebView2 is not { } core) return;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.NavigationStarting += MainNavigationStarting;
        core.FrameNavigationStarting += (_, args) =>
        {
            if (Policy(Parse(args.Uri), Kind(args), false, provider) != NavigationPolicy.Allow) args.Cancel = true;
        };
        core.NewWindowRequested += async (_, args) =>
        {
            // window.open targets go to the browser when they are web pages; other schemes are dropped.
            args.Handled = true;
            if (Parse(args.Uri) is { } target && MayOpenInBrowser(target)) await Windows.System.Launcher.LaunchUriAsync(target);
        };
        core.PermissionRequested += (_, args) =>
        {
            var origin = Parse(args.Uri);
            var media = args.PermissionKind is CoreWebView2PermissionKind.Camera or CoreWebView2PermissionKind.Microphone;
            var allowed = media && origin is not null && MayCaptureMedia(origin.Scheme, origin.Host, true, origin.Scheme, origin.Host, provider, origin.Port, origin.Port);
            args.State = allowed ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
            args.Handled = true;
        };
        core.FrameCreated += (_, created) =>
        {
            if (created.Frame is not { } frame) return;
            frame.PermissionRequested += (_, args) =>
            {
                args.State = CoreWebView2PermissionState.Deny;
                args.Handled = true;
            };
        };
        core.SourceChanged += (_, _) => CurrentURLChanged?.Invoke(Parse(core.Source));
        core.NavigationCompleted += (_, args) =>
        {
            LoadingChanged?.Invoke(false);
            CurrentURLChanged?.Invoke(Parse(core.Source));
            if (!args.IsSuccess && args.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                LoadErrorChanged?.Invoke(args.WebErrorStatus.ToString());
        };
        core.Navigate(url.AbsoluteUri);
    }

    /// <summary>Every main-frame navigation, including each hop of a redirect chain.</summary>
    private async void MainNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        var target = Parse(args.Uri);
        switch (Policy(target, Kind(args), true, provider))
        {
            case NavigationPolicy.Allow:
                LoadingChanged?.Invoke(true);
                LoadErrorChanged?.Invoke(null);
                CurrentURLChanged?.Invoke(target);
                break;
            case NavigationPolicy.OpenInBrowser:
                args.Cancel = true;
                if (target is not null) await Windows.System.Launcher.LaunchUriAsync(target);
                break;
            default:
                args.Cancel = true;
                LoadErrorChanged?.Invoke($"Blocked a page outside {provider.Title()}: {(target is null ? "unknown" : target.Host.Length > 0 ? target.Host : target.Scheme)}. Use “Open in browser instead” if you need it.");
                break;
        }
    }

    private static NavigationType Kind(CoreWebView2NavigationStartingEventArgs args) =>
        args.IsUserInitiated && !args.IsRedirected ? NavigationType.LinkActivated : NavigationType.Other;

    private static Uri? Parse(string? text) => Uri.TryCreate(text, UriKind.Absolute, out var uri) ? uri : null;
}
