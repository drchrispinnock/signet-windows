namespace Signet.Models;

/// <summary>
/// A freshly generated key pair in Tezos base58 form. The secret key is only ever held in
/// memory long enough to be written to the secret key store.
/// </summary>
/// <param name="PublicKey">Base58 public key, e.g. <c>edpk...</c>, <c>sppk...</c>, <c>p2pk...</c>, <c>BLpk...</c>.</param>
/// <param name="Address">Base58 address derived from the public key, e.g. <c>tz1...</c>.</param>
/// <param name="SecretKey">Base58 secret key, e.g. <c>edsk...</c> (seed form), <c>spsk...</c>, <c>p2sk...</c>, <c>BLsk...</c>.</param>
public sealed record KeyMaterial(AddressScheme Scheme, string PublicKey, string Address, string SecretKey);
