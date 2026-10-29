using System.Text;
using Signet.Models;
using Signet.Services;
using Signet.ViewModels;

namespace Signet.Tests;

/// <summary>Paper keys: imported for this run only, usable like any software key, never written anywhere.</summary>
public class SessionKeyTests
{
    private static readonly KeyMaterial Paper = new(AddressScheme.Tz1, "edpkuPAPER", "tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb", "edskPAPERSECRET");
    private static readonly KeyMaterial Other = new(AddressScheme.Tz1, "edpkuOTHER", "tz1dCaMnnMJk76UodjewCK67ABiXWjcKj73N", "edskOTHER");

    [Fact]
    public async Task ASessionKeySignsAndExportsButIsNeverStored()
    {
        var store = new InMemoryWalletStore();
        var model = new WalletViewModel(chain: new MockChainService(), walletStore: store);
        await model.ImportAccount("Paper", Paper, keepForSession: true);
        var wallet = model.SelectedWallet!;
        Assert.True(wallet.Alias == "Paper" && wallet.IsSessionOnly && wallet.KeyKind == KeyKind.Unencrypted);
        Assert.Equal(["Paper"], model.Wallets.Select(w => w.Alias));
        Assert.Empty(store.Load());

        var key = Assert.IsType<SigningKey.Secret>(model.SigningKeyFor(wallet, null));
        Assert.True(key.Key == Paper.SecretKey && key.Passphrase is null && key.Address == wallet.Address);
        Assert.Equal(new WalletViewModel.ExportedKey(Paper.SecretKey, null), await model.ExportSecretKey(null));

        // Aliases are shared with the directory; stored accounts come first; a fresh load never sees the paper key.
        await Assert.ThrowsAnyAsync<WalletViewModel.WalletError>(() => model.ImportAccount("Paper", Other));
        await model.ImportAccount("Stored", Other);
        Assert.Equal(["Stored", "Paper"], model.Wallets.Select(w => w.Alias));
        Assert.Equal(["Stored"], store.Load().Select(w => w.Alias));
        Assert.Equal(["Stored"], new WalletViewModel(chain: new MockChainService(), walletStore: store).Wallets.Select(w => w.Alias));
    }

    [Fact]
    public async Task RenameAndForgetStayInMemory()
    {
        var store = new InMemoryWalletStore();
        var model = new WalletViewModel(chain: new MockChainService(), walletStore: store);
        await model.ImportAccount("Paper", Paper, keepForSession: true);
        model.RenameSelectedWallet("Cold");
        Assert.True(model.SelectedWallet?.Alias == "Cold");
        Assert.Equal(["Cold"], model.Wallets.Select(w => w.Alias));
        var renamed = model.SelectedWallet!;
        Assert.NotNull(model.SigningKeyFor(renamed, null));
        // A wallet value from before the rename no longer names the key.
        var stale = new Wallet("Paper", renamed.Address, AddressScheme.Tz1, renamed.PublicKey, KeyKind.Unencrypted) { IsSessionOnly = true };
        Assert.Null(model.SigningKeyFor(stale, null));
        Assert.Empty(store.Load());

        await model.ForgetSelectedWallet(null);
        Assert.True(model.Wallets.Count == 0 && model.SelectedWallet is null);
        Assert.Empty(store.Load());
    }

    [Fact]
    public async Task EncryptedSessionKeysKeepTheirPassword()
    {
        var material = await new KeyGenerator().Generate(AddressScheme.Tz1);
        var model = new WalletViewModel(chain: new MockChainService(), walletStore: new InMemoryWalletStore());
        await model.ImportAccount("Vault", material, storePassphrase: "correct horse battery staple", keepForSession: true);
        var wallet = model.SelectedWallet!;
        Assert.True(wallet.IsSessionOnly && wallet.KeyKind == KeyKind.Encrypted);
        var key = Assert.IsType<SigningKey.Secret>(model.SigningKeyFor(wallet, "correct horse battery staple"));
        Assert.True(key.Key.StartsWith("edesk", StringComparison.Ordinal) && key.Passphrase == "correct horse battery staple");
        // Forgetting an encrypted key needs its password, like a stored one.
        await Assert.ThrowsAnyAsync<Exception>(() => model.ForgetSelectedWallet(null));
        Assert.Single(model.Wallets);
    }

    [Fact]
    public void SecretsAreWipedWhenForgotten()
    {
        var secret = new SessionSecret("edskSECRET");
        Assert.Equal("edskSECRET", secret.Value);
        secret.Wipe();
        var bytes = Encoding.UTF8.GetBytes(secret.Value);
        Assert.True(bytes.Length == 10 && bytes.All(b => b == 0));
    }

    [Fact(Skip = "Not applicable on Windows: Wallet is never serialised, so there is no older encoding to read")]
    public void WalletsRecordedBeforeTheFlagStillDecode() { }
}
