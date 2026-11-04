using System.Text;
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

    /// <summary>
    /// A content path as it is appended to a gateway: the CID, then zero or more path components
    /// (each percent-encoded, none empty, <c>.</c> or <c>..</c>), then an optional query. Built only through
    /// <c>Validated</c>, so a path from token metadata cannot climb out of <c>/ipfs/</c> on the gateway.
    /// </summary>
    /// <param name="TrailingSlash"><c>ipfs://&lt;cid&gt;/</c> keeps its slash: some gateways treat the directory index differently without it.</param>
    public sealed record ContentPath(string Cid, IReadOnlyList<string> Components, bool TrailingSlash, string? Query)
    {
        /// <summary><c>cid/a/b?query</c>, the components percent-encoded.</summary>
        public string Encoded
        {
            get
            {
                var text = string.Join("/", new[] { Cid }.Concat(Components.Select(EncodeComponent)));
                if (TrailingSlash) text += "/";
                if (Query is not null) text += "?" + Query;
                return text;
            }
        }

        /// <summary>Like <c>urlPathAllowed</c> without <c>/</c> and <c>;</c>.</summary>
        private static string EncodeComponent(string component)
        {
            var builder = new StringBuilder();
            foreach (var b in Encoding.UTF8.GetBytes(component))
            {
                var c = (char)b;
                if (b < 128 && (char.IsAsciiLetterOrDigit(c) || "-._~!$&'()*+,=:@".Contains(c))) builder.Append(c);
                else builder.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
            return builder.ToString();
        }

        /// <summary>Validates <c>&lt;cid&gt;[/path][?query]</c> as it appears after <c>ipfs://</c> or <c>/ipfs/</c> on a gateway.</summary>
        public static ContentPath? Validated(string raw)
        {
            var rest = raw;
            if (rest.IndexOf('#') is var hash and >= 0) rest = rest[..hash];
            string? query = null;
            if (rest.IndexOf('?') is var mark and >= 0)
            {
                var q = rest[(mark + 1)..];
                rest = rest[..mark];
                if (!q.All(c => c < 128 && (char.IsAsciiLetterOrDigit(c) || "-._~!$&'()*+,;=:@/?%".Contains(c)))) return null;
                query = q.Length == 0 ? null : q;
            }
            var parts = rest.Split('/').ToList();
            // A trailing slash (ipfs://Qm…/) is fine; an empty component anywhere else is not.
            var trailingSlash = false;
            if (parts.Count > 1 && parts[^1].Length == 0) { parts.RemoveAt(parts.Count - 1); trailingSlash = true; }
            if (parts.Count == 0 || !LooksLikeCID(parts[0])) return null;
            var components = parts.Skip(1).ToList();
            foreach (var component in components)
            {
                string decoded;
                try { decoded = Uri.UnescapeDataString(component); }
                catch (UriFormatException) { return null; }
                if (component.Length == 0 || component is "." or ".." || component.Contains('\\') || decoded is "." or ".." || decoded.Contains('/')) return null;
            }
            return new ContentPath(parts[0], components.Select(Uri.UnescapeDataString).ToList(), trailingSlash, query);
        }

        public bool Equals(ContentPath? other) =>
            other is not null && Cid == other.Cid && Components.SequenceEqual(other.Components) && TrailingSlash == other.TrailingSlash && Query == other.Query;
        public override int GetHashCode() => HashCode.Combine(Cid, Components.Count, Query);
    }

    /// <summary>The <c>&lt;cid&gt;[/path]</c> part of an <c>ipfs://</c> URI (also accepts <c>ipfs://ipfs/&lt;cid&gt;</c>), or null.</summary>
    public static string? PathOf(string uri) => ContentPathOf(uri)?.Encoded;

    public static ContentPath? ContentPathOf(string uri)
    {
        var trimmed = uri.Trim();
        if (!trimmed.StartsWith("ipfs://", StringComparison.OrdinalIgnoreCase)) return null;
        var path = trimmed["ipfs://".Length..];
        if (path.StartsWith("ipfs/", StringComparison.Ordinal)) path = path["ipfs/".Length..];
        return path.Length == 0 ? null : ContentPath.Validated(path);
    }

    /// <summary>
    /// The <c>&lt;cid&gt;[/path]</c> behind an HTTP(S) gateway URL, in either the path form
    /// (<c>https://host/ipfs/&lt;cid&gt;/x</c>) or the subdomain form (<c>https://&lt;cid&gt;.ipfs.host/x</c>), or null.
    /// </summary>
    public static ContentPath? GatewayContentPath(Uri url)
    {
        var components = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        var query = url.Query.Length > 1 ? url.Query : "";
        var index = components.IndexOf("ipfs");
        if (index >= 0 && index + 1 < components.Count) return ContentPath.Validated(string.Join('/', components.Skip(index + 1)) + query);
        var labels = HostAsWritten(url).Split('.');
        if (labels.Length >= 3 && labels[1].Equals("ipfs", StringComparison.OrdinalIgnoreCase) && LooksLikeCID(labels[0]))
        {
            var rest = string.Join('/', components);
            return ContentPath.Validated((rest.Length == 0 ? labels[0] : $"{labels[0]}/{rest}") + query);
        }
        return null;
    }

    /// <summary>
    /// The host with its letters as written: <c>Uri.Host</c> lower-cases it, which would turn a
    /// CIDv0 label (<c>Qm…</c>, case-sensitive base58) into another, invalid one.
    /// </summary>
    private static string HostAsWritten(Uri url)
    {
        var text = url.OriginalString.Trim();
        var start = text.IndexOf("://", StringComparison.Ordinal);
        if (start < 0) return url.Host;
        var rest = text[(start + 3)..];
        if (rest.LastIndexOf('@', rest.IndexOfAny(['/', '?', '#']) is var end and >= 0 ? end : rest.Length - 1) is var at and >= 0) rest = rest[(at + 1)..];
        var stop = rest.IndexOfAny(['/', ':', '?', '#']);
        var host = stop < 0 ? rest : rest[..stop];
        return host.Equals(url.Host, StringComparison.OrdinalIgnoreCase) ? host : url.Host;
    }

    /// <summary>The <c>&lt;cid&gt;[/path]</c> behind a gateway URL as text, or null. (Kept for the tests and callers of the first port.)</summary>
    public static string? GatewayPath(Uri url) => GatewayContentPath(url)?.Encoded;

    /// <summary><c>/ipfs/</c> in the path, or a <c>&lt;cid&gt;.ipfs.</c> subdomain: the URL names IPFS content.</summary>
    public static bool LooksLikeGatewayURL(Uri url)
    {
        if (url.AbsolutePath.Split('/').Contains("ipfs")) return true;
        var labels = url.Host.Split('.');
        return labels.Length >= 3 && labels[1] == "ipfs";
    }

    /// <summary>CIDv0 (<c>Qm…</c>, 46 base58 chars) or CIDv1 base32 (<c>b…</c>, lower-case letters and digits 2 to 7).</summary>
    public static bool LooksLikeCID(string text)
    {
        if (text.StartsWith("Qm", StringComparison.Ordinal))
            return text.Length == 46 && text.All(c => char.IsAsciiLetterOrDigit(c) && !"0OIl".Contains(c));
        return text.StartsWith('b') && text.Length >= 50 && text.All(c => char.IsAsciiLetterLower(c) || c is >= '2' and <= '7');
    }

    /// <summary><c>path</c> appended to a gateway, if the result still points at that gateway's <c>/ipfs/</c>.</summary>
    private static Uri? GatewayURL(Uri gateway, ContentPath path)
    {
        if (!Uri.TryCreate(gateway.AbsoluteUri + path.Encoded, UriKind.Absolute, out var url) || url.Host != gateway.Host) return null;
        var parts = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && parts[0] == "ipfs" && parts[1] == path.Cid ? url : null;
    }

    /// <summary>
    /// Every HTTPS URL worth trying for <c>uri</c>, best first. An <c>ipfs://</c> URI yields one URL per
    /// gateway. An HTTP(S) URL on some other IPFS gateway is widened to our gateways too, with
    /// the original last (public gateways such as dweb.link rate-limit, HTTP 429). Any other
    /// URL passes through as the only candidate when it is HTTPS to a public host; anything
    /// else (http, credentials, loopback, link-local, private ranges, local names) yields none,
    /// so token metadata cannot make the wallet poke at services on this machine or its network (S13).
    /// </summary>
    public static IReadOnlyList<Uri> CandidateURLs(string uri)
    {
        if (ContentPathOf(uri) is { } path) return Gateways.Select(g => GatewayURL(g, path)).OfType<Uri>().ToList();
        if (!Uri.TryCreate(uri.Trim(), UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http")) return [];
        if (LooksLikeGatewayURL(url))
        {
            // A gateway URL whose content path does not validate is not tried at all.
            if (GatewayContentPath(url) is not { } gatewayPath) return [];
            var urls = Gateways.Select(g => GatewayURL(g, gatewayPath)).OfType<Uri>().ToList();
            if (EndpointPolicy.IsPublicHTTPS(url) && !urls.Contains(url)) urls.Add(url);
            return urls;
        }
        return EndpointPolicy.IsPublicHTTPS(url) ? [url] : [];
    }

    /// <summary>The preferred HTTPS URL for <c>uri</c>, or null.</summary>
    public static Uri? HttpURL(string uri) => CandidateURLs(uri) is { Count: > 0 } urls ? urls[0] : null;
}
