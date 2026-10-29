using Signet.Models;
using Signet.Services;
using Signet.ViewModels;

namespace Signet.Tests;

public class WalletDirectoryTests
{
    private static void Seed(string dir, string alias, string address, string? remembered = null)
    {
        var store = new TezosClientStore(dir);
        store.AddWithSecretKey(new Wallet(alias, new Address(address), publicKey: $"edpk{alias}", keyKind: KeyKind.Unencrypted), $"edsk{alias}");
        store.AddWithSecretKey(new Wallet($"{alias}-2", new Address(address), publicKey: $"edpk{alias}2", keyKind: KeyKind.Unencrypted), $"edsk{alias}2");
        if (remembered is not null) new FileAppStateStore(dir).Save(new AppState { SelectedWalletAlias = remembered });
    }

    [Fact]
    public void SwitchingDirectoriesSwapsWalletsAndRestoresThatDirectorysSelection()
    {
        string a = TestSupport.TempDir("a"), b = TestSupport.TempDir("b");
        Seed(a, "alpha", "tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb");
        Seed(b, "beta", "tz2BFTyPeYRzxd5aiBchbXN3WCZhx7BqbMBq", remembered: "beta-2");

        var defaults = UserDefaults.Temporary();
        defaults.Set(WalletDirectorySettings.Key, a);

        static (IWalletStore, IAppStateStore) Factory(string dir) => (new TezosClientStore(dir), new FileAppStateStore(dir));
        var model = new WalletViewModel(chain: new MockChainService(), directorySettings: new WalletDirectorySettings(defaults), storeFactory: Factory);
        Assert.True(PathUtility.SamePath(a, model.WalletDirectory!));
        Assert.Equal(["alpha", "alpha-2"], model.Wallets.Select(w => w.Alias));
        Assert.False(model.IsUsingDefaultWalletDirectory);

        model.ChangeWalletDirectory(b);

        Assert.Equal(["beta", "beta-2"], model.Wallets.Select(w => w.Alias));
        Assert.Equal("beta-2", model.SelectedWallet?.Alias);
        Assert.True(PathUtility.SamePath(b, defaults.String(WalletDirectorySettings.Key)!));

        // A relaunch over the same preferences lands in b.
        var again = new WalletViewModel(chain: new MockChainService(), directorySettings: new WalletDirectorySettings(defaults), storeFactory: Factory);
        Assert.Equal(["beta", "beta-2"], again.Wallets.Select(w => w.Alias));
    }

    [Fact]
    public void DefaultDirectoryClearsTheOverride()
    {
        var defaults = UserDefaults.Temporary();
        var settings = new WalletDirectorySettings(defaults);
        Assert.Equal(settings.DefaultDirectory, settings.Current);

        var elsewhere = Path.Combine(Path.GetTempPath(), "elsewhere");
        settings.Set(elsewhere);
        Assert.True(PathUtility.SamePath(elsewhere, settings.Current));
        settings.Set(settings.DefaultDirectory);
        Assert.Null(defaults.String(WalletDirectorySettings.Key));
        Assert.Equal(settings.DefaultDirectory, settings.Current);
    }
}
