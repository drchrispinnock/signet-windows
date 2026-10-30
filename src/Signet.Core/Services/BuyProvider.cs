using System.Globalization;
using Signet.Models;

namespace Signet.Services;

/// <summary>
/// Where the Buy button sends people: Mt Pelerin's widget inside the app, or an exchange
/// (Coinbase, Kraken) or Transak's own site in the browser with the address to paste (no partner
/// agreement needed, and they serve the US). The choice is a dropdown in the Buy sheet and is
/// remembered. Further widget providers slot in as cases with their own URL builder and trusted hosts.
/// </summary>
public enum BuyProvider { MtPelerin, Coinbase, Kraken, Transak }

public static class BuyProviderExtensions
{
    public const string Key = "buyProvider";
    public const BuyProvider Default = BuyProvider.MtPelerin;

    public static string RawValue(this BuyProvider provider) => provider switch
    {
        BuyProvider.MtPelerin => "mtPelerin",
        BuyProvider.Coinbase => "coinbase",
        BuyProvider.Kraken => "kraken",
        _ => "transak",
    };

    public static BuyProvider? FromRawValue(string? raw) =>
        Enum.GetValues<BuyProvider>().Cast<BuyProvider?>().FirstOrDefault(p => p!.Value.RawValue() == raw);

    public static string Title(this BuyProvider provider) => provider switch
    {
        BuyProvider.MtPelerin => "Mt Pelerin",
        BuyProvider.Coinbase => "Coinbase",
        BuyProvider.Kraken => "Kraken",
        _ => "Transak",
    };

    /// <summary>
    /// Whether the provider's page is shown inside Signet (a widget that takes the address) or
    /// opened in the browser, with Signet showing the address to paste.
    /// </summary>
    public static bool UsesWidget(this BuyProvider provider) => provider == BuyProvider.MtPelerin;

    /// <summary>The page to open in the browser for a provider that has no widget.</summary>
    public static Uri? BrowserPage(this BuyProvider provider) => provider switch
    {
        BuyProvider.Coinbase => new Uri("https://www.coinbase.com/price/tezos"),
        BuyProvider.Kraken => new Uri("https://www.kraken.com/prices/tezos"),
        BuyProvider.Transak => new Uri("https://transak.com/buy/xtz"),
        _ => null,
    };

    /// <summary>Who it is and who it cannot serve, for the Buy sheet.</summary>
    public static string Summary(this BuyProvider provider) => provider switch
    {
        BuyProvider.MtPelerin => "Swiss on-ramp: bank transfer or card, tez delivered straight to your account. Not available to US persons.",
        BuyProvider.Coinbase => "Buy tez on Coinbase in your browser, then send it to this account; Signet shows the address to paste. Available wherever Coinbase is, the US included.",
        BuyProvider.Kraken => "Buy tez on Kraken in your browser, then withdraw it to this account; Signet shows the address to paste. Available wherever Kraken is, the US included.",
        _ => "Buy tez on Transak's site in your browser by card, Apple Pay or bank transfer, delivered to the address Signet shows. Registered in the US, the UK, Canada and Australia.",
    };

    /// <summary>
    /// The step where the address goes, for a provider with no widget: an exchange has a
    /// Withdraw screen after the purchase, Transak asks for the address during it.
    /// </summary>
    public static string DeliveryStep(this BuyProvider provider) => provider switch
    {
        BuyProvider.MtPelerin => "",
        BuyProvider.Coinbase or BuyProvider.Kraken => "Choose Withdraw (or Send), paste this address and pick the Tezos network.",
        _ => "Paste this address when it asks for your Tezos wallet; the tez are sent straight to it.",
    };

    public static IReadOnlyList<string> FiatCurrencies(this BuyProvider provider) =>
        provider == BuyProvider.MtPelerin ? MtPelerin.FiatCurrencies : [];

    public static string DefaultFiat(this BuyProvider provider, CultureInfo? culture = null) =>
        provider == BuyProvider.MtPelerin ? MtPelerin.DefaultFiat(culture) : "";

    /// <summary>Hosts the embedded widget may use the camera for and navigate within.</summary>
    public static IReadOnlySet<string> TrustedHosts(this BuyProvider provider) => provider == BuyProvider.MtPelerin
        ? new HashSet<string> { "widget.mtpelerin.com", "mtpelerin.com", "www.mtpelerin.com", "api.mtpelerin.com" }
        : new HashSet<string>();   // never embedded

    public static string TrustedDomainSuffix(this BuyProvider provider) =>
        provider == BuyProvider.MtPelerin ? ".mtpelerin.com" : ".invalid";

    /// <summary>Whether the provider wants a signed proof that we own the address (and what to sign).</summary>
    public static string? OwnershipPayload(this BuyProvider provider, string code) =>
        provider == BuyProvider.MtPelerin ? MtPelerin.PackedMessage(code) : null;

    public static Uri BuyURL(this BuyProvider provider, Address address, string fiat, string code, SignedPayload? proof, bool embedded, bool dark, CultureInfo? culture = null)
    {
        if (provider != BuyProvider.MtPelerin) return provider.BrowserPage()!;
        var validation = proof is null ? null : new MtPelerin.Validation(code, proof.PublicKey, proof.Signature);
        return MtPelerin.BuyURL(address, fiat, validation, embedded ? MtPelerin.Presentation.Embedded : MtPelerin.Presentation.Browser, culture, dark);
    }

    /// <summary>The same URL as a plain browser link (used by "Open in browser instead").</summary>
    public static Uri BrowserURL(this BuyProvider provider, Uri embedded) =>
        provider == BuyProvider.MtPelerin
            ? new Uri(embedded.AbsoluteString().Replace("type=webview", "type=direct-link", StringComparison.Ordinal))
            : embedded;

    /// <summary>"Mt Pelerin, Coinbase, Kraken or Transak", for tooltips on the way to the sheet.</summary>
    public static string Choices
    {
        get
        {
            var titles = Enum.GetValues<BuyProvider>().Select(p => p.Title()).ToList();
            return string.Join(", ", titles.SkipLast(1)) + " or " + titles[^1];
        }
    }

    /// <summary>The provider last chosen in the Buy sheet.</summary>
    public static BuyProvider Current => FromRawValue(UserDefaults.Standard.String(Key)) ?? Default;
}

/// <summary>
/// The embedded purchase page's navigation and media policy (S23), upstream's static functions on
/// <c>BuyWebView</c>. Windows keeps them here, without the view, so the headless tests reach them;
/// the WebView2 host in the app applies them.
/// </summary>
public static class BuyWebViewPolicy
{
    /// <summary>What started a navigation, as WebKit's <c>WKNavigationType</c> says it.</summary>
    public enum NavigationType { LinkActivated, FormSubmitted, BackForward, Reload, FormResubmitted, Other }

    /// <summary>What to do with a navigation the web view proposes.</summary>
    public enum NavigationPolicy
    {
        Allow,
        /// <summary>A foreign page the user asked for: show it in the browser instead.</summary>
        OpenInBrowser,
        Cancel,
    }

    /// <summary>An HTTPS URL on one of the provider's hosts.</summary>
    public static bool IsTrusted(Uri? url, BuyProvider provider)
    {
        if (url is null || !url.IsAbsoluteUri || !string.Equals(url.Scheme, "https", StringComparison.OrdinalIgnoreCase)) return false;
        if (url.Port != 443 || url.UserInfo.Length > 0 || url.Host.Length == 0) return false;
        return IsTrustedHost(url.Host, provider);
    }

    public static bool IsTrustedHost(string host, BuyProvider provider)
    {
        var lower = host.ToLowerInvariant();
        return provider.TrustedHosts().Contains(lower) || lower.EndsWith(provider.TrustedDomainSuffix(), StringComparison.Ordinal);
    }

    /// <summary>Decides every navigation, main frame or not, whatever started it.</summary>
    public static NavigationPolicy Policy(Uri? url, NavigationType navigationType, bool isMainFrame, BuyProvider provider)
    {
        if (url is null || !url.IsAbsoluteUri) return NavigationPolicy.Cancel;
        var scheme = url.Scheme.ToLowerInvariant();
        if (scheme == "about" && url.OriginalString == "about:blank") return NavigationPolicy.Allow;
        if (scheme != "https") return NavigationPolicy.Cancel;     // plain http, custom schemes, data:, file:
        if (!isMainFrame) return NavigationPolicy.Allow;           // frames: any HTTPS, no browser hand-off
        if (IsTrusted(url, provider)) return NavigationPolicy.Allow;
        // Only something the user did on the page earns a trip to the browser; a redirect, a
        // script or a timer does not.
        return navigationType is NavigationType.LinkActivated or NavigationType.FormSubmitted
            ? NavigationPolicy.OpenInBrowser
            : NavigationPolicy.Cancel;
    }

    /// <summary>Whether <c>window.open(url)</c> may be forwarded to the browser: only web URLs.</summary>
    public static bool MayOpenInBrowser(Uri? url) =>
        url is { IsAbsoluteUri: true } && (url.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) || url.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Camera/microphone: the provider's exact hosts, over HTTPS, asked for by the main frame
    /// whose own origin is that host.
    /// </summary>
    public static bool MayCaptureMedia(string originProtocol, string originHost, bool isMainFrame, string? frameProtocol, string? frameHost,
                                       BuyProvider provider, int originPort = 443, int framePort = 443)
    {
        if (!originProtocol.Equals("https", StringComparison.OrdinalIgnoreCase) || !isMainFrame) return false;
        if (originPort is not (0 or 443) || framePort is not (0 or 443)) return false;
        var host = originHost.ToLowerInvariant();
        if (!provider.TrustedHosts().Contains(host)) return false;
        return string.Equals(frameProtocol, "https", StringComparison.OrdinalIgnoreCase) && frameHost?.ToLowerInvariant() == host;
    }
}
