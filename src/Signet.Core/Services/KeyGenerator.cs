using System.Security.Cryptography;
using NSec.Cryptography;
using Signet.Models;

namespace Signet.Services;

/// <summary>
/// Generates key pairs for each address scheme.
///
/// tz1 (Ed25519, via NSec/libsodium) and tz3 (P-256, via .NET's ECDsa) are generated and encoded
/// in C#, so those secrets never enter the JavaScript runtime; only the public key is sent to the
/// bridge to derive the address. tz2 (secp256k1), tz4 (BLS12-381) and tz5 (ML-DSA-44) are generated
/// inside the Taquito bridge with the libraries Taquito signs with. (Upstream uses CryptoKit for the
/// same split.)
/// </summary>
public sealed class KeyGenerator(TaquitoBridge? bridge = null)
{
    public abstract class KeyError(string message) : Exception(message)
    {
        public sealed class UnsupportedScheme(AddressScheme scheme) : KeyError($"{scheme.RawValue()} keys cannot be created yet.");
        public sealed class BadBridgeResponse(string detail) : KeyError($"Unexpected response from the Taquito bridge: {detail}");
    }

    private readonly TaquitoBridge bridge = bridge ?? TaquitoBridge.Shared;

    public async Task<KeyMaterial> Generate(AddressScheme scheme)
    {
        switch (scheme)
        {
            case AddressScheme.Tz1:
            {
                using var key = Key.Create(SignatureAlgorithm.Ed25519,
                    new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
                var seed = key.Export(KeyBlobFormat.RawPrivateKey);
                var secret = Base58.CheckEncode(TezosPrefix.Edsk, seed);
                CryptographicOperations.ZeroMemory(seed);
                var publicKey = Base58.CheckEncode(TezosPrefix.Edpk, key.PublicKey.Export(KeyBlobFormat.RawPublicKey));
                return new KeyMaterial(AddressScheme.Tz1, publicKey, await Address(publicKey), secret);
            }

            case AddressScheme.Tz3:
            {
                using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                var parameters = key.ExportParameters(includePrivateParameters: true);
                var d = LeftPad(parameters.D!, 32);
                var secret = Base58.CheckEncode(TezosPrefix.P2sk, d);
                CryptographicOperations.ZeroMemory(d);
                CryptographicOperations.ZeroMemory(parameters.D);
                // Compressed SEC1 point: 02/03 by the parity of y, then x.
                var x = LeftPad(parameters.Q.X!, 32);
                var y = parameters.Q.Y!;
                byte[] compressed = [(byte)((y[^1] & 1) == 0 ? 0x02 : 0x03), .. x];
                var publicKey = Base58.CheckEncode(TezosPrefix.P2pk, compressed);
                return new KeyMaterial(AddressScheme.Tz3, publicKey, await Address(publicKey), secret);
            }

            case AddressScheme.Tz2 or AddressScheme.Tz4 or AddressScheme.Tz5:
            {
                // No platform support for secp256k1, BLS12-381 or ML-DSA-44: the bridge generates these
                // with the same libraries Taquito signs with.
                var result = await bridge.Call("generateKeyPair", scheme.RawValue());
                if (result["secretKey"]?.StringValue is not { } secret ||
                    result["publicKey"]?.StringValue is not { } publicKey ||
                    result["address"]?.StringValue is not { } address)
                    throw new KeyError.BadBridgeResponse(result.ToString());
                return new KeyMaterial(scheme, publicKey, address, secret);
            }

            default:
                throw new KeyError.UnsupportedScheme(scheme);
        }
    }

    /// <summary>Encrypts a clear-text secret key with <c>passphrase</c> in octez-client's format (edesk/spesk/p2esk/BLesk).</summary>
    public async Task<string> Encrypt(string secretKey, string passphrase)
    {
        var result = await bridge.Call("encryptSecretKey", secretKey, passphrase);
        if (result.StringValue is not { Length: >= 5 } encrypted || !KeyKindExtensions.EncryptedKeyPrefixes.Contains(encrypted[..5]))
            throw new KeyError.BadBridgeResponse(result.ToString());
        return encrypted;
    }

    /// <summary>Derives the tz address for a base58 public key. Public data only.</summary>
    public async Task<string> Address(string publicKey)
    {
        var result = await bridge.Call("addressFromPublicKey", publicKey);
        return result.StringValue ?? throw new KeyError.BadBridgeResponse(result.ToString());
    }

    private static byte[] LeftPad(byte[] bytes, int length) =>
        bytes.Length >= length ? bytes : [.. new byte[length - bytes.Length], .. bytes];
}
