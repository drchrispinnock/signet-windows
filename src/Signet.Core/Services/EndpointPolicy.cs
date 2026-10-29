using System.Globalization;

namespace Signet.Services;

/// <summary>
/// Where the app is allowed to send requests on behalf of untrusted input (a dApp's RPC, a
/// token's artwork URL). Local and private destinations are refused unless the caller says the
/// user chose them deliberately, so a metadata or request field cannot make the wallet poke at
/// services on this machine or its network (S12, S13).
/// </summary>
public static class EndpointPolicy
{
    public abstract record Verdict
    {
        public sealed record Allowed : Verdict;
        public sealed record NotHTTPS : Verdict;
        public sealed record HasUserInfo : Verdict;
        public sealed record NoHost : Verdict;
        public sealed record LocalOrPrivate(string Host) : Verdict;
    }

    /// <summary>Hosts that are this machine or an internal name: refused for untrusted destinations.</summary>
    public static bool IsLocalOrPrivateHost(string rawHost)
    {
        var host = rawHost.ToLowerInvariant().Trim('[', ']');
        if (host.Length == 0 || host == "localhost" || host.EndsWith(".localhost") || host.EndsWith(".local")
            || host.EndsWith(".internal") || host.EndsWith(".home.arpa") || host.EndsWith(".lan") || !host.Contains('.') && !host.Contains(':'))
            return true;
        if (IPv4(host) is { } v4) return IsPrivateIPv4(v4);
        if (host.Contains(':')) return IsPrivateIPv6(host);
        return false;
    }

    /// <summary><c>https</c>, with a host, without credentials, and not local or private.</summary>
    public static Verdict Check(Uri url, bool allowHTTP = false)
    {
        var scheme = url.Scheme.ToLowerInvariant();
        if (!(scheme == "https" || allowHTTP && scheme == "http")) return new Verdict.NotHTTPS();
        if (url.UserInfo.Length > 0) return new Verdict.HasUserInfo();
        var host = url.IdnHost;
        if (host.Length == 0) return new Verdict.NoHost();
        if (IsLocalOrPrivateHost(host)) return new Verdict.LocalOrPrivate(host);
        return new Verdict.Allowed();
    }

    /// <summary>A public HTTPS destination fit for untrusted input.</summary>
    public static bool IsPublicHTTPS(Uri url) => Check(url) is Verdict.Allowed;

    // ---- Address ranges ------------------------------------------------------------------------

    public static byte[]? IPv4(string host)
    {
        var parts = host.Split('.');
        if (parts.Length != 4) return null;
        var bytes = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            var part = parts[i];
            if (part.Length is 0 or > 3 || !part.All(char.IsAsciiDigit) ||
                !byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out bytes[i])) return null;
        }
        return bytes;
    }

    /// <summary>Loopback, link-local, RFC 1918, CGNAT, "this network", broadcast and multicast.</summary>
    public static bool IsPrivateIPv4(byte[] b) => (b[0], b[1]) switch
    {
        (0, _) or (10, _) or (127, _) => true,
        (169, 254) or (192, 168) => true,
        (172, >= 16 and <= 31) => true,
        (100, >= 64 and <= 127) => true,
        (>= 224, _) => true,
        _ => false,
    };

    /// <summary>Loopback, unspecified, unique-local, link-local, multicast and IPv4-mapped private addresses.</summary>
    public static bool IsPrivateIPv6(string host)
    {
        var text = host.ToLowerInvariant();
        if (text.IndexOf('%') is var zone and >= 0) text = text[..zone];
        if (text is "::1" or "::") return true;
        if (text.StartsWith("::ffff:", StringComparison.Ordinal) && IPv4(text["::ffff:".Length..]) is { } v4) return IsPrivateIPv4(v4);
        var first = text.Split(':')[0];
        if (!ushort.TryParse(first.Length == 0 ? "0" : first, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value)) return true;
        if ((value & 0xfe00) == 0xfc00) return true;   // fc00::/7 unique local
        if ((value & 0xffc0) == 0xfe80) return true;   // fe80::/10 link local
        if ((value & 0xff00) == 0xff00) return true;   // multicast
        if (value == 0) return true;                    // ::/8 incl. mapped and compatible forms
        return false;
    }
}
