namespace Signet.Models;

/// <summary>
/// A Tezos address with helpers for display. Validation of the base58 payload lives in the
/// chain service; this type only understands the textual shape.
/// </summary>
public readonly record struct Address(string Value)
{
    public AddressScheme? Scheme => AddressSchemeExtensions.FromAddress(Value);

    /// <summary>Prefixes that denote an account something can be sent to: implicit (tz1–tz6) or contract (KT1).</summary>
    public static readonly IReadOnlySet<string> AccountPrefixes =
        AddressSchemeExtensions.AllCases.Select(s => s.RawValue()).Append("KT1").ToHashSet();

    /// <summary>
    /// True when this is a well-formed Tezos account address: known prefix, 20-byte hash, valid checksum.
    /// Works for every scheme including tz5/tz6, which Taquito does not yet know.
    /// </summary>
    public bool IsValidAccount =>
        Value.Length == 36 && AccountPrefixes.Contains(Value[..3]) && Base58.CheckDecode(Value) is { Length: 3 + 20 };

    public bool IsContract => Value.StartsWith("KT1", StringComparison.Ordinal);

    /// <summary>
    /// Shortened form for headers, e.g. <c>tz1VSUr8wwN...Th8Cjcjb</c>. Keeps the full prefix so the
    /// scheme stays visible and enough of each end to tell similar addresses apart.
    /// </summary>
    public string Shortened(int prefix = 11, int suffix = 8) =>
        Value.Length > prefix + suffix + 3 ? $"{Value[..prefix]}...{Value[^suffix..]}" : Value;

    public override string ToString() => Value;
}
