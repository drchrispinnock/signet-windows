using Signet.Models;

namespace Signet.Services;

/// <summary>What a pasted secret key turned out to be.</summary>
/// <param name="NeedsPassphrase">An octez-encrypted key (edesk…) that needs its password before anything is known.</param>
/// <param name="SecretKey">The key as it should be stored: the pasted key, or an ed25519 seed when a 64-byte key was pasted.</param>
public sealed record InspectedSecretKey(bool NeedsPassphrase, bool IsEncrypted, string? PublicKey, Address? Address, string? SecretKey)
{
    // Never print secrets: records would otherwise include the key in ToString().
    private bool PrintMembers(System.Text.StringBuilder builder) => false;
}

public sealed record MnemonicCheck(bool IsValid, int WordCount, IReadOnlyList<string> UnknownWords)
{
    public bool Equals(MnemonicCheck? other) =>
        other is not null && IsValid == other.IsValid && WordCount == other.WordCount && UnknownWords.SequenceEqual(other.UnknownWords);
    public override int GetHashCode() => HashCode.Combine(IsValid, WordCount);
}

/// <summary>Derivation curves a recovery phrase can produce keys on (Taquito's names).</summary>
public enum MnemonicCurve { Ed25519, Secp256k1, P256, Bip25519 }

public static class MnemonicCurveExtensions
{
    public static IReadOnlyList<MnemonicCurve> AllCases { get; } = Enum.GetValues<MnemonicCurve>();

    public static string RawValue(this MnemonicCurve curve) => curve switch
    {
        MnemonicCurve.Ed25519 => "ed25519",
        MnemonicCurve.Secp256k1 => "secp256k1",
        MnemonicCurve.P256 => "p256",
        _ => "bip25519",
    };

    public static string Title(this MnemonicCurve curve) => curve switch
    {
        MnemonicCurve.Ed25519 => "Ed25519 (tz1) — most wallets",
        MnemonicCurve.Secp256k1 => "secp256k1 (tz2)",
        MnemonicCurve.P256 => "P-256 (tz3)",
        _ => "BIP32-Ed25519 (tz1) — Ledger style",
    };

    public static AddressScheme Scheme(this MnemonicCurve curve) => curve switch
    {
        MnemonicCurve.Secp256k1 => AddressScheme.Tz2,
        MnemonicCurve.P256 => AddressScheme.Tz3,
        _ => AddressScheme.Tz1,
    };
}

/// <summary>Turns pasted secrets into key material, through the bridge (Taquito's own derivation).</summary>
public interface IKeyImporter
{
    Task<InspectedSecretKey> Inspect(string secretKey, string? passphrase);
    Task<MnemonicCheck> Check(string mnemonic);
    Task<KeyMaterial> KeyFromMnemonic(string mnemonic, string? passphrase, string derivationPath, MnemonicCurve curve);
    /// <summary>The old fundraiser / non-HD derivation (email + password + phrase).</summary>
    Task<KeyMaterial> KeyFromFundraiser(string email, string password, string mnemonic);
    /// <summary>The clear key behind an octez-encrypted one (for Export).</summary>
    Task<string> Decrypt(string secretKey, string passphrase);
}

public sealed class BridgeKeyImporter(TaquitoBridge? bridge = null) : IKeyImporter
{
    private readonly TaquitoBridge bridge = bridge ?? TaquitoBridge.Shared;

    public const string DefaultDerivationPath = "44'/1729'/0'/0'";

    public async Task<InspectedSecretKey> Inspect(string secretKey, string? passphrase)
    {
        var r = await TaquitoBridge.BridgeError.Typed(() => bridge.Call("inspectSecretKey", secretKey, passphrase ?? ""));
        return new InspectedSecretKey(
            r["needsPassphrase"]?.BoolValue ?? false,
            r["encrypted"]?.BoolValue ?? false,
            r["publicKey"]?.StringValue,
            r["address"]?.StringValue is { } address ? new Address(address) : null,
            r["secretKey"]?.StringValue);
    }

    public async Task<MnemonicCheck> Check(string mnemonic)
    {
        var r = await bridge.Call("validateMnemonic", mnemonic);
        return new MnemonicCheck(r["valid"]?.BoolValue ?? false, (int)(r["wordCount"]?.DoubleValue ?? 0),
                                 (r["unknownWords"]?.ArrayValue ?? []).Select(w => w.StringValue).OfType<string>().ToList());
    }

    public async Task<KeyMaterial> KeyFromMnemonic(string mnemonic, string? passphrase, string derivationPath, MnemonicCurve curve) =>
        Material(curve.Scheme(), await bridge.Call("keyFromMnemonic", mnemonic, passphrase ?? "", derivationPath, curve.RawValue()));

    public async Task<KeyMaterial> KeyFromFundraiser(string email, string password, string mnemonic) =>
        Material(AddressScheme.Tz1, await bridge.Call("keyFromFundraiser", email, password, mnemonic));

    public Task<string> Decrypt(string secretKey, string passphrase) =>
        TaquitoBridge.BridgeError.Typed(async () =>
        {
            var r = await bridge.Call("decryptSecretKey", secretKey, passphrase);
            return r["secretKey"]?.StringValue ?? throw new TaquitoBridge.BridgeError.JavaScript("no key returned");
        });

    private static KeyMaterial Material(AddressScheme scheme, JSONValue r)
    {
        if (r["secretKey"]?.StringValue is not { } sk || r["publicKey"]?.StringValue is not { } pk || r["address"]?.StringValue is not { } address)
            throw new TaquitoBridge.BridgeError.JavaScript("unexpected key payload");
        return new KeyMaterial(new Address(address).Scheme ?? scheme, pk, address, sk);
    }
}

/// <summary>For tests and previews: fixed answers, no bridge.</summary>
public sealed class MockKeyImporter : IKeyImporter
{
    public Task<InspectedSecretKey> Inspect(string secretKey, string? passphrase)
    {
        if (secretKey.StartsWith("edesk", StringComparison.Ordinal))
        {
            if (passphrase is null) return Task.FromResult(new InspectedSecretKey(true, true, null, null, null));
            if (passphrase != "correct horse") throw new ChainError.WrongPassphrase();
            return Task.FromResult(new InspectedSecretKey(false, true, "edpkEnc", new Address("tz1EncMockMockMockMockMockMockMockMo"), secretKey));
        }
        if (!secretKey.StartsWith("edsk", StringComparison.Ordinal)) throw new TaquitoBridge.BridgeError.JavaScript("Invalid secret key");
        return Task.FromResult(new InspectedSecretKey(false, false, "edpkClear", new Address("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb"), secretKey));
    }

    public Task<MnemonicCheck> Check(string mnemonic)
    {
        var words = mnemonic.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return Task.FromResult(new MnemonicCheck(words.Length is 12 or 15 or 18 or 21 or 24, words.Length, []));
    }

    public Task<KeyMaterial> KeyFromMnemonic(string mnemonic, string? passphrase, string derivationPath, MnemonicCurve curve) =>
        Task.FromResult(new KeyMaterial(curve.Scheme(), "edpkMnemonic", "tz1dnCbNYHDoxmHss9kEQVjWQj6GvYX4gYp5", "edskMnemonic"));

    public Task<KeyMaterial> KeyFromFundraiser(string email, string password, string mnemonic) =>
        Task.FromResult(new KeyMaterial(AddressScheme.Tz1, "edpkFund", "tz1FundMockMockMockMockMockMockMockMo", "edskFund"));

    public Task<string> Decrypt(string secretKey, string passphrase) =>
        passphrase == "correct horse" ? Task.FromResult("edskDECRYPTED") : throw new ChainError.WrongPassphrase();
}
