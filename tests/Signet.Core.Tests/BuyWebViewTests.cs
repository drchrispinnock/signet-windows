using Signet.Models;
using Signet.Services;
using static Signet.Services.BuyWebViewPolicy;

namespace Signet.Tests;

/// <summary>
/// S23: the embedded purchase page has no address bar, so the navigation and media policy
/// decides what the user can end up looking at and what the page can reach.
/// </summary>
public class BuyWebViewPolicyTests
{
    private const BuyProvider Provider = BuyProvider.MtPelerin;

    private static NavigationPolicy Check(string text, NavigationType type = NavigationType.Other, bool mainFrame = true) =>
        Policy(Uri.TryCreate(text, UriKind.Absolute, out var url) ? url : null, type, mainFrame, Provider);

    [Fact]
    public void MainFrameOnlyShowsTheProvidersHTTPSPages()
    {
        Assert.Equal(NavigationPolicy.Allow, Check("https://widget.mtpelerin.com/?x=1"));
        Assert.Equal(NavigationPolicy.Allow, Check("https://kyc.mtpelerin.com/verify"));
        Assert.Equal(NavigationPolicy.Allow, Check("https://widget.mtpelerin.com/", NavigationType.LinkActivated));
        // The provider over plain http is not the provider.
        Assert.Equal(NavigationPolicy.Cancel, Check("http://widget.mtpelerin.com/"));
        // Look-alikes.
        Assert.Equal(NavigationPolicy.Cancel, Check("https://mtpelerin.com.evil.example/"));
        Assert.Equal(NavigationPolicy.Cancel, Check("https://evil.example/widget.mtpelerin.com"));
        Assert.Equal(NavigationPolicy.Cancel, Check("https://user:pw@widget.mtpelerin.com/"));
    }

    [Fact]
    public void RedirectsScriptsAndFormsToForeignSitesAreCancelled()
    {
        // A server redirect or a script-driven navigation shows up as Other.
        Assert.Equal(NavigationPolicy.Cancel, Check("https://attacker.example/phish", NavigationType.Other));
        Assert.Equal(NavigationPolicy.Cancel, Check("https://attacker.example/phish", NavigationType.Reload));
        Assert.Equal(NavigationPolicy.Cancel, Check("https://attacker.example/phish", NavigationType.BackForward));
        Assert.Equal(NavigationPolicy.Cancel, Check("https://attacker.example/phish", NavigationType.FormResubmitted));
    }

    [Fact]
    public void UserInitiatedForeignLinksGoToTheBrowser()
    {
        Assert.Equal(NavigationPolicy.OpenInBrowser, Check("https://bank.example/login", NavigationType.LinkActivated));
        Assert.Equal(NavigationPolicy.OpenInBrowser, Check("https://bank.example/pay", NavigationType.FormSubmitted));
        // Only web pages go to the browser; a custom scheme is dropped even when clicked.
        Assert.Equal(NavigationPolicy.Cancel, Check("mailto:help@mtpelerin.com", NavigationType.LinkActivated));
        Assert.Equal(NavigationPolicy.Cancel, Check("signet://?type=tzip10&data=abc", NavigationType.LinkActivated));
        Assert.Equal(NavigationPolicy.Cancel, Check("ms-settings:privacy-webcam", NavigationType.LinkActivated));
        Assert.Equal(NavigationPolicy.Cancel, Check("javascript:alert(1)", NavigationType.LinkActivated));
        Assert.Equal(NavigationPolicy.Cancel, Check("file:///C:/Windows/win.ini", NavigationType.LinkActivated));
        Assert.Equal(NavigationPolicy.Cancel, Check("data:text/html,hi", NavigationType.LinkActivated));
    }

    [Fact]
    public void FramesMayOnlyLoadHTTPS()
    {
        Assert.Equal(NavigationPolicy.Allow, Check("https://js.stripe.com/v3/", mainFrame: false));
        Assert.Equal(NavigationPolicy.Allow, Check("https://attacker.example/", NavigationType.LinkActivated, mainFrame: false));
        Assert.Equal(NavigationPolicy.Cancel, Check("http://insecure.example/", mainFrame: false));
        Assert.Equal(NavigationPolicy.Cancel, Check("mailto:x@y", NavigationType.LinkActivated, mainFrame: false));
        Assert.Equal(NavigationPolicy.Allow, Check("about:blank", mainFrame: false));
        Assert.Equal(NavigationPolicy.Allow, Check("about:blank"));
        Assert.Equal(NavigationPolicy.Cancel, Policy(null, NavigationType.Other, true, Provider));
    }

    [Fact]
    public void WindowOpenForwardsOnlyWebURLs()
    {
        Assert.True(MayOpenInBrowser(new Uri("https://help.mtpelerin.com/")));
        Assert.True(MayOpenInBrowser(new Uri("http://example.org/")));
        Assert.False(MayOpenInBrowser(new Uri("tel:+41000000000")));
        Assert.False(MayOpenInBrowser(new Uri("signet://?type=tzip10&data=abc")));
        Assert.False(MayOpenInBrowser(new Uri("file:///C:/Program%20Files")));
        Assert.False(MayOpenInBrowser(null));
    }

    [Fact]
    public void MediaCaptureNeedsTheExactHostOverHTTPSFromTheMainFrame()
    {
        static bool May(string proto = "https", string host = "widget.mtpelerin.com", bool main = true, string? frameProto = "https", string? frameHost = "widget.mtpelerin.com") =>
            MayCaptureMedia(proto, host, main, frameProto, frameHost, Provider);
        Assert.True(May());
        Assert.True(May("https", "WIDGET.mtpelerin.com", frameHost: "widget.mtpelerin.com"));
        Assert.False(May("http"));
        Assert.False(May("https", "kyc.mtpelerin.com", frameHost: "kyc.mtpelerin.com"));   // suffix match is not enough for the camera
        Assert.False(May(main: false));
        Assert.False(May(frameHost: "attacker.example"));
        Assert.False(May(frameProto: "http"));
        Assert.False(May(frameProto: null, frameHost: null));
    }

    [Fact]
    public void TrustedMeansHTTPSOnTheProvidersHosts()
    {
        Assert.True(IsTrusted(new Uri("https://widget.mtpelerin.com/x"), Provider));
        Assert.True(IsTrusted(new Uri("https://kyc.mtpelerin.com/"), Provider));
        Assert.False(IsTrusted(new Uri("http://widget.mtpelerin.com/x"), Provider));
        Assert.False(IsTrusted(new Uri("https://evil.example/mtpelerin.com"), Provider));
        Assert.False(IsTrusted(null, Provider));
    }

    /// <summary>Part of upstream's AuditRepairTests: privileges are bound to the effective HTTPS port.</summary>
    [Fact]
    public void PurchasePrivilegesIncludeThePortAndEndpointsIncludeQueries()
    {
        Assert.False(IsTrusted(new Uri("https://widget.mtpelerin.com:8443/"), Provider));
        Assert.False(MayCaptureMedia("https", "widget.mtpelerin.com", true, "https", "widget.mtpelerin.com", Provider, originPort: 443, framePort: 8443));
        Assert.False(DAppRequest.SameEndpoint(new Uri("https://example.org/rpc?chain=a"), new Uri("https://example.org/rpc?chain=b")));
    }
}
