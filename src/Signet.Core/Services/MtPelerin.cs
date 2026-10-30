using System.Globalization;
using System.Text;
using Signet.Models;

namespace Signet.Services;

/// <summary>
/// Mt Pelerin's hosted buy widget (https://developers.mtpelerin.com). Signet only builds the URL
/// and opens it; the purchase, KYC and payment all happen on Mt Pelerin's side and the tez are
/// delivered straight to the account's address.
/// </summary>
public static class MtPelerin
{
    public static readonly Uri WidgetURL = new("https://widget.mtpelerin.com/");
    /// <summary>Activation key (<c>_ctkn</c>), if Mt Pelerin issues us one. Direct links work without it.</summary>
    public static readonly string? ActivationKey = null;
    /// <summary>Revenue-sharing code (<c>rfr</c>), none for now.</summary>
    public static readonly string? ReferralCode = null;
    public const string Network = "tezos_mainnet";
    public static readonly IReadOnlySet<string> SupportedLanguages = new HashSet<string> { "en", "fr", "de", "it", "es", "pt" };
    public static readonly IReadOnlyList<string> FiatCurrencies = ["CHF", "EUR", "USD", "GBP"];

    /// <summary>Proof that we control the address, so the widget skips its own "sign this message" step.</summary>
    public sealed record Validation(string Code, string PublicKey, string Signature);

    /// <summary>A 4-digit code from 1000 to 9999; must differ per address.</summary>
    public static string RandomCode() => Random.Shared.Next(1000, 10000).ToString(CultureInfo.InvariantCulture);

    /// <summary>The text the user signs: Tezos wallets prefix message signatures so they can never be mistaken for an operation.</summary>
    public static string Message(string code) => $"Tezos Signed Message: MtPelerin-{code}";

    /// <summary>
    /// The message packed as a Micheline string (<c>PACK "…"</c>): 0x05 (data), 0x01 (string), 4-byte
    /// big-endian length, UTF-8 bytes. This is what <c>SignPayload</c> signs with no watermark.
    /// </summary>
    public static string PackedMessage(string code)
    {
        var text = Encoding.UTF8.GetBytes(Message(code));
        var length = (uint)text.Length;
        byte[] bytes = [0x05, 0x01, (byte)(length >> 24), (byte)((length >> 16) & 0xff), (byte)((length >> 8) & 0xff), (byte)(length & 0xff), .. text];
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>The <c>hash</c> parameter for Tezos: a six-line armored block carrying the message, public key and signature.</summary>
    public static string ArmoredBlock(Validation validation) => string.Join("\n",
        "-----BEGIN TEZOS SIGNED MESSAGE-----",
        Message(validation.Code),
        "-----BEGIN SIGNATURE-----",
        validation.PublicKey,
        validation.Signature,
        "-----END TEZOS SIGNED MESSAGE-----");

    /// <summary>The best fiat to offer first: the culture's currency when Mt Pelerin takes it, else EUR.</summary>
    public static string DefaultFiat(CultureInfo? culture = null)
    {
        string code;
        try { code = new RegionInfo((culture ?? CultureInfo.CurrentCulture).Name).ISOCurrencySymbol.ToUpperInvariant(); }
        catch (ArgumentException) { code = ""; }   // neutral or invariant culture: no region
        return FiatCurrencies.Contains(code) ? code : "EUR";
    }

    public static string Language(CultureInfo? culture = null)
    {
        var code = (culture ?? CultureInfo.CurrentUICulture).TwoLetterISOLanguageName.ToLowerInvariant();
        return SupportedLanguages.Contains(code) ? code : "en";
    }

    /// <summary>How the widget is shown: inside Signet (<c>webview</c>) or as a link in the browser (<c>direct-link</c>).</summary>
    public enum Presentation { Embedded, Browser }

    public static string RawValue(this Presentation presentation) => presentation == Presentation.Embedded ? "webview" : "direct-link";

    /// <summary>The buy widget for <c>address</c>, locked to tez on Tezos mainnet.</summary>
    public static Uri BuyURL(Address address, string fiat, Validation? validation, Presentation presentation = Presentation.Embedded, CultureInfo? culture = null, bool dark = false)
    {
        var items = new List<(string Name, string Value)>();
        if (ActivationKey is { } key) items.Add(("_ctkn", key));
        items.AddRange([
            ("type", presentation.RawValue()),
            ("lang", Language(culture)),
            ("tab", "buy"),
            ("tabs", "buy"),
            ("bsc", fiat),
            ("bdc", "XTZ"),
            ("crys", "XTZ"),
            ("dnet", Network),
            ("nets", Network),
            ("net", Network),
            ("addr", address.Value),
        ]);
        if (ReferralCode is { } referral) items.Add(("rfr", referral));
        if (dark) items.Add(("mode", "dark"));
        if (validation is not null)
        {
            items.Add(("code", validation.Code));
            items.Add(("hash", ArmoredBlock(validation)));
        }
        var query = string.Join("&", items.Select(item => $"{item.Name}={Encode(item.Value)}"));
        return new Uri($"{WidgetURL.AbsoluteUri}?{query}");
    }

    /// <summary>
    /// Percent-encodes a query value the way upstream does: URL-query-allowed characters minus
    /// <c>+ / = &amp; ? :</c> and newline, since <c>+</c>, <c>/</c> and <c>=</c> appear in base58
    /// signatures and the armored block.
    /// </summary>
    private static string Encode(string value)
    {
        var builder = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || "-._~!$'()*,;@".Contains(c)) builder.Append(c);
            else builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    /// <summary>The value of <c>name</c> in a URL's query, decoded (tests and the browser hand-off).</summary>
    public static string? QueryValue(Uri url, string name) =>
        url.Query.TrimStart('?').Split('&')
            .Select(pair => pair.Split('=', 2))
            .Where(parts => parts[0] == name)
            .Select(parts => Uri.UnescapeDataString(parts.Length > 1 ? parts[1] : ""))
            .FirstOrDefault();
}
