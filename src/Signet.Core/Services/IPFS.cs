using Signet.Models;

namespace Signet.Services;

public static class IPFS
{
    /// <summary>
    /// Public gateways in the order we try them. ipfs.io and dweb.link rate-limit aggressively
    /// (HTTP 429) so they are last resorts; Filebase has been fast and reliable.
    /// </summary>
    public static readonly IReadOnlyList<Uri> Gateways =
    [
        new("https://ipfs.filebase.io/ipfs/"),
        new("https://gateway.pinata.cloud/ipfs/"),
        new("https://ipfs.io/ipfs/"),
    ];

    /// <summary>The <c>&lt;cid&gt;[/path]</c> part of an <c>ipfs://</c> URI (also accepts <c>ipfs://ipfs/&lt;cid&gt;</c>), or <c>null</c>.</summary>
    public static string? PathOf(string uri)
    {
        var trimmed = uri.Trim();
        if (!trimmed.StartsWith("ipfs://", StringComparison.Ordinal)) return null;
        var path = trimmed["ipfs://".Length..];
        if (path.StartsWith("ipfs/", StringComparison.Ordinal)) path = path["ipfs/".Length..];
        return path.Length == 0 ? null : path;
    }

    /// <summary>
    /// The <c>&lt;cid&gt;[/path]</c> behind an HTTP(S) gateway URL, in either the path form
    /// (<c>https://host/ipfs/&lt;cid&gt;/x</c>) or the subdomain form (<c>https://&lt;cid&gt;.ipfs.host/x</c>), or <c>null</c>.
    /// </summary>
    public static string? GatewayPath(Uri url)
    {
        var components = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToList();
        var index = components.IndexOf("ipfs");
        if (index >= 0 && index + 1 < components.Count) return string.Join('/', components.Skip(index + 1));
        var labels = url.Host.Split('.');
        if (labels.Length >= 3 && labels[1] == "ipfs" && LooksLikeCID(labels[0]))
        {
            var rest = string.Join('/', components);
            return rest.Length == 0 ? labels[0] : $"{labels[0]}/{rest}";
        }
        return null;
    }

    /// <summary>CIDv0 (<c>Qm…</c>, 46 chars) or CIDv1 base32 (<c>b…</c>).</summary>
    public static bool LooksLikeCID(string text) =>
        (text.StartsWith("Qm", StringComparison.Ordinal) && text.Length == 46) ||
        (text.StartsWith('b') && text.Length >= 50 && text.All(char.IsLetterOrDigit));

    /// <summary>
    /// Every HTTPS URL worth trying for <c>uri</c>, best first. An <c>ipfs://</c> URI yields one URL per
    /// gateway. An HTTP(S) URL on some other IPFS gateway is widened to our gateways too, with
    /// the original last (public gateways such as dweb.link rate-limit, HTTP 429). Any other
    /// HTTP(S) URL passes through as the only candidate; anything else yields none.
    /// </summary>
    public static IReadOnlyList<Uri> CandidateURLs(string uri)
    {
        if (PathOf(uri) is { } path) return OnGateways(path);
        var trimmed = uri.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var url) && url.Scheme is "https" or "http")
        {
            if (GatewayPath(url) is { } gatewayPath)
            {
                var urls = OnGateways(gatewayPath).ToList();
                if (!urls.Contains(url)) urls.Add(url);
                return urls;
            }
            return [url];
        }
        return [];
    }

    private static List<Uri> OnGateways(string path) =>
        Gateways.Select(g => Uri.TryCreate(g, path, out var u) ? u : null).OfType<Uri>().ToList();

    /// <summary>The preferred HTTPS URL for <c>uri</c>, or <c>null</c>.</summary>
    public static Uri? HttpURL(string uri) => CandidateURLs(uri) is { Count: > 0 } urls ? urls[0] : null;
}
