namespace Signet.Models;

/// <summary>
/// What the text of a scanned QR code (or a pasted string) is, as far as Signet can use it.
/// Classification is by shape and checksum only: nothing is sent anywhere and no key is opened.
/// The sheet each case is handed to does the real validation.
/// </summary>
public abstract record QRPayload
{
    /// <summary>An Octez Connect / Beacon pairing code, bare or carried by a <c>…?type=tzip10&amp;data=…</c> link.</summary>
    public sealed record Pairing(string Code) : QRPayload;
    /// <summary>A tz1 to tz6 or KT1 address.</summary>
    public sealed record AddressContent(Address Address) : QRPayload;
    /// <summary>A base58 public key (edpk, sppk, p2pk, BLpk, mdpk); its address is derived before use.</summary>
    public sealed record PublicKey(string Key) : QRPayload;
    /// <summary>A base58 secret key, clear or octez-encrypted.</summary>
    public sealed record SecretKey(string Key) : QRPayload;
    /// <summary>12, 15, 18, 21 or 24 lower-case words: a BIP39 recovery phrase by shape.</summary>
    public sealed record RecoveryPhrase(string Phrase) : QRPayload;
    public sealed record Unknown : QRPayload;

    public static readonly string[] PublicKeyPrefixes = ["edpk", "sppk", "p2pk", "BLpk", "mdpk"];
    public static readonly string[] SecretKeyPrefixes = ["edsk", "spsk", "p2sk", "BLsk", "mdsk", "edesk", "spesk", "p2esk", "BLesk", "mdesk"];
    /// <summary>Longer than any QR code can carry; anything bigger is not worth decoding.</summary>
    public const int MaximumLength = 8192;

    /// <summary>True for the cases that carry a secret, which must never be shown or logged.</summary>
    public bool IsSecret => this is SecretKey or RecoveryPhrase;

    public static QRPayload Classify(string raw)
    {
        var text = raw.Trim();
        if (text.Length == 0 || System.Text.Encoding.UTF8.GetByteCount(text) > MaximumLength) return new Unknown();

        if (DAppPairingLink.CodeFromScanned(text) is { } code) return new Pairing(code);

        // Some wallets write an address as a `tezos:` URI.
        var bare = StrippingTezosScheme(text);
        if (new Address(bare).IsValidAccount) return new AddressContent(new Address(bare));

        if (RecoveryPhraseOf(text) is { } phrase) return new RecoveryPhrase(phrase);

        // Everything else Signet reads is one base58check word.
        if (text.Any(char.IsWhiteSpace) || Base58.CheckDecode(text) is not { } body) return new Unknown();
        if (SecretKeyPrefixes.Any(p => text.StartsWith(p, StringComparison.Ordinal))) return new SecretKey(text);
        if (PublicKeyPrefixes.Any(p => text.StartsWith(p, StringComparison.Ordinal))) return new PublicKey(text);
        // A bare pairing code is base58check over the pairing request's JSON.
        if (body.Length > 0 && body[0] == (byte)'{' && body[^1] == (byte)'}') return new Pairing(text);
        return new Unknown();
    }

    private static string StrippingTezosScheme(string text)
    {
        if (!text.StartsWith("tezos:", StringComparison.OrdinalIgnoreCase)) return text;
        return text["tezos:".Length..].TrimStart('/');
    }

    /// <summary>
    /// The phrase with single spaces when <c>text</c> has the shape of one, else <c>null</c>. The
    /// word list and checksum are checked by the Import sheet, through the bridge.
    /// </summary>
    private static string? RecoveryPhraseOf(string text)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is not (12 or 15 or 18 or 21 or 24)
            || !words.All(w => w.Length is >= 3 and <= 8 && w.All(c => c is >= 'a' and <= 'z'))) return null;
        return string.Join(' ', words);
    }
}

/// <summary>What the Import account sheet opens on when a scan found a key.</summary>
public abstract record ImportPrefill
{
    public sealed record SecretKey(string Key) : ImportPrefill;
    public sealed record RecoveryPhrase(string Phrase) : ImportPrefill;
}
