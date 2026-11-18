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
    /// <summary>
    /// <c>airgap://&lt;fingerprint&gt;/&lt;path&gt;</c> — the key is in AirGap Vault on a phone;
    /// Signet holds the public key, and operations go to the Vault and back by QR code.
    /// </summary>
    Airgap,
    /// <summary>
    /// <c>http(s)://</c>, <c>tcp://</c>, <c>unix:</c> — a remote signer such as octez-signer, holding the
    /// key elsewhere; Signet sends what is to be signed and checks the signature it gets back.
    /// (<c>remote:tz1…</c> names a signer configured in octez-client and is <c>Unknown</c> here.)
    /// </summary>
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
            "airgap" => KeyKind.Airgap,
            "http" or "https" or "tcp" or "unix" => KeyKind.Remote,
            _ => KeyKind.Unknown,
        };
    }

    /// <summary>
    /// Kinds Signet can sign operations with: clear-text keys, encrypted keys once the user
    /// supplies the password, Ledger keys when the device is plugged in, AirGap Vault keys by a QR
    /// round trip with the phone, and keys on a remote signer.
    /// </summary>
    public static bool CanSign(this KeyKind kind) => kind is KeyKind.Unencrypted or KeyKind.Encrypted or KeyKind.Ledger or KeyKind.Airgap or KeyKind.Remote;

    /// <summary>
    /// Kinds that can also sign raw bytes without the operation watermark: a dApp's sign_payload,
    /// a multisig proposal, Mt Pelerin's address check. AirGap Vault's transaction sign request
    /// carries operations only.
    /// </summary>
    public static bool SignsMessages(this KeyKind kind) => kind.CanSign() && kind != KeyKind.Airgap;

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
                  KeyKind keyKind = KeyKind.None, LedgerKey? ledgerKey = null, RemoteSignerLocator? remoteSigner = null)
    {
        Alias = alias;
        Address = address;
        Scheme = scheme ?? address.Scheme ?? AddressScheme.Tz1;
        PublicKey = publicKey;
        KeyKind = keyKind;
        LedgerKey = ledgerKey;
        RemoteSigner = remoteSigner;
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
    /// <summary>Where the key is held when <c>KeyKind</c> is <c>Remote</c> and the locator parsed.</summary>
    public RemoteSignerLocator? RemoteSigner { get; init; }
    /// <summary>
    /// A paper key imported for this run only: its secret is in memory (<c>SessionKeyStore</c>), not
    /// in the wallet directory, and is gone when Signet quits.
    /// </summary>
    public bool IsSessionOnly { get; init; }
    /// <summary>
    /// The BLS proof of possession a tz4 account's reveal carries (<c>BLsig…</c>, the key's signature
    /// over its own public key), once made with the secret key. Only an estimate for an
    /// unrevealed tz4 account needs it; it is never stored (the reveal publishes it on chain).
    /// </summary>
    public string? ProofOfPossession { get; init; }
}
