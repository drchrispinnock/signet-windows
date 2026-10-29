using Signet.Models;
using Signet.Services;
using Signet.ViewModels;

namespace Signet.Tests;

public class ImportTests
{
    /// <summary>Live: the Mt Pelerin docs' test phrase derives their documented address at 44'/1729'/0'/0'.</summary>
    [Fact]
    public async Task DerivesTheDocumentedAddressFromAPhrase()
    {
        var importer = new BridgeKeyImporter();
        const string phrase = "bamboo feed assist glove soda merry medal vanish almost solid bean loop";
        var check = await importer.Check(phrase);
        Assert.True(check.IsValid && check.WordCount == 12 && check.UnknownWords.Count == 0);
        var key = await importer.KeyFromMnemonic(phrase, null, BridgeKeyImporter.DefaultDerivationPath, MnemonicCurve.Ed25519);
        Assert.Equal("tz1dnCbNYHDoxmHss9kEQVjWQj6GvYX4gYp5", key.Address);
        Assert.Equal("edpkus9ckWtwxNi7NqZqGT2bo1VGxyRkCvaV5zkms7rkeWUV532PhU", key.PublicKey);
        Assert.True(key.SecretKey.StartsWith("edsk", StringComparison.Ordinal) && key.SecretKey.Length == 54);
        // Other curves and accounts give other addresses.
        var tz2 = await importer.KeyFromMnemonic(phrase, null, BridgeKeyImporter.DefaultDerivationPath, MnemonicCurve.Secp256k1);
        Assert.True(tz2.Address.StartsWith("tz2", StringComparison.Ordinal) && tz2.Scheme == AddressScheme.Tz2);
        var second = await importer.KeyFromMnemonic(phrase, null, "44'/1729'/1'/0'", MnemonicCurve.Ed25519);
        Assert.NotEqual(key.Address, second.Address);
        // A 24-word phrase and a bad checksum.
        var bad = await importer.Check("bamboo feed assist glove soda merry medal vanish almost solid bean bean");
        Assert.True(!bad.IsValid && bad.WordCount == 12);
        var typo = await importer.Check("bamboo feed assist glove soda merry medal vanish almost solid bean looop");
        Assert.Equal(["looop"], typo.UnknownWords);
    }

    /// <summary>Live: a pasted key round-trips, a 64-byte edsk is reduced to its seed, encrypted keys need their password.</summary>
    [Fact]
    public async Task InspectsPastedKeys()
    {
        var importer = new BridgeKeyImporter();
        var generated = await new KeyGenerator().Generate(AddressScheme.Tz1);
        var clear = await importer.Inspect(generated.SecretKey, null);
        Assert.True(clear.Address?.Value == generated.Address && clear.SecretKey == generated.SecretKey && !clear.IsEncrypted);

        var encrypted = await new KeyGenerator().Encrypt(generated.SecretKey, "correct horse battery staple");
        var locked = await importer.Inspect(encrypted, null);
        Assert.True(locked.NeedsPassphrase && locked.IsEncrypted);
        var opened = await importer.Inspect(encrypted, "correct horse battery staple");
        Assert.True(opened.Address?.Value == generated.Address && opened.SecretKey == encrypted);
        await Assert.ThrowsAsync<ChainError.WrongPassphrase>(() => importer.Inspect(encrypted, "nope"));

        var tz2 = await new KeyGenerator().Generate(AddressScheme.Tz2);
        Assert.Equal(tz2.Address, (await importer.Inspect(tz2.SecretKey, null)).Address?.Value);
        await Assert.ThrowsAnyAsync<Exception>(() => importer.Inspect("edskNotAKey", null));
    }

    [Fact]
    public async Task ImportStoresAndSelects()
    {
        var dir = TestSupport.TempDir("import");
        var store = new TezosClientStore(dir);
        var model = new WalletViewModel(chain: new MockChainService(), walletStore: store);
        var material = new KeyMaterial(AddressScheme.Tz1, "edpkX", "tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb", "edskX");
        await model.ImportAccount("Imported", material);
        Assert.Equal("Imported", model.SelectedWallet?.Alias);
        Assert.Equal(KeyKind.Unencrypted, model.SelectedWallet?.KeyKind);
        Assert.Equal("edskX", store.SecretKey(model.SelectedWallet!));

        await Assert.ThrowsAnyAsync<WalletViewModel.WalletError>(() => model.ImportAccount("Again", material));

        var enc = new KeyMaterial(AddressScheme.Tz1, "edpkY", "tz1dnCbNYHDoxmHss9kEQVjWQj6GvYX4gYp5", "edeskALREADY");
        await model.ImportAccount("Vault", enc, alreadyEncrypted: true);
        Assert.Equal(KeyKind.Encrypted, model.SelectedWallet?.KeyKind);
        Assert.Equal("edeskALREADY", store.SecretKey(model.SelectedWallet!));
    }

    /// <summary>Live: an encrypted key decrypts back to the seed it was made from.</summary>
    [Fact]
    public async Task DecryptsForExport()
    {
        var generated = await new KeyGenerator().Generate(AddressScheme.Tz1);
        var encrypted = await new KeyGenerator().Encrypt(generated.SecretKey, "correct horse battery staple");
        Assert.Equal(generated.SecretKey, await new BridgeKeyImporter().Decrypt(encrypted, "correct horse battery staple"));
        await Assert.ThrowsAsync<ChainError.WrongPassphrase>(() => new BridgeKeyImporter().Decrypt(encrypted, "nope"));
    }

    [Fact]
    public async Task ExportNeedsThePasswordForEncryptedKeys()
    {
        var store = new InMemoryWalletStore();
        store.AddWithSecretKey(new Wallet("clear", new Address("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb"), publicKey: "edpkX", keyKind: KeyKind.Unencrypted), "edskCLEAR");
        store.AddWithSecretKey(new Wallet("vault", new Address("tz1dnCbNYHDoxmHss9kEQVjWQj6GvYX4gYp5"), publicKey: "edpkY", keyKind: KeyKind.Encrypted), "edeskLOCKED");
        var model = new WalletViewModel(chain: new MockChainService(), walletStore: store);
        model.Select(model.Wallets.First(w => w.Alias == "clear"));
        Assert.True(model.CanExportSelectedKey);
        Assert.Equal(new WalletViewModel.ExportedKey("edskCLEAR", null), await model.ExportSecretKey(null));

        model.Select(model.Wallets.First(w => w.Alias == "vault"));
        await Assert.ThrowsAsync<SendViewModel.SendError.PassphraseRequired>(() => model.ExportSecretKey(null));
        await Assert.ThrowsAsync<ChainError.WrongPassphrase>(() => model.ExportSecretKey("wrong"));
        Assert.Equal(new WalletViewModel.ExportedKey("edskDECRYPTED", "edeskLOCKED"), await model.ExportSecretKey("correct horse"));

        var watch = new WalletViewModel([new Wallet("w", new Address("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb"), keyKind: KeyKind.None)], new MockChainService());
        Assert.False(watch.CanExportSelectedKey);
    }

    [Fact]
    public async Task ForgetRemovesTheAliasFromEveryFile()
    {
        var dir = TestSupport.TempDir("forget");
        var store = new TezosClientStore(dir);
        store.AddWithSecretKey(new Wallet("clear", new Address("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb"), publicKey: "edpkX", keyKind: KeyKind.Unencrypted), "edskCLEAR");
        store.AddWithSecretKey(new Wallet("vault", new Address("tz1dnCbNYHDoxmHss9kEQVjWQj6GvYX4gYp5"), publicKey: "edpkY", keyKind: KeyKind.Encrypted), "edeskLOCKED");
        store.AddWatchOnly(new Wallet("book", new Address("tz2BFTyPeYRzxd5aiBchbXN3WCZhx7BqbMBq"), keyKind: KeyKind.None));
        var model = new WalletViewModel(chain: new MockChainService(), walletStore: store);

        model.Select(model.Wallets.First(w => w.Alias == "vault"));
        await Assert.ThrowsAsync<SendViewModel.SendError.PassphraseRequired>(() => model.ForgetSelectedWallet(null));
        await Assert.ThrowsAsync<ChainError.WrongPassphrase>(() => model.ForgetSelectedWallet("wrong"));
        Assert.Equal(3, model.Wallets.Count);
        await model.ForgetSelectedWallet("correct horse");
        Assert.Equal(["clear", "book"], model.Wallets.Select(w => w.Alias));
        Assert.Equal("clear", model.SelectedWallet?.Alias);
        foreach (var file in TezosClientStore.WalletFiles)
        {
            var path = Path.Combine(dir, file);
            var text = File.Exists(path) ? File.ReadAllText(path) : "";
            Assert.True(!text.Contains("vault") && !text.Contains("edeskLOCKED"));
        }

        model.Select(model.Wallets.First(w => w.Alias == "book"));
        await model.ForgetSelectedWallet(null);
        await model.ForgetSelectedWallet(null);
        Assert.True(model.Wallets.Count == 0 && model.SelectedWallet is null);
        Assert.Empty(store.Load());
    }
}
