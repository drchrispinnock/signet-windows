using System.Text.Json;

namespace Signet.Models;

/// <summary>
/// The curves the Tezos Wallet app on a Ledger can derive keys for. Names and derivation types
/// follow octez-client (<c>ledger://…/&lt;curve&gt;/…</c>) and Taquito's <c>DerivationType</c>.
/// </summary>
public enum LedgerCurve { Ed25519, Secp256k1, P256, Bip25519 }

public static class LedgerCurveExtensions
{
    public static readonly IReadOnlyList<LedgerCurve> AllCases = Enum.GetValues<LedgerCurve>();

    /// <summary>Swift's raw value and <c>Identifiable</c> id.</summary>
    public static string RawValue(this LedgerCurve curve) => curve switch
    {
        LedgerCurve.Ed25519 => "ed25519",
        LedgerCurve.Secp256k1 => "secp256k1",
        LedgerCurve.P256 => "p256",
        _ => "bip25519",
    };

    /// <summary>The name octez-client writes in the locator.</summary>
    public static string OctezName(this LedgerCurve curve) => curve switch
    {
        LedgerCurve.P256 => "P-256",
        _ => curve.RawValue(),
    };

    /// <summary>Taquito's <c>DerivationType</c> number.</summary>
    public static int DerivationType(this LedgerCurve curve) => (int)curve;

    public static AddressScheme Scheme(this LedgerCurve curve) => curve switch
    {
        LedgerCurve.Secp256k1 => AddressScheme.Tz2,
        LedgerCurve.P256 => AddressScheme.Tz3,
        _ => AddressScheme.Tz1,
    };

    public static string DisplayName(this LedgerCurve curve) => curve switch
    {
        LedgerCurve.Ed25519 => "Ed25519 (tz1)",
        LedgerCurve.Secp256k1 => "secp256k1 (tz2)",
        LedgerCurve.P256 => "P-256 (tz3)",
        _ => "BIP32-Ed25519 (tz1)",
    };

    /// <summary>Parses octez's spellings, case-insensitively: ed25519, secp256k1, P-256 / secp256r1 / p256, bip25519 / bip32-ed25519.</summary>
    public static LedgerCurve? FromOctezName(string name) => name.ToLowerInvariant() switch
    {
        "ed25519" => LedgerCurve.Ed25519,
        "secp256k1" => LedgerCurve.Secp256k1,
        "p-256" or "p256" or "secp256r1" => LedgerCurve.P256,
        "bip25519" or "bip32-ed25519" => LedgerCurve.Bip25519,
        _ => null,
    };
}

/// <summary>
/// A key held on a Ledger: which device (octez's "ledger id": the root key's tz1 address, or
/// its four-animal name), which curve, and the BIP32 path under <c>44'/1729'</c>.
///
/// Locator form, as octez-client writes it: <c>ledger://&lt;id&gt;/&lt;curve&gt;/0h/0h</c> where the path is
/// relative to <c>44h/1729h</c>. <c>h</c> and <c>'</c> both mark hardened indexes; an explicit <c>44h/1729h</c>
/// prefix is accepted too.
/// </summary>
public sealed class LedgerKey : IEquatable<LedgerKey>
{
    public static readonly IReadOnlyList<string> RootPath = ["44'", "1729'"];

    public string RootID { get; init; }
    public LedgerCurve Curve { get; init; }
    /// <summary>Path components below <c>44'/1729'</c>, normalised to <c>'</c> (e.g. <c>["0'", "0'"]</c>).</summary>
    public IReadOnlyList<string> RelativePath { get; init; }

    public LedgerKey(string rootID, LedgerCurve curve, IEnumerable<string> relativePath)
    {
        RootID = rootID;
        Curve = curve;
        RelativePath = relativePath.Select(Normalise).ToArray();
    }

    /// <summary>The default path for account <c>n</c>: <c>44'/1729'/n'/0'</c>.</summary>
    public LedgerKey(string rootID, LedgerCurve curve, int account)
        : this(rootID, curve, [$"{account}'", "0'"]) { }

    public static LedgerKey? FromLocator(string locator)
    {
        const string scheme = "ledger://";
        if (!locator.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return null;
        var parts = locator[scheme.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count < 2 || LedgerCurveExtensions.FromOctezName(parts[1]) is not { } curve) return null;
        var root = parts[0];
        var path = parts.Skip(2).Select(Normalise).ToList();
        if (path.Count >= 2 && path[0] == "44'" && path[1] == "1729'") path.RemoveRange(0, 2);
        if (!path.All(c => int.TryParse(c.EndsWith('\'') ? c[..^1] : c, out _))) return null;
        return new LedgerKey(root, curve, path);
    }

    private static string Normalise(string component) =>
        component.EndsWith('h') || component.EndsWith('H') ? component[..^1] + "'" : component;

    /// <summary>Full BIP32 path as Taquito wants it: <c>44'/1729'/0'/0'</c>.</summary>
    public string FullPath => string.Join('/', RootPath.Concat(RelativePath));

    /// <summary>The <c>secret_keys</c> / <c>public_keys</c> locator in octez-client's form (<c>h</c> for hardened).</summary>
    public string Locator =>
        string.Join('/', new[] { $"ledger://{RootID}", Curve.OctezName() }.Concat(RelativePath.Select(c => c.Replace("'", "h"))));

    /// <summary>The account index when the path is the usual <c>n'/0'</c>.</summary>
    public int? Account =>
        RelativePath.Count == 2 && RelativePath[1] == "0'" && int.TryParse(RelativePath[0][..^1], out var n) ? n : null;

    public bool Equals(LedgerKey? other) =>
        other is not null && RootID == other.RootID && Curve == other.Curve && RelativePath.SequenceEqual(other.RelativePath);
    public override bool Equals(object? obj) => Equals(obj as LedgerKey);
    public override int GetHashCode() => HashCode.Combine(RootID, Curve, string.Join('/', RelativePath));
    public static bool operator ==(LedgerKey? a, LedgerKey? b) => a?.Equals(b) ?? b is null;
    public static bool operator !=(LedgerKey? a, LedgerKey? b) => !(a == b);
}

/// <summary>What an operation is signed with. Built on demand for the one operation and not kept.</summary>
public abstract record SigningKey
{
    /// <summary>
    /// A base58 secret key from the wallet directory, with the password if it is encrypted, and
    /// the address it must derive: the bridge refuses to sign if the key is somebody else's.
    /// </summary>
    public sealed record Secret(string Key, string? Passphrase, Address Address) : SigningKey;
    /// <summary>A key on a Ledger; the device signs after the user approves on it.</summary>
    public sealed record Ledger(LedgerKey Key, Address Address) : SigningKey;

    public bool IsLedger => this is Ledger;

    /// <summary>The JSON the bridge's <c>signerFor</c> understands.</summary>
    public string BridgeSpec => this switch
    {
        Secret s => JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["kind"] = "secret", ["secretKey"] = s.Key, ["passphrase"] = s.Passphrase ?? "", ["address"] = s.Address.Value,
        }),
        Ledger l => JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["kind"] = "ledger", ["path"] = l.Key.FullPath, ["derivationType"] = l.Key.Curve.DerivationType(), ["address"] = l.Address.Value,
        }),
        _ => throw new InvalidOperationException(),
    };

    // Never print secrets: records would otherwise include the key in ToString().
    protected virtual bool PrintMembers(System.Text.StringBuilder builder) => false;
}
