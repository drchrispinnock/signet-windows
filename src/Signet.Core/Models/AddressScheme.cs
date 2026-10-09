namespace Signet.Models;

/// <summary>
/// The signature scheme behind a Tezos implicit account, identified by its address prefix.
///
/// Every known prefix is listed so the rest of the app can reason about it, but only the
/// schemes marked <c>IsSupported</c> can be created or signed with today. tz5 (ML-DSA-44) and
/// tz6 (XMSS) keys made by octez-client are still shown and their balances fetched.
/// </summary>
public enum AddressScheme { Tz1, Tz2, Tz3, Tz4, Tz5, Tz6 }

public static class AddressSchemeExtensions
{
    public static readonly IReadOnlyList<AddressScheme> AllCases = Enum.GetValues<AddressScheme>();

    /// <summary>The address prefix, e.g. <c>tz1</c> (Swift's raw value).</summary>
    public static string RawValue(this AddressScheme scheme) => scheme.ToString().ToLowerInvariant();

    public static string DisplayName(this AddressScheme scheme) => scheme switch
    {
        AddressScheme.Tz1 => "Ed25519",
        AddressScheme.Tz2 => "secp256k1",
        AddressScheme.Tz3 => "P-256",
        AddressScheme.Tz4 => "BLS12-381",
        AddressScheme.Tz5 => "ML-DSA-44 (post-quantum)",
        _ => "XMSS (post-quantum)",
    };

    /// <summary>Base58 prefix of public keys for this scheme (edpk, sppk, p2pk, BLpk, mdpk, xmpk).</summary>
    public static string PublicKeyPrefix(this AddressScheme scheme) => scheme switch
    {
        AddressScheme.Tz1 => "edpk",
        AddressScheme.Tz2 => "sppk",
        AddressScheme.Tz3 => "p2pk",
        AddressScheme.Tz4 => "BLpk",
        AddressScheme.Tz5 => "mdpk",
        _ => "xmpk",
    };

    /// <summary>
    /// Whether this app can currently generate keys and sign for the scheme.
    /// tz5 (ML-DSA-44) arrived with Taquito 25.1 (beta); tz6 (XMSS) is still unsupported.
    /// </summary>
    public static bool IsSupported(this AddressScheme scheme) => scheme != AddressScheme.Tz6;

    /// <summary>A warning worth showing when creating a key of this scheme.</summary>
    public static string? Caveat(this AddressScheme scheme) => scheme switch
    {
        AddressScheme.Tz5 => "Post-quantum tz5 accounts are live on Shadownet and other testnets but still behind a feature flag on Mainnet, so a tz5 account cannot receive mainnet tez yet.",
        _ => null,
    };

    /// <summary>Shown in the UI next to schemes that cannot be created yet.</summary>
    public static string? UnavailableReason(this AddressScheme scheme) => scheme switch
    {
        AddressScheme.Tz6 => "XMSS keys are stateful and not yet supported by the wallet libraries. Use octez-client to create one; Signet will show it.",
        _ => null,
    };

    /// <summary>Parses a raw value such as <c>tz1</c>, or <c>null</c>.</summary>
    public static AddressScheme? FromRawValue(string? raw)
    {
        foreach (var scheme in AllCases)
            if (scheme.RawValue() == raw) return scheme;
        return null;
    }

    /// <summary>Detects the scheme from an address string, or <c>null</c> if the prefix is not an implicit account.</summary>
    public static AddressScheme? FromAddress(string address) =>
        address.Length < 3 ? null : FromRawValue(address[..3]);
}
