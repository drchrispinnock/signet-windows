using System.Text.Json;
using Signet.Models;
using Signet.Services;

namespace Signet.Tests;

public class TezosClientStoreTests
{
    private static void WriteFixture(string directory)
    {
        const string hashes = """
        [ { "name": "alice", "value": "tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb" },
          { "name": "bob",   "value": "tz2BFTyPeYRzxd5aiBchbXN3WCZhx7BqbMBq" },
          { "name": "pq",    "value": "tz5c9dMrWKhwBbSUPhxQRdwUnMMYHfqnpQRD" },
          { "name": "watch", "value": "tz3WXYtyDUNL91qfiCJtVUX746QpNv5i5ve5" } ]
        """;
        // Mixed public_keys formats: current object form, legacy string form, and one missing.
        const string publicKeys = """
        [ { "name": "bob",   "value": "unencrypted:sppkBOB" },
          { "name": "alice", "value": { "locator": "unencrypted:edpkALICE", "key": "edpkALICE" } },
          { "name": "pq",    "value": { "locator": "unencrypted:mdpkPQ", "key": "mdpkPQ" } } ]
        """;
        const string secretKeys = """
        [ { "name": "alice", "value": "unencrypted:edskALICE" },
          { "name": "bob",   "value": "encrypted:edeskBOB" },
          { "name": "pq",    "value": "ledger://thing/ed25519/0h/0h" } ]
        """;
        TestSupport.WriteText(directory, "public_key_hashs", hashes);
        TestSupport.WriteText(directory, "public_keys", publicKeys);
        TestSupport.WriteText(directory, "secret_keys", secretKeys);
    }

    [Fact]
    public void LoadsAliasesJoinedByName()
    {
        var dir = TestSupport.MakeDir("tests");
        WriteFixture(dir);
        var store = new TezosClientStore(dir);
        Assert.True(store.HasWallets);

        var wallets = store.Load();
        Assert.Equal(["alice", "bob", "pq", "watch"], wallets.Select(w => w.Alias));

        var alice = wallets[0];
        Assert.Equal(AddressScheme.Tz1, alice.Scheme);
        Assert.Equal("edpkALICE", alice.PublicKey);
        Assert.Equal(KeyKind.Unencrypted, alice.KeyKind);

        var bob = wallets[1];
        Assert.Equal(AddressScheme.Tz2, bob.Scheme);
        Assert.Equal("sppkBOB", bob.PublicKey);   // legacy string form
        Assert.Equal(KeyKind.Encrypted, bob.KeyKind);

        var pq = wallets[2];
        Assert.Equal(AddressScheme.Tz5, pq.Scheme);
        Assert.Equal(KeyKind.Ledger, pq.KeyKind);

        var watch = wallets[3];
        Assert.Null(watch.PublicKey);
        Assert.Equal(KeyKind.None, watch.KeyKind);
    }

    [Fact]
    public void SecretKeyForClearAndEncryptedEntriesOnly()
    {
        var dir = TestSupport.MakeDir("tests");
        WriteFixture(dir);
        var store = new TezosClientStore(dir);
        var wallets = store.Load();
        Assert.Equal("edskALICE", store.SecretKey(wallets[0]));
        // Encrypted keys come back too (the caller supplies the password); ledger and watch-only do not.
        Assert.Equal("edeskBOB", store.SecretKey(wallets[1]));
        Assert.Null(store.SecretKey(wallets[2]));
        Assert.Null(store.SecretKey(wallets[3]));
    }

    [Fact]
    public void AddAppendsInOctezFormatAndKeepsExistingEntries()
    {
        var dir = TestSupport.MakeDir("tests");
        WriteFixture(dir);
        var store = new TezosClientStore(dir);

        var wallet = new Wallet("new one", new Address("tz3WXYtyDUNL91qfiCJtVUX746QpNv5i5ve5"),
                                AddressScheme.Tz3, publicKey: "p2pkNEW", keyKind: KeyKind.Unencrypted);
        store.AddWithSecretKey(wallet, "p2skNEW");

        var reloaded = store.Load();
        Assert.Equal(["alice", "bob", "pq", "watch", "new one"], reloaded.Select(w => w.Alias));
        Assert.Equal("p2pkNEW", reloaded[^1].PublicKey);
        Assert.Equal(KeyKind.Unencrypted, reloaded[^1].KeyKind);
        Assert.Equal("p2skNEW", store.SecretKey(wallet));
        // Existing entries survive untouched.
        Assert.Equal("edskALICE", store.SecretKey(reloaded[0]));

        // The public_keys entry uses the current {locator, key} object form.
        using var entries = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "public_keys")));
        var value = entries.RootElement[entries.RootElement.GetArrayLength() - 1].GetProperty("value");
        Assert.Equal("unencrypted:p2pkNEW", value.GetProperty("locator").GetString());
        Assert.Equal("p2pkNEW", value.GetProperty("key").GetString());

        // Not upstream: octez-client must be able to read what we write, so no BOM.
        Assert.NotEqual(0xEF, File.ReadAllBytes(Path.Combine(dir, "secret_keys"))[0]);
    }

    [Fact]
    public void AddRefusesDuplicateAlias()
    {
        var dir = TestSupport.MakeDir("tests");
        WriteFixture(dir);
        var store = new TezosClientStore(dir);
        var dup = new Wallet("alice", new Address("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb"), publicKey: "edpkX");
        Assert.ThrowsAny<TezosClientStore.StoreError>(() => store.AddWithSecretKey(dup, "edskX"));
    }

    [Fact]
    public void CreatesDirectoryAndFilesWhenMissing()
    {
        var dir = Path.Combine(TestSupport.MakeDir("tests"), "fresh");
        var store = new TezosClientStore(dir);
        Assert.False(store.HasWallets);
        Assert.Empty(store.Load());

        var wallet = new Wallet("My Wallet", new Address("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb"), publicKey: "edpkX", keyKind: KeyKind.Unencrypted);
        store.AddWithSecretKey(wallet, "edskX");
        Assert.True(store.HasWallets);
        Assert.Equal([wallet], store.Load());
    }

    // Not upstream: a second writer waits for wallet_lock instead of interleaving.
    [Fact]
    public async Task WritersQueueOnTheWalletLock()
    {
        var dir = TestSupport.MakeDir("lock");
        var store = new TezosClientStore(dir);
        var writers = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
            store.AddWatchOnly(new Wallet($"w{i}", new Address("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb")))));
        await Task.WhenAll(writers);
        Assert.Equal(8, store.Load().Count);
    }
}

public class TezosClientStoreRenameTests
{
    private static TezosClientStore MakeStore()
    {
        var dir = TestSupport.MakeDir("rename");
        TestSupport.WriteText(dir, "public_key_hashs", """
        [ { "name": "alice", "value": "tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb" },
          { "name": "watch", "value": "tz3WXYtyDUNL91qfiCJtVUX746QpNv5i5ve5" } ]
        """);
        TestSupport.WriteText(dir, "public_keys", """
        [ { "name": "alice", "value": { "locator": "unencrypted:edpkALICE", "key": "edpkALICE" } } ]
        """);
        TestSupport.WriteText(dir, "secret_keys", """
        [ { "name": "alice", "value": "unencrypted:edskALICE" } ]
        """);
        return new TezosClientStore(dir);
    }

    [Fact]
    public void RenamesAliasInAllThreeFiles()
    {
        var store = MakeStore();
        store.Rename("alice", "alice-main");

        var wallets = store.Load();
        Assert.Equal(["alice-main", "watch"], wallets.Select(w => w.Alias));
        var renamed = wallets[0];
        Assert.Equal("edpkALICE", renamed.PublicKey);
        Assert.Equal(KeyKind.Unencrypted, renamed.KeyKind);
        Assert.Equal("edskALICE", store.SecretKey(renamed));
        Assert.Null(store.SecretKey(new Wallet("alice", renamed.Address)));
    }

    [Fact]
    public void RenamesWatchOnlyAliasPresentInOneFile()
    {
        var store = MakeStore();
        store.Rename("watch", "observer");
        Assert.Equal(["alice", "observer"], store.Load().Select(w => w.Alias));
    }

    [Fact]
    public void RefusesTakenOrUnknownAliases()
    {
        var store = MakeStore();
        Assert.ThrowsAny<TezosClientStore.StoreError>(() => store.Rename("alice", "watch"));
        Assert.ThrowsAny<TezosClientStore.StoreError>(() => store.Rename("nobody", "x"));
        Assert.Equal(["alice", "watch"], store.Load().Select(w => w.Alias));
    }
}

public class TezosClientStoreImportTests
{
    private static void WriteOctez(string dir)
    {
        TestSupport.WriteText(dir, "public_key_hashs", """
        [ { "name": "alice", "value": "tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb" },
          { "name": "bob",   "value": "tz2BFTyPeYRzxd5aiBchbXN3WCZhx7BqbMBq" } ]
        """);
        TestSupport.WriteText(dir, "public_keys", """
        [ { "name": "alice", "value": { "locator": "unencrypted:edpkALICE", "key": "edpkALICE" } } ]
        """);
        TestSupport.WriteText(dir, "secret_keys", """
        [ { "name": "alice", "value": "unencrypted:edskALICE" } ]
        """);
    }

    [Fact]
    public void ColdStartImportCopiesFilesExactly()
    {
        var octez = TestSupport.MakeDir("octez");
        WriteOctez(octez);
        var signet = Path.Combine(TestSupport.MakeDir("signet"), "fresh");
        var store = new TezosClientStore(signet);

        var added = store.ImportWallets(octez);

        Assert.Equal(2, added);
        Assert.Equal(["alice", "bob"], store.Load().Select(w => w.Alias));
        foreach (var file in TezosClientStore.WalletFiles)
        {
            var a = File.ReadAllBytes(Path.Combine(octez, file));
            var b = File.ReadAllBytes(Path.Combine(signet, file));
            Assert.True(a.SequenceEqual(b), $"{file} should be a byte-for-byte copy");
        }
        // The source is untouched.
        Assert.Equal(2, new TezosClientStore(octez).Load().Count);
    }

    [Fact]
    public void LaterImportMergesOnlyNewAliases()
    {
        var octez = TestSupport.MakeDir("octez");
        WriteOctez(octez);
        var signet = TestSupport.MakeDir("signet");
        var store = new TezosClientStore(signet);
        var mine = new Wallet("alice", new Address("tz3WXYtyDUNL91qfiCJtVUX746QpNv5i5ve5"), publicKey: "p2pkMINE", keyKind: KeyKind.Unencrypted);
        store.AddWithSecretKey(mine, "p2skMINE");

        var added = store.ImportWallets(octez);

        Assert.Equal(1, added);
        var wallets = store.Load();
        Assert.Equal(["alice", "bob"], wallets.Select(w => w.Alias));
        // My own "alice" wins; octez's "alice" was skipped.
        Assert.Equal("p2pkMINE", wallets[0].PublicKey);
        Assert.Equal("p2skMINE", store.SecretKey(wallets[0]));
        Assert.Equal(AddressScheme.Tz2, wallets[1].Scheme);
    }

    [Fact]
    public void ImportWithNoSourceIsANoOp()
    {
        var signet = TestSupport.MakeDir("signet");
        var store = new TezosClientStore(signet);
        Assert.Equal(0, store.ImportWallets(Path.Combine(signet, "missing")));
        Assert.False(store.HasWallets);
    }
}

public class ImportViewModelTests
{
    [Fact(Skip = Pending.ViewModel)] public void OffersImportOnColdStartAndClearsItAfterwards() { }
    [Fact(Skip = Pending.ViewModel)] public void NoOfferWithoutASource() { }
}

public class FileAppStateStoreTests
{
    [Fact]
    public void RoundTripsThroughTheStateFile()
    {
        var dir = TestSupport.TempDir("state");
        var store = new FileAppStateStore(dir);
        Assert.Equal(new AppState(), store.Load());

        store.Save(new AppState { SelectedWalletAlias = "pq", NetworkName = "Shadownet" });
        Assert.Equal(new AppState { SelectedWalletAlias = "pq", NetworkName = "Shadownet" }, new FileAppStateStore(dir).Load());
    }

    // Not upstream: the file has the same shape as the Mac's Codable output, so a backup or a shared
    // wallet directory reads the same on both.
    [Fact]
    public void WritesTheSameKeysAsTheMac()
    {
        var dir = TestSupport.TempDir("state");
        new FileAppStateStore(dir).Save(new AppState
        {
            SelectedWalletAlias = "pq", NodeURLs = new Dictionary<string, string> { ["Mainnet"] = "https://rpc.example.org" },
        });
        var text = File.ReadAllText(Path.Combine(dir, "state"));
        Assert.Contains("\"nodeURLs\"", text);
        Assert.Contains("\"selectedWalletAlias\": \"pq\"", text);
        Assert.DoesNotContain("networkName", text);   // absent values are omitted, as Codable does
        Assert.True(text.IndexOf("nodeURLs", StringComparison.Ordinal) < text.IndexOf("selectedWalletAlias", StringComparison.Ordinal));

        TestSupport.WriteText(dir, "state", """{ "networkName" : "Shadownet", "selectedWalletAlias" : "x" }""");
        Assert.Equal(new AppState { NetworkName = "Shadownet", SelectedWalletAlias = "x" }, new FileAppStateStore(dir).Load());
    }

    [Fact(Skip = Pending.ViewModel)]
    public void ModelRestoresSelectionFromTheStateFile() { }
}
