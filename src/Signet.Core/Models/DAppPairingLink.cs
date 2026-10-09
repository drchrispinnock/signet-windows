namespace Signet.Models;

/// <summary>
/// The link a dApp's wallet list opens for a desktop wallet: <c>&lt;scheme&gt;://?type=tzip10&amp;data=&lt;code&gt;</c>.
/// Beacon and Octez Connect build it as <c>deepLink + "?type=tzip10&amp;data=" + pairingPayload</c>.
/// </summary>
public static class DAppPairingLink
{
    public const string Scheme = "signet";
    public const string Type = "tzip10";

    /// <summary>The pairing code carried by <c>url</c>, or <c>null</c> when it is not a TZIP-10 pairing link.</summary>
    public static string? Code(Uri url)
    {
        if (!string.Equals(url.Scheme, Scheme, StringComparison.OrdinalIgnoreCase)) return null;
        var query = url.Query.TrimStart('?');
        if (query.Length == 0) return null;
        var items = query.Split('&').Select(pair =>
        {
            var eq = pair.IndexOf('=');
            var name = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]);
            string? value = eq < 0 ? null : Uri.UnescapeDataString(pair[(eq + 1)..]);
            return (name, value);
        }).ToList();
        string? Value(string name) => items.FirstOrDefault(i => i.name.ToLowerInvariant() == name).value?.Trim();
        if (Value("type")?.ToLowerInvariant() != Type || Value("data") is not { Length: > 0 } data) return null;
        return data;
    }

    /// <summary>The link for a pairing code, for tests and for telling dApp authors what to open.</summary>
    public static Uri Url(string code) => new($"{Scheme}://?type={Type}&data={Uri.EscapeDataString(code)}");
}
