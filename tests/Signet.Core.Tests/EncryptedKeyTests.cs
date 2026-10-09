using Signet.Models;
using Signet.Services;

namespace Signet.Tests;

public class EncryptedKeyTests
{
    [Theory]
    [InlineData(AddressScheme.Tz1)]
    [InlineData(AddressScheme.Tz2)]
    [InlineData(AddressScheme.Tz3)]
    [InlineData(AddressScheme.Tz4)]
    [InlineData(AddressScheme.Tz5)]
    public async Task EncryptsInOctezFormatAndDecryptsWithThePassphrase(AddressScheme scheme)
    {
        var generator = new KeyGenerator();
        var material = await generator.Generate(scheme);
        var encrypted = await generator.Encrypt(material.SecretKey, "correct horse battery staple");

        var expectedPrefix = new Dictionary<string, string>
        {
            ["tz1"] = "edesk", ["tz2"] = "spesk", ["tz3"] = "p2esk", ["tz4"] = "BLesk", ["tz5"] = "mdesk",
        }[scheme.RawValue()];
        Assert.StartsWith(expectedPrefix, encrypted);
        Assert.StartsWith("encrypted:", KeyKindExtensions.Locator(encrypted));
        Assert.StartsWith("unencrypted:", KeyKindExtensions.Locator(material.SecretKey));

        // Taquito's own signer must open it and land on the same key.
        var info = await TaquitoBridge.Shared.Call("keyInfoFromSecretKey", encrypted, "correct horse battery staple");
        Assert.Equal(new JSONValue.String(material.Address), info["address"]);
        Assert.Equal(new JSONValue.String(material.PublicKey), info["publicKey"]);

        await Assert.ThrowsAnyAsync<TaquitoBridge.BridgeError>(() => TaquitoBridge.Shared.Call("keyInfoFromSecretKey", encrypted, "wrong"));
    }

    [Fact]
    public void StoreKeepsEncryptedLocatorAndKind()
    {
        var dir = TestSupport.TempDir("enc");
        var store = new TezosClientStore(dir);
        var wallet = new Wallet("vault", new Address("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb"), publicKey: "edpkV", keyKind: KeyKind.Encrypted);
        store.AddWithSecretKey(wallet, "edeskFAKEENCRYPTED");

        var loaded = Assert.Single(store.Load());
        Assert.Equal(KeyKind.Encrypted, loaded.KeyKind);
        Assert.Equal("edeskFAKEENCRYPTED", store.SecretKey(loaded));
        var raw = File.ReadAllText(Path.Combine(dir, "secret_keys"));
        Assert.Contains("\"encrypted:edeskFAKEENCRYPTED\"", raw);
    }
}

public class EncryptedWalletFlowTests
{
    [Fact(Skip = Pending.ViewModel)] public void CreateWalletWithPassphraseStoresAnEncryptedKey() { }
    [Fact(Skip = "Pending port: SendViewModel")] public void SendingFromAnEncryptedWalletNeedsTheRightPassword() { }
}
