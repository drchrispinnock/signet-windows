using Signet.Models;
using Signet.Services;

namespace Signet.Tests;

public class WalletBackupTests
{
    private static void SeedWallet(string dir, string alias) =>
        new TezosClientStore(dir).AddWithSecretKey(
            new Wallet(alias, new Address("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb"), publicKey: $"edpk{alias}", keyKind: KeyKind.Unencrypted),
            $"edsk{alias}");

    [Fact]
    public void CreatesAGenerationWithTheWalletFiles()
    {
        var wallet = TestSupport.TempDir("wallet");
        var backups = TestSupport.TempDir("backups");
        SeedWallet(wallet, "a");
        new FileAppStateStore(wallet).Save(new AppState { SelectedWalletAlias = "a" });

        var outcome = new WalletBackup(wallet, backups, 10).Run();
        var generation = Assert.IsType<WalletBackup.Outcome.Created>(outcome).Generation;

        foreach (var file in new[] { "public_key_hashs", "public_keys", "secret_keys", "state" })
        {
            var copy = Path.Combine(generation, file);
            Assert.True(File.Exists(copy), file);
            Assert.True(File.ReadAllBytes(Path.Combine(wallet, file)).SequenceEqual(File.ReadAllBytes(copy)), file);
        }
        Assert.Equal(["a"], new TezosClientStore(generation).Load().Select(w => w.Alias));
    }

    [Fact]
    public void SkipsWhenNothingChangedAndPrunesToTheLimit()
    {
        var wallet = TestSupport.TempDir("wallet");
        var backups = TestSupport.TempDir("backups");
        SeedWallet(wallet, "a");
        var job = new WalletBackup(wallet, backups, 3);
        var baseDate = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

        var first = Assert.IsType<WalletBackup.Outcome.Created>(job.Run(baseDate)).Generation;
        Assert.Equal(new WalletBackup.Outcome.Unchanged(first), job.Run(baseDate.AddSeconds(60)));
        Assert.Single(WalletBackup.GenerationsIn(backups));

        // Each change produces a generation; only the newest three survive.
        var aliases = new[] { "b", "c", "d", "e" };
        for (var i = 0; i < aliases.Length; i++)
        {
            SeedWallet(wallet, aliases[i]);
            Assert.IsType<WalletBackup.Outcome.Created>(job.Run(baseDate.AddSeconds(120 * (i + 1))));
        }
        var remaining = WalletBackup.GenerationsIn(backups);
        Assert.Equal(3, remaining.Count);
        Assert.DoesNotContain(first, remaining);
        // The newest generation holds everything.
        Assert.Equal(["a", "b", "c", "d", "e"], new TezosClientStore(remaining[^1]).Load().Select(w => w.Alias));
    }

    [Fact]
    public void NothingToBackUpWithoutWalletFiles()
    {
        var wallet = TestSupport.TempDir("wallet");
        var backups = TestSupport.TempDir("backups");
        Assert.IsType<WalletBackup.Outcome.NothingToBackUp>(new WalletBackup(wallet, backups, 10).Run());
        Assert.Empty(WalletBackup.GenerationsIn(backups));
    }

    [Fact]
    public void SettingsDefaultAndClamp()
    {
        var defaults = UserDefaults.Temporary();
        var settings = new BackupSettings(defaults);
        Assert.Equal(10, settings.Generations);
        Assert.Equal(".signet_backups", Path.GetFileName(settings.Directory));

        settings.SetGenerations(500);
        Assert.Equal(100, settings.Generations);
        settings.SetGenerations(0);
        Assert.Equal(1, settings.Generations);
        settings.SetGenerations(10);
        Assert.Null(defaults.Object(BackupSettings.GenerationsKey));

        var bk = Path.Combine(Path.GetTempPath(), "bk");
        settings.SetDirectory(bk);
        Assert.True(PathUtility.SamePath(bk, settings.Directory));
        settings.SetDirectory(settings.DefaultDirectory);
        Assert.Null(defaults.String(BackupSettings.DirectoryKey));
    }
}

public class BackupViewModelTests
{
    [Fact(Skip = Pending.ViewModel)] public void BacksUpOnLaunchAndAfterCreatingAKey() { }
}

public class ForcedBackupTests
{
    [Fact]
    public void ForceWritesAGenerationEvenWhenUnchanged()
    {
        var root = TestSupport.TempDir("force");
        var wallet = Path.Combine(root, "wallet");
        var backups = Path.Combine(root, "backups");
        new TezosClientStore(wallet).AddWithSecretKey(
            new Wallet("a", new Address("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb"), publicKey: "edpkA", keyKind: KeyKind.Unencrypted), "edskA");
        var job = new WalletBackup(wallet, backups, 10);
        var t0 = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

        Assert.IsType<WalletBackup.Outcome.Created>(job.Run(t0));
        Assert.IsType<WalletBackup.Outcome.Unchanged>(job.Run(t0.AddSeconds(60)));
        Assert.IsType<WalletBackup.Outcome.Created>(job.Run(t0.AddSeconds(120), force: true));
        Assert.Equal(2, WalletBackup.GenerationsIn(backups).Count);
    }
}
