namespace Signet.Models;

/// <summary>How a wallet's secret key is held, taken from the URI scheme of its <c>secret_keys</c> entry.</summary>
public enum KeyKind
{
    /// <summary><c>unencrypted:edsk...</c> — the secret is in the file in clear.</summary>
    Unencrypted,
    /// <summary><c>encrypted:edesk...</c> — octez-client passphrase encryption; needs the passphrase to sign.</summary>
    Encrypted,
    /// <summary><c>ledger://...</c></summary>
    Ledger,
    /// <summary><c>http(s)://</c>, <c>tcp://</c>, <c>unix:</c>, <c>remote:</c> — a remote signer such as octez-signer.</summary>
    Remote,
    /// <summary>Some other scheme we do not understand.</summary>
    Unknown,
    /// <summary>No <c>secret_keys</c> entry: watch-only.</summary>
    None,
}

public static class KeyKindExtensions
{
    public static KeyKind FromLocator(string? locator)
    {
        if (locator is null) return KeyKind.None;
        var colon = locator.IndexOf(':');
        if (colon < 0) return KeyKind.Unknown;
        return locator[..colon].ToLowerInvariant() switch
        {
            "unencrypted" => KeyKind.Unencrypted,
            "encrypted" => KeyKind.Encrypted,
            "ledger" => KeyKind.Ledger,
            "http" or "https" or "tcp" or "unix" or "remote" => KeyKind.Remote,
            _ => KeyKind.Unknown,
        };
    }

    /// <summary>
    /// Kinds Signet can sign with: clear-text keys, encrypted keys once the user supplies the
    /// password, and Ledger keys when the device is plugged in.
    /// </summary>
    public static bool CanSign(this KeyKind kind) => kind is KeyKind.Unencrypted or KeyKind.Encrypted or KeyKind.Ledger;

    /// <summary>Base58 prefixes of octez-encrypted secret keys.</summary>
    public static readonly IReadOnlySet<string> EncryptedKeyPrefixes =
        new HashSet<string>(StringComparer.Ordinal) { "edesk", "spesk", "p2esk", "BLesk", "mdesk" };

    /// <summary>The <c>secret_keys</c> locator for a base58 secret key: <c>encrypted:</c> for edesk-style keys, else <c>unencrypted:</c>.</summary>
    public static string Locator(string secretKey) =>
        (EncryptedKeyPrefixes.Contains(secretKey.Length >= 5 ? secretKey[..5] : secretKey) ? "encrypted:" : "unencrypted:") + secretKey;
}

/// <summary>
/// One address with the alias the user gave it: an octez-client alias. The app holds many
/// wallets, possibly of different schemes, and shows one at a time.
/// </summary>
public sealed record Wallet
{
    public Wallet(string alias, Address address, AddressScheme? scheme = null, string? publicKey = null,
                  KeyKind keyKind = KeyKind.None, LedgerKey? ledgerKey = null)
    {
        Alias = alias;
        Address = address;
        Scheme = scheme ?? address.Scheme ?? AddressScheme.Tz1;
        PublicKey = publicKey;
        KeyKind = keyKind;
        LedgerKey = ledgerKey;
    }

    /// <summary>Aliases are unique within a client directory, so the alias is the identity.</summary>
    public string Id => Alias;

    public string Alias { get; init; }
    public Address Address { get; init; }
    public AddressScheme Scheme { get; init; }
    /// <summary>Base58 public key. <c>null</c> when the client directory has no <c>public_keys</c> entry.</summary>
    public string? PublicKey { get; init; }
    public KeyKind KeyKind { get; init; }
    /// <summary>Where the key lives on a Ledger, when <c>KeyKind</c> is <c>Ledger</c> and the locator parsed.</summary>
    public LedgerKey? LedgerKey { get; init; }
}
