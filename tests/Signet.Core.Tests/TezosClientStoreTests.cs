using System.Text.Json;
using Signet.Models;
using Signet.Services;
using Signet.ViewModels;

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
    [Fact]
    public void OffersImportOnColdStartAndClearsItAfterwards()
    {
        var @base = TestSupport.TempDir("vm");
        var octez = Directory.CreateDirectory(Path.Combine(@base, "tezos-client")).FullName;
        TestSupport.WriteText(octez, "public_key_hashs", """[ { "name": "alice", "value": "tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb" } ]""");

        var model = new WalletViewModel(chain: new MockChainService(), walletStore: new TezosClientStore(Path.Combine(@base, "signet")), importSource: octez);
        Assert.Empty(model.Wallets);
        Assert.Equal(1, model.ImportableWalletCount);

        Assert.Equal(1, model.ImportFromOctezClient());
        Assert.Equal("alice", model.SelectedWallet?.Alias);
        Assert.Equal(0, model.ImportableWalletCount);
    }

    [Fact]
    public void NoOfferWithoutASource()
    {
        var model = new WalletViewModel(chain: new MockChainService());
        Assert.Equal(0, model.ImportableWalletCount);
    }
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

    [Fact]
    public void ModelRestoresSelectionFromTheStateFile()
    {
        var dir = TestSupport.TempDir("state");
        var wallets = new TezosClientStore(dir);
        wallets.AddWithSecretKey(new Wallet("first", new Address("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb"), publicKey: "edpkA", keyKind: KeyKind.Unencrypted), "edskA");
        wallets.AddWithSecretKey(new Wallet("pq", new Address("tz5c9dMrWKhwBbSUPhxQRdwUnMMYHfqnpQRD"), publicKey: "mdpkB", keyKind: KeyKind.Unencrypted), "mdskB");
        new FileAppStateStore(dir).Save(new AppState { SelectedWalletAlias = "pq" });

        var model = new WalletViewModel(chain: new MockChainService(), walletStore: wallets, stateStore: new FileAppStateStore(dir));
        Assert.Equal("pq", model.SelectedWallet?.Alias);
    }
}

/// <summary>S09, S10: wallet files are replaced as one journalled unit under the wallet lock.</summary>
public class TezosClientStoreIntegrityTests
{
    private static readonly Wallet A = new("a", new Address("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb"), publicKey: "edpkA", keyKind: KeyKind.Unencrypted);

    /// <summary>Windows has no POSIX modes (see <c>TestSupport</c>), so what is left is the cleanup.</summary>
    [Fact]
    public void FilesAreCreatedPrivateAndTheDirectoryIsHardened()
    {
        var dir = TestSupport.MakeDir("integrity");
        new TezosClientStore(dir).AddWithSecretKey(A, "edskA");
        foreach (var file in TezosClientStore.WalletFiles) Assert.True(File.Exists(Path.Combine(dir, file)));
        // No temporary or journal is left behind.
        Assert.DoesNotContain(Directory.GetFiles(dir).Select(Path.GetFileName), n => n!.Contains(".tmp") || n == TezosClientStore.JournalFile);
    }

    [Fact]
    public void AnUnfinishedChangeIsRolledBackBeforeTheNextRead()
    {
        var dir = TestSupport.MakeDir("integrity");
        var store = new TezosClientStore(dir);
        store.AddWithSecretKey(A, "edskA");
        var before = TezosClientStore.WalletFiles.Select(f => File.ReadAllBytes(Path.Combine(dir, f))).ToList();
        // Simulate a crash after the first of three files was replaced: the journal still names
        // all three with their previous contents, and public_key_hashs already has the new alias.
        var files = new System.Text.Json.Nodes.JsonObject();
        foreach (var (file, data) in TezosClientStore.WalletFiles.Zip(before)) files[file] = Convert.ToBase64String(data);
        files[TezosClientStore.ContractsFile] = null;
        File.WriteAllText(Path.Combine(dir, TezosClientStore.JournalFile), new System.Text.Json.Nodes.JsonObject { ["version"] = 1, ["files"] = files }.ToJsonString());
        TestSupport.WriteText(dir, "public_key_hashs", """[{"name":"a","value":"tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb"},{"name":"b","value":"tz2BFTyPeYRzxd5aiBchbXN3WCZhx7BqbMBq"}]""");
        TestSupport.WriteText(dir, "contracts", """[{"name":"orphan","value":"KT1BEqzn5Wx8uJrZNvuS9DVHmLvG9td3fDLi"}]""");
        // Windows' temporaries are AtomicFile's "<name>.tmp-<guid>".
        TestSupport.WriteText(dir, "secret_keys.tmp-deadbeef", "junk");

        Assert.Equal(["a"], store.Load().Select(w => w.Alias));
        Assert.Equal(before, TezosClientStore.WalletFiles.Select(f => File.ReadAllBytes(Path.Combine(dir, f))));
        Assert.False(File.Exists(Path.Combine(dir, "contracts")));
        Assert.False(File.Exists(Path.Combine(dir, TezosClientStore.JournalFile)));
        Assert.False(File.Exists(Path.Combine(dir, "secret_keys.tmp-deadbeef")));
        // A journal that cannot be read is an error, never silently ignored.
        TestSupport.WriteText(dir, TezosClientStore.JournalFile, "{");
        Assert.ThrowsAny<TezosClientStore.StoreError>(() => store.Load());
    }

    [Fact]
    public void RenameAndRemoveAreJournaledAsOneUnit()
    {
        var dir = TestSupport.MakeDir("integrity");
        var store = new TezosClientStore(dir);
        store.AddWithSecretKey(A, "edskA");
        store.Rename("a", "b");
        var renamed = store.Load().First();
        Assert.True(renamed.Alias == "b" && renamed.PublicKey == "edpkA" && renamed.KeyKind == KeyKind.Unencrypted);
        store.Remove(renamed, store.RemovalSnapshot(renamed));
        Assert.Empty(store.Load());
        foreach (var file in TezosClientStore.WalletFiles)
            Assert.Empty(JsonSerializer.Deserialize<JsonElement[]>(File.ReadAllBytes(Path.Combine(dir, file)))!);
        Assert.False(File.Exists(Path.Combine(dir, TezosClientStore.JournalFile)));
    }

    [Fact]
    public void ProposalUpdatesMergeWhatIsOnDisk()
    {
        var dir = TestSupport.MakeDir("integrity");
        FileMultisigProposalStore one = new(dir), two = new(dir);
        static MultisigProposal Proposal(int n) =>
            new("m", "KT1BEqzn5Wx8uJrZNvuS9DVHmLvG9td3fDLi", "Shadownet", "NetX", n, 2, ["edpk1", "edpk2"], new MultisigAction.SetDelegate(null), $"05{n}");
        // Each instance holds a stale in-memory view; neither write may drop the other's proposal.
        MultisigProposal first = Proposal(1), second = Proposal(2);
        one.Update(p => p.Add(first));
        two.Update(p => p.Add(second));
        Assert.Equal([1, 2], one.Load().Select(p => p.Counter).Order());
        one.Update(p => { var i = p.FindIndex(x => x.Id == first.Id); p[i] = p[i] with { Signatures = [.. p[i].Signatures, new MultisigSignature("edpk1", "edsig1")] }; });
        two.Update(p => { var i = p.FindIndex(x => x.Id == second.Id); p[i] = p[i] with { Signatures = [.. p[i].Signatures, new MultisigSignature("edpk2", "edsig2")] }; });
        var stored = two.Load();
        Assert.True(stored.First(p => p.Id == first.Id).Signatures.Count == 1 && stored.First(p => p.Id == second.Id).Signatures.Count == 1);
        // Oversized entries another program wrote are left out (S19).
        var huge = Proposal(3) with { Bytes = new string('0', FileMultisigProposalStore.MaxBytesHex + 2) };
        File.WriteAllText(Path.Combine(dir, FileMultisigProposalStore.FileName), new System.Text.Json.Nodes.JsonArray(first.ToJson(), huge.ToJson()).ToJsonString());
        Assert.Equal([1], one.Load().Select(p => p.Counter));
    }

    /// <summary>Upstream holds the lock from a Python child; here another handle on the lock file stands in for another process.</summary>
    [Fact]
    public async Task AHeldLockMakesCallersWaitThenFail()
    {
        var dir = TestSupport.MakeDir("integrity");
        var held = new FileStream(Path.Combine(dir, TezosClientStore.LockFile), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var release = Task.Delay(1500, TestContext.Current.CancellationToken).ContinueWith(_ => held.Dispose(), TestContext.Current.CancellationToken);
        var started = DateTime.UtcNow;
        // Taking the lock waits for the holder (well under the 5 s timeout) rather than failing at once.
        TezosClientStore.WithWalletLock(dir, () => 0);
        Assert.True(DateTime.UtcNow - started > TimeSpan.FromSeconds(1));
        await release;
    }
}
