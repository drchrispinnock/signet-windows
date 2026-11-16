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
    /// <summary>
    /// One of several codes that together carry a secret key too long for one (a tz5 key on a
    /// recovery sheet); <c>KeyPartCollector</c> puts them together.
    /// </summary>
    public sealed record KeyPartContent(KeyPart Part) : QRPayload;
    public sealed record Unknown : QRPayload;

    public static readonly string[] PublicKeyPrefixes = ["edpk", "sppk", "p2pk", "BLpk", "mdpk"];
    public static readonly string[] SecretKeyPrefixes = ["edsk", "spsk", "p2sk", "BLsk", "mdsk", "edesk", "spesk", "p2esk", "BLesk", "mdesk"];
    /// <summary>Longer than any QR code can carry; anything bigger is not worth decoding.</summary>
    public const int MaximumLength = 8192;

    /// <summary>True for the cases that carry a secret, which must never be shown or logged.</summary>
    public bool IsSecret => this is SecretKey or RecoveryPhrase or KeyPartContent;

    public static QRPayload Classify(string raw)
    {
        var text = raw.Trim();
        if (text.Length == 0 || System.Text.Encoding.UTF8.GetByteCount(text) > MaximumLength) return new Unknown();

        if (text.StartsWith(KeyPart.Scheme, StringComparison.Ordinal))
            return KeyPart.FromText(text) is { } part ? new KeyPartContent(part) : new Unknown();
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

/// <summary>
/// One QR code of a secret key printed across several: <c>signet-key:&lt;index&gt;/&lt;count&gt;:&lt;id&gt;:&lt;chunk&gt;</c>.
/// The id is the same on every part of one key, so parts of different keys are never joined,
/// and the joined text must still pass the key's own base58 checksum. The format is Signet's
/// own; no other wallet reads it.
/// </summary>
/// <param name="Index">1-based.</param>
public sealed record KeyPart(int Index, int Count, string Id, string Chunk)
{
    public const string Scheme = "signet-key:";
    public const int MaximumParts = 32;
    public const int MaximumChunkLength = 2000;
    /// <summary>Short enough for a code a webcam reads easily from a printed page (QR version 18 at level L).</summary>
    public const int DefaultChunkLength = 680;


    public string Text => $"{Scheme}{Index}/{Count}:{Id}:{Chunk}";

    /// <summary>The part a scanned text carries, or <c>null</c> when it is not a well-formed one.</summary>
    public static KeyPart? FromText(string text)
    {
        if (!text.StartsWith(Scheme, StringComparison.Ordinal)) return null;
        var fields = text[Scheme.Length..].Split(':');
        if (fields.Length != 3) return null;
        var numbers = fields[0].Split('/');
        if (numbers.Length != 2
            || !int.TryParse(numbers[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index)
            || !int.TryParse(numbers[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count)
            || count is < 2 or > MaximumParts || index < 1 || index > count
            || fields[1].Length != 8 || !fields[1].All(char.IsAsciiHexDigit)
            || fields[2].Length == 0 || fields[2].Length > MaximumChunkLength
            || !fields[2].All(char.IsAsciiLetterOrDigit)) return null;
        return new KeyPart(index, count, fields[1], fields[2]);
    }

    /// <summary>What ties the parts of one key together: the start of the key's SHA-256.</summary>
    public static string IdFor(string key) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)).AsSpan(0, 4));

    /// <summary>
    /// The key cut into equal parts of at most <c>chunkLength</c> characters; empty when it would
    /// take more parts than a sheet should carry, or fits one code anyway.
    /// </summary>
    public static IReadOnlyList<KeyPart> Split(string key, int chunkLength = DefaultChunkLength)
    {
        var count = (key.Length + chunkLength - 1) / chunkLength;
        if (count is < 2 or > MaximumParts) return [];
        var size = (key.Length + count - 1) / count;
        var id = IdFor(key);
        var parts = new List<KeyPart>();
        for (var index = 1; index <= count; index++)
        {
            var start = Math.Min((index - 1) * size, key.Length);
            parts.Add(new KeyPart(index, count, id, key.Substring(start, Math.Min(size, key.Length - start))));
        }
        return parts;
    }
}

/// <summary>Gathers the parts of one key as they are scanned, in any order.</summary>
public sealed class KeyPartCollector
{
    public string? Id { get; private set; }
    public int Count { get; private set; }
    private Dictionary<int, string> chunks = [];

    public abstract record Outcome
    {
        /// <summary>Kept (or already held); more parts are needed.</summary>
        public sealed record NeedMore : Outcome;
        /// <summary>Every part is in and the key they make is sound.</summary>
        public sealed record Complete(string Key) : Outcome;
        /// <summary>Every part is in but they do not make a key; the collector has been emptied.</summary>
        public sealed record Invalid : Outcome;
    }

    public bool IsEmpty => chunks.Count == 0;
    public int Received => chunks.Count;
    /// <summary>The parts still to scan, by number.</summary>
    public IReadOnlyList<int> Missing => Count == 0 ? [] : Enumerable.Range(1, Count).Where(i => !chunks.ContainsKey(i)).ToList();

    /// <summary>Adds a part. A part of another key starts a new collection, dropping the old one.</summary>
    public Outcome Add(KeyPart part)
    {
        if (part.Id != Id || part.Count != Count)
        {
            Id = part.Id;
            Count = part.Count;
            chunks = [];
        }
        chunks[part.Index] = part.Chunk;
        if (Missing.Count > 0) return new Outcome.NeedMore();
        var key = string.Concat(Enumerable.Range(1, Count).Select(i => chunks[i]));
        var wanted = Id;
        Id = null;
        Count = 0;
        chunks = [];
        if (KeyPart.IdFor(key) != wanted || QRPayload.Classify(key) is not QRPayload.SecretKey) return new Outcome.Invalid();
        return new Outcome.Complete(key);
    }
}
