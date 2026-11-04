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

    /// <summary>
    /// S05: only generations Signet made are pruned. Anything else in the backup folder, a
    /// user's documents, a link, a stamp-named folder holding other things, is never touched.
    /// </summary>
    [Fact]
    public void PruningLeavesWhatItDoesNotOwnAlone()
    {
        var wallet = TestSupport.TempDir("wallet");
        var backups = TestSupport.TempDir("backups");
        var elsewhere = TestSupport.TempDir("elsewhere");
        SeedWallet(wallet, "a");
        Directory.CreateDirectory(backups);
        var documents = Path.Combine(backups, "000-unrelated-documents");
        Directory.CreateDirectory(documents);
        File.WriteAllText(Path.Combine(documents, "thesis.txt"), "thesis");
        var lookalike = Path.Combine(backups, "1999-01-01T000000Z");   // stamp name, foreign contents
        Directory.CreateDirectory(lookalike);
        File.WriteAllText(Path.Combine(lookalike, "holiday.jpg"), "photo");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "keep.txt"), "precious");
        // Windows: a junction, which needs no privilege (a symbolic link does).
        var link = Path.Combine(backups, "1999-01-02T000000Z");
        TestSupport.CreateJunction(link, elsewhere);
        var legacy = Path.Combine(backups, "2000-01-01T000000Z");      // no manifest: owner unknown
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "public_key_hashs"), "[]");

        var job = new WalletBackup(wallet, backups, 1);
        Assert.Empty(WalletBackup.GenerationsIn(backups));
        var baseDate = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var first = Assert.IsType<WalletBackup.Outcome.Created>(job.Run(baseDate)).Generation;
        SeedWallet(wallet, "b");
        var second = Assert.IsType<WalletBackup.Outcome.Created>(job.Run(baseDate.AddSeconds(60))).Generation;

        // Our old generations went; everything foreign is still there, link target included.
        Assert.Equal([second], WalletBackup.GenerationsIn(backups));
        Assert.True(Directory.Exists(legacy) && !Directory.Exists(first));
        Assert.Equal("thesis", File.ReadAllText(Path.Combine(documents, "thesis.txt")));
        Assert.Equal("photo", File.ReadAllText(Path.Combine(lookalike, "holiday.jpg")));
        Assert.Equal("precious", File.ReadAllText(Path.Combine(elsewhere, "keep.txt")));
        Assert.True(Directory.Exists(link));
        var manifest = WalletBackup.ManifestOf(second)!;
        Assert.True(PathUtility.SamePath(WalletBackup.Normalized(wallet), manifest.Source));
        Assert.Contains("public_key_hashs", manifest.Files);
    }

    [Fact]
    public void SharedBackupFolderKeepsEachWalletsGenerations()
    {
        var a = TestSupport.TempDir("wallet-a");
        var b = TestSupport.TempDir("wallet-b");
        var backups = TestSupport.TempDir("backups");
        SeedWallet(a, "same");
        SeedWallet(b, "same");
        var baseDate = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var savedA = Assert.IsType<WalletBackup.Outcome.Created>(new WalletBackup(a, backups, 1).Run(baseDate)).Generation;
        // Identical data from another wallet must neither deduplicate against nor prune A.
        var savedB = Assert.IsType<WalletBackup.Outcome.Created>(new WalletBackup(b, backups, 1).Run(baseDate.AddSeconds(60))).Generation;
        Assert.True(Directory.Exists(savedA));
        _ = new WalletBackup(b, backups, 1).Run(baseDate.AddSeconds(120), force: true);
        Assert.True(Directory.Exists(savedA));
        Assert.False(Directory.Exists(savedB));
        Assert.Equal([savedA], WalletBackup.GenerationsIn(backups, a));
        Assert.Single(WalletBackup.GenerationsIn(backups, b));
    }

    [Fact]
    public void UnknownManifestVersionsAndExtraDocumentsArePreserved()
    {
        var wallet = TestSupport.TempDir("wallet");
        var backups = TestSupport.TempDir("backups");
        SeedWallet(wallet, "a");
        var job = new WalletBackup(wallet, backups, 1);
        var baseDate = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var old = Assert.IsType<WalletBackup.Outcome.Created>(job.Run(baseDate)).Generation;
        var manifest = WalletBackup.ManifestOf(old)!;
        File.WriteAllBytes(Path.Combine(old, WalletBackup.ManifestFile), WalletBackup.EncodeManifest(manifest with { Version = manifest.Version + 1 }));
        var documents = Assert.IsType<WalletBackup.Outcome.Created>(job.Run(baseDate.AddSeconds(60), force: true)).Generation;
        File.WriteAllText(Path.Combine(documents, "notes.txt"), "precious");
        _ = job.Run(baseDate.AddSeconds(120), force: true);
        Assert.True(Directory.Exists(old));
        Assert.Equal("precious", File.ReadAllText(Path.Combine(documents, "notes.txt")));
    }

    [Fact]
    public void BackupFolderMustNotOverlapTheWallet()
    {
        var wallet = TestSupport.TempDir("wallet");
        SeedWallet(wallet, "a");
        foreach (var bad in new[] { wallet, Path.Combine(wallet, "backups"), Path.GetDirectoryName(wallet)! })
            Assert.ThrowsAny<WalletBackup.BackupError>(() => new WalletBackup(wallet, bad, 3).Run());
        Assert.Empty(WalletBackup.GenerationsIn(wallet));
    }

    /// <summary>S28: a directory holding only named contracts or proposals is worth a generation too.</summary>
    [Fact]
    public void ContractsOnlyDirectoryIsBackedUp()
    {
        var wallet = TestSupport.TempDir("wallet");
        var backups = TestSupport.TempDir("backups");
        new TezosClientStore(wallet).AddContract(new MultisigContract("treasury", new Address("KT1BEqzn5Wx8uJrZNvuS9DVHmLvG9td3fDLi")));
        var generation = Assert.IsType<WalletBackup.Outcome.Created>(new WalletBackup(wallet, backups, 3).Run()).Generation;
        Assert.Equal(["treasury"], new TezosClientStore(generation).LoadContracts().Select(c => c.Alias));
        // State alone is not.
        var empty = TestSupport.TempDir("wallet");
        new FileAppStateStore(empty).Save(new AppState { SelectedWalletAlias = null });
        Assert.IsType<WalletBackup.Outcome.NothingToBackUp>(new WalletBackup(empty, backups, 3).Run());
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
    [Fact]
    public async Task BacksUpOnLaunchAndAfterCreatingAKey()
    {
        var @base = TestSupport.TempDir("vmbk");
        string wallet = Path.Combine(@base, "wallet"), backups = Path.Combine(@base, "backups");
        new TezosClientStore(wallet).AddWithSecretKey(
            new Wallet("seed", new Address("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb"), publicKey: "edpkS", keyKind: KeyKind.Unencrypted), "edskS");

        var defaults = UserDefaults.Temporary();
        defaults.Set(WalletDirectorySettings.Key, wallet);
        defaults.Set(BackupSettings.DirectoryKey, backups);

        var model = new Signet.ViewModels.WalletViewModel(chain: new MockChainService(),
            directorySettings: new WalletDirectorySettings(defaults),
            storeFactory: dir => (new TezosClientStore(dir), new FileAppStateStore(dir)),
            backupSettings: new BackupSettings(defaults));
        // The launch backup is asynchronous; wait for it.
        for (var i = 0; i < 50 && model.LastBackup is null; i++) await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.IsType<WalletBackup.Outcome.Created>(model.LastBackup?.Outcome);
        Assert.Single(WalletBackup.GenerationsIn(backups));

        await model.CreateWallet("fresh", AddressScheme.Tz1);
        Assert.Equal(2, WalletBackup.GenerationsIn(backups).Count);
        Assert.Equal(["seed", "fresh"], new TezosClientStore(WalletBackup.GenerationsIn(backups)[^1]).Load().Select(w => w.Alias));
    }
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

/// <summary>S09, S10: a backup is taken from a locked snapshot into a uniquely named staging folder.</summary>
public class WalletBackupIntegrityTests
{
    [Fact]
    public void GenerationsArePrivateAndStagingIsCleanedUp()
    {
        var root = TestSupport.TempDir("backup-int");
        var wallet = Path.Combine(root, "wallet");
        var backups = Path.Combine(root, "backups");
        Directory.CreateDirectory(wallet);
        new TezosClientStore(wallet).AddWithSecretKey(
            new Wallet("a", new Address("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb"), publicKey: "edpkA", keyKind: KeyKind.Unencrypted), "edskA");
        var job = new WalletBackup(wallet, backups, 3);
        var generation = Assert.IsType<WalletBackup.Outcome.Created>(job.Run()).Generation;
        // Windows has no POSIX modes (see TestSupport); the manifest is there and no staging folder is left.
        Assert.True(File.Exists(Path.Combine(generation, WalletBackup.ManifestFile)));
        Assert.DoesNotContain(Directory.GetDirectories(backups), d => d.EndsWith(".partial", StringComparison.Ordinal));
        // Unchanged files: no second generation, same fingerprint through the locked snapshot.
        Assert.IsType<WalletBackup.Outcome.Unchanged>(job.Run());
    }
}
