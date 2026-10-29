using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Signet.Models;

namespace Signet.Services;

/// <summary>
/// Reads and writes a wallet directory in octez-client's format. Signet's own directory is
/// <c>%USERPROFILE%\.signet</c>; <c>%USERPROFILE%\.tezos-client</c> is read only when the user imports from it.
///
/// Three JSON files hold arrays of <c>{ "name": alias, "value": ... }</c>:
/// - <c>public_key_hashs</c>: value is the address (<c>tz1...</c>).
/// - <c>public_keys</c>: value is <c>{ "locator": "unencrypted:edpk...", "key": "edpk..." }</c>
///   (older clients wrote a bare <c>"unencrypted:edpk..."</c> string; both are read).
/// - <c>secret_keys</c>: value is a locator such as <c>unencrypted:edsk...</c>, <c>encrypted:edesk...</c>,
///   <c>ledger://...</c> or a remote signer URI.
///
/// Entries are joined by alias. Writes take <c>wallet_lock</c> like octez-client, append only, and
/// replace each file atomically so existing entries are never rewritten by hand.
///
/// Windows: the files are not chmod 600 as on the Mac; files under the user profile inherit an
/// ACL that only the user (and administrators) can read.
/// </summary>
public sealed class TezosClientStore(string? directory = null) : IWalletStore
{
    public abstract class StoreError(string message) : Exception(message)
    {
        public sealed class AliasExists(string alias) : StoreError($"An alias named “{alias}” already exists in the tezos-client directory.");
        public sealed class UnknownAlias(string alias) : StoreError($"No alias named “{alias}” exists in the tezos-client directory.");
        public sealed class MissingPublicKey(string alias) : StoreError($"Account “{alias}” has no public key to write.");
        public sealed class Malformed(string file, string detail) : StoreError($"{file} in the tezos-client directory is malformed: {detail}");
        /// <summary>The alias now names a different address than the <c>Wallet</c> the caller holds.</summary>
        public sealed class AddressMismatch(string alias, Address expected, Address? found)
            : StoreError($"Account “{alias}” is {found?.Shortened() ?? "missing"} in the wallet directory, not {expected.Shortened()} as expected. The directory may have changed; please try again.");
        public sealed class WalletChanged(string alias)
            : StoreError($"Account “{alias}” or its key entries changed while removal was being confirmed. Nothing was removed; please try again.");
        /// <summary>Another program (octez-client, another Signet) held the wallet lock for too long.</summary>
        public sealed class Locked() : StoreError("The wallet directory is locked by another program (octez-client?). Please try again in a moment.");
        /// <summary>Import was asked to copy a directory into itself.</summary>
        public sealed class SameDirectory() : StoreError("The wallet directory cannot be imported into itself.");
    }

    public const string PublicKeyHashesFile = "public_key_hashs";
    public const string PublicKeysFile = "public_keys";
    public const string SecretKeysFile = "secret_keys";
    public const string LockFile = "wallet_lock";
    /// <summary>
    /// Written before a change touches several files, removed after the last one (S09). If it is
    /// still there, the change did not finish and every file it names is put back as it was.
    /// </summary>
    public const string JournalFile = "wallet_journal";
    /// <summary>octez-client's named contracts (<c>[{name, value: "KT1…"}]</c>); multisigs live here.</summary>
    public const string ContractsFile = "contracts";

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Signet's own wallet directory: <c>%USERPROFILE%\.signet</c> (upstream <c>~/.signet</c>).</summary>
    public static string DefaultDirectory => Path.Combine(Home, ".signet");

    /// <summary>Where octez-client keeps its wallet: <c>%USERPROFILE%\.tezos-client</c>.</summary>
    public static string OctezClientDirectory => Path.Combine(Home, ".tezos-client");

    public static readonly IReadOnlyList<string> WalletFiles = [PublicKeyHashesFile, PublicKeysFile, SecretKeysFile];
    /// <summary>Every file an alias can appear in; aliases are unique across all of them, as in octez-client.</summary>
    public static readonly IReadOnlyList<string> AliasFiles = [PublicKeyHashesFile, PublicKeysFile, SecretKeysFile, ContractsFile];

    public string Directory { get; } = directory ?? DefaultDirectory;

    /// <summary>True when the directory already holds a <c>public_key_hashs</c> file.</summary>
    public bool HasWallets => File.Exists(FilePath(PublicKeyHashesFile));

    private string FilePath(string file) => Path.Combine(Directory, file);

    // ---- IWalletStore ----------------------------------------------------------------------------

    public IReadOnlyList<Wallet> Load()
    {
        // A read takes the same lock octez-client writes under, so it never sees a half-done
        // change, and an unfinished change of ours is rolled back first (S09).
        var (hashes, publicKeys, secretKeys, contractRows) = WithWalletLock(() =>
        {
            RecoverIfNeeded();
            RemoveStaleTemporaries();
            return (ReadEntries(PublicKeyHashesFile), Index(ReadEntries(PublicKeysFile)), Index(ReadEntries(SecretKeysFile)), ReadEntries(ContractsFile));
        });

        var accounts = new List<Wallet>();
        foreach (var (name, value) in hashes)
        {
            if (StringValue(value) is not { } address) continue;
            var locator = StringValue(secretKeys.GetValueOrDefault(name));
            accounts.Add(new Wallet(
                alias: name,
                address: new Address(address),
                publicKey: PublicKey(publicKeys.GetValueOrDefault(name)),
                keyKind: KeyKindExtensions.FromLocator(locator),
                ledgerKey: locator is null ? null : LedgerKey.FromLocator(locator)));
        }
        // Named contracts (`contracts`) are part of the address book: watch-only entries whose
        // address is a KT1. octez-client keeps them apart because `public_key_hashs` may only
        // hold tz addresses (it refuses to read the file otherwise), so Signet keeps them apart
        // on disk too and joins them here.
        var taken = accounts.Select(w => w.Alias).ToHashSet();
        var contracts = Contracts(contractRows).Where(c => !taken.Contains(c.Alias)).Select(c => new Wallet(c.Alias, c.Address, keyKind: KeyKind.None));
        return accounts.Concat(contracts).ToList();
    }

    public void Add(Wallet wallet, string locator)
    {
        if (wallet.PublicKey is not { } publicKey) throw new StoreError.MissingPublicKey(wallet.Alias);
        // octez-client points the public key at the same place as the secret: the file for
        // in-memory keys, the device for Ledger keys.
        var publicKeyLocator = locator.StartsWith("ledger://", StringComparison.OrdinalIgnoreCase) ? locator : $"unencrypted:{publicKey}";
        System.IO.Directory.CreateDirectory(Directory);
        WithWalletLock(() =>
        {
            RecoverIfNeeded();
            var hashes = ReadRaw(PublicKeyHashesFile);
            var publicKeys = ReadRaw(PublicKeysFile);
            var secretKeys = ReadRaw(SecretKeysFile);

            if (new[] { hashes, publicKeys, secretKeys, ReadRaw(ContractsFile) }.Any(list => list.Any(e => Name(e) == wallet.Alias)))
                throw new StoreError.AliasExists(wallet.Alias);

            hashes.Add(new JsonObject { ["name"] = wallet.Alias, ["value"] = wallet.Address.Value });
            publicKeys.Add(new JsonObject { ["name"] = wallet.Alias, ["value"] = new JsonObject { ["locator"] = publicKeyLocator, ["key"] = publicKey } });
            secretKeys.Add(new JsonObject { ["name"] = wallet.Alias, ["value"] = locator });

            Commit([(PublicKeyHashesFile, hashes), (PublicKeysFile, publicKeys), (SecretKeysFile, secretKeys)]);
        });
    }

    public IReadOnlyList<MultisigContract> LoadContracts() =>
        Contracts(WithWalletLock(() => { RecoverIfNeeded(); return ReadEntries(ContractsFile); }));

    private static List<MultisigContract> Contracts(List<(string Name, JsonNode Value)> entries) =>
        entries.Select(e => StringValue(e.Value) is { } address ? new MultisigContract(e.Name, new Address(address)) : null)
               .OfType<MultisigContract>().ToList();

    public void AddContract(MultisigContract contract)
    {
        System.IO.Directory.CreateDirectory(Directory);
        WithWalletLock(() =>
        {
            RecoverIfNeeded();
            var contracts = ReadRaw(ContractsFile);
            var taken = new[] { contracts, ReadRaw(PublicKeyHashesFile), ReadRaw(PublicKeysFile), ReadRaw(SecretKeysFile) }
                .Any(list => list.Any(e => Name(e) == contract.Alias));
            if (taken) throw new StoreError.AliasExists(contract.Alias);
            contracts.Add(new JsonObject { ["name"] = contract.Alias, ["value"] = contract.Address.Value });
            Commit([(ContractsFile, contracts)]);
        });
    }

    public void AddWatchOnly(Wallet wallet)
    {
        // A KT1 belongs in `contracts`: octez-client refuses to read `public_key_hashs` if one is in there.
        if (wallet.Address.IsContract)
        {
            AddContract(new MultisigContract(wallet.Alias, wallet.Address));
            return;
        }
        System.IO.Directory.CreateDirectory(Directory);
        WithWalletLock(() =>
        {
            RecoverIfNeeded();
            var hashes = ReadRaw(PublicKeyHashesFile);
            var taken = new[] { hashes, ReadRaw(PublicKeysFile), ReadRaw(SecretKeysFile), ReadRaw(ContractsFile) }
                .Any(list => list.Any(e => Name(e) == wallet.Alias));
            if (taken) throw new StoreError.AliasExists(wallet.Alias);
            hashes.Add(new JsonObject { ["name"] = wallet.Alias, ["value"] = wallet.Address.Value });
            Commit([(PublicKeyHashesFile, hashes)]);
        });
    }

    public void Rename(string alias, string newAlias)
    {
        WithWalletLock(() =>
        {
            RecoverIfNeeded();
            var files = AliasFiles.Select(f => (File: f, Entries: ReadRaw(f))).ToList();
            if (files.Any(f => f.Entries.Any(e => Name(e) == newAlias))) throw new StoreError.AliasExists(newAlias);
            // An alias is an account (in public_key_hashs) or a named contract (in contracts).
            if (!files.Any(f => f.File is PublicKeyHashesFile or ContractsFile && f.Entries.Any(e => Name(e) == alias)))
                throw new StoreError.UnknownAlias(alias);
            foreach (var (_, entries) in files)
                foreach (var entry in entries.Where(e => Name(e) == alias)) entry["name"] = newAlias;
            Commit(files.Where(f => File.Exists(FilePath(f.File))).Select(f => (f.File, f.Entries)).ToList());
        });
    }

    public WalletRemovalSnapshot RemovalSnapshot(Wallet wallet) =>
        WithWalletLock(() =>
        {
            RecoverIfNeeded();
            return Snapshot(wallet, AliasFiles.Select(f => (f, ReadRaw(f))).ToList());
        });

    private WalletRemovalSnapshot Snapshot(Wallet wallet, List<(string File, List<JsonObject> Rows)> files)
    {
        JsonNode? Value(string file) => files.First(f => f.File == file).Rows.FirstOrDefault(e => Name(e) == wallet.Alias)?["value"];
        // Accounts live in public_key_hashs, named contracts in contracts; never both.
        var address = StringValue(Value(PublicKeyHashesFile) ?? Value(ContractsFile)) is { } a ? new Address(a) : (Address?)null;
        if (address != wallet.Address) throw new StoreError.AddressMismatch(wallet.Alias, wallet.Address, address);
        var locator = StringValue(Value(SecretKeysFile));
        if (PublicKey(Value(PublicKeysFile)) != wallet.PublicKey ||
            KeyKindExtensions.FromLocator(locator) != wallet.KeyKind ||
            (locator is null ? null : LedgerKey.FromLocator(locator)) != wallet.LedgerKey)
            throw new StoreError.WalletChanged(wallet.Alias);
        var entries = new JsonObject();
        foreach (var (file, rows) in files.OrderBy(f => f.File, StringComparer.Ordinal))
            entries[file] = new JsonArray(rows.Where(e => Name(e) == wallet.Alias).Select(e => (JsonNode)e.DeepClone()).ToArray());
        var fingerprint = SHA256.HashData(Encoding.UTF8.GetBytes(entries.ToJsonString()));
        return new WalletRemovalSnapshot(wallet, StorageID(), fingerprint, WalletStoreExtensions.SecretFromLocator(locator));
    }

    /// <summary>
    /// Binds a snapshot to the actual directory, including replacement at the same path. Upstream uses
    /// device and inode; Windows has neither in .NET, so the creation time stands in for the inode.
    /// </summary>
    private string StorageID()
    {
        var info = new DirectoryInfo(Path.GetFullPath(Directory));
        if (!info.Exists) throw new StoreError.WalletChanged(Directory);
        return $"{info.FullName.ToLowerInvariant()}:{info.CreationTimeUtc.Ticks}";
    }

    public void Remove(Wallet wallet, WalletRemovalSnapshot expected)
    {
        var alias = wallet.Alias;
        WithWalletLock(() =>
        {
            RecoverIfNeeded();
            var files = AliasFiles.Select(f => (File: f, Rows: ReadRaw(f))).ToList();
            if (!files.Any(f => f.Rows.Any(e => Name(e) == alias))) throw new StoreError.UnknownAlias(alias);
            var current = Snapshot(wallet, files);
            if (expected.Wallet != wallet || expected.StorageID != current.StorageID || !expected.Fingerprint.SequenceEqual(current.Fingerprint))
                throw new StoreError.WalletChanged(alias);
            var updates = new List<(string, List<JsonObject>)>();
            foreach (var (file, rows) in files)
            {
                var kept = rows.Where(e => Name(e) != alias).ToList();
                if (kept.Count != rows.Count) updates.Add((file, kept));
            }
            Commit(updates);
        });
    }

    /// <summary>
    /// Copies the aliases of another octez-format directory into this one. The source is read as
    /// one recovered snapshot under its own locks (S09): an unfinished change there is rolled
    /// back first and a concurrent writer waits, so the three key files always come from the
    /// same moment. That lock is released before this directory's is taken, so two imports in
    /// opposite directions cannot deadlock; a directory cannot be imported into itself.
    /// </summary>
    public int ImportWallets(string directory)
    {
        if (PathUtility.SamePath(Path.GetFullPath(directory), Path.GetFullPath(Directory))) throw new StoreError.SameDirectory();
        var snapshot = SnapshotFiles(directory, WalletFiles);
        var sourceFiles = snapshot.Select(s => (File: s.File, Rows: ParseRaw(s.Data, s.File))).ToList();
        if (sourceFiles.FirstOrDefault(f => f.File == PublicKeyHashesFile).Rows is not { } sourceHashes) return 0;
        System.IO.Directory.CreateDirectory(Directory);
        return WithWalletLock(() =>
        {
            RecoverIfNeeded();
            // Every namespace an alias can occupy here, as `Add` checks them.
            var existing = AliasFiles.SelectMany(f => ReadRaw(f).Select(Name)).OfType<string>().ToHashSet();
            var incoming = sourceHashes.Select(Name).OfType<string>().Where(n => !existing.Contains(n)).ToList();
            var accepted = incoming.ToHashSet();
            if (accepted.Count == 0) return 0;
            var copies = new List<(string, byte[])>();
            var merges = new List<(string, List<JsonObject>)>();
            foreach (var (file, data) in snapshot)
            {
                var rows = sourceFiles.First(f => f.File == file).Rows;
                var additions = rows.Where(e => Name(e) is { } n && accepted.Contains(n)).ToList();
                if (!File.Exists(FilePath(file)) && existing.Count == 0 && additions.Count == rows.Count)
                {
                    // A genuinely empty target may retain the exact octez file representation.
                    copies.Add((file, data));
                }
                else
                {
                    // Merge: append only aliases we do not already have.
                    if (additions.Count == 0) continue;
                    var entries = ReadRaw(file);
                    entries.AddRange(additions);
                    merges.Add((file, entries));
                }
            }
            Commit(merges, copies);
            return incoming.Count;
        });
    }

    /// <summary>
    /// The base58 secret key for <c>unencrypted:</c> and <c>encrypted:</c> entries (the caller supplies the
    /// password for the latter); <c>null</c> for ledger, remote and watch-only aliases.
    /// </summary>
    public string? SecretKey(Wallet wallet)
    {
        var (secretKeys, hashes) = WithWalletLock(() =>
        {
            RecoverIfNeeded();
            return (Index(ReadEntries(SecretKeysFile)), Index(ReadEntries(PublicKeyHashesFile)));
        });
        if (StringValue(secretKeys.GetValueOrDefault(wallet.Alias)) is not { } locator) return null;
        var found = StringValue(hashes.GetValueOrDefault(wallet.Alias)) is { } a ? new Address(a) : (Address?)null;
        if (found != wallet.Address) throw new StoreError.AddressMismatch(wallet.Alias, wallet.Address, found);
        return WalletStoreExtensions.SecretFromLocator(locator);
    }

    // ---- Parsing ---------------------------------------------------------------------------------

    private List<JsonObject> ReadRaw(string file)
    {
        var path = FilePath(file);
        return File.Exists(path) ? ParseRaw(File.ReadAllBytes(path), file) : [];
    }

    private static List<JsonObject> ParseRaw(byte[] data, string file)
    {
        if (data.Length == 0 || Encoding.UTF8.GetString(data).Trim().Length == 0) return [];
        JsonNode? root;
        try { root = JsonNode.Parse(data); }
        catch (JsonException e) { throw new StoreError.Malformed(file, e.Message); }
        if (root is not JsonArray array || array.Any(n => n is not JsonObject))
            throw new StoreError.Malformed(file, "expected a JSON array of objects");
        // Detach from the parsed array so entries can move into the list that gets written.
        return array.Select(n => (JsonObject)n!.DeepClone()).ToList();
    }

    private List<(string Name, JsonNode Value)> ReadEntries(string file) =>
        ReadRaw(file)
            .Where(o => Name(o) is not null && o["value"] is not null)
            .Select(o => (Name(o)!, o["value"]!))
            .ToList();

    private static Dictionary<string, JsonNode> Index(List<(string Name, JsonNode Value)> entries)
    {
        var index = new Dictionary<string, JsonNode>();
        foreach (var (name, value) in entries) index.TryAdd(name, value);
        return index;
    }

    private static string? Name(JsonObject entry) => StringValue(entry["name"]);

    private static string? StringValue(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;

    private static string? PublicKey(JsonNode? value)
    {
        if (value is JsonObject o && StringValue(o["key"]) is { } key) return key;
        if (StringValue(value) is { } locator)
        {
            // Older format: "unencrypted:edpk..." or a bare key.
            var colon = locator.IndexOf(':');
            return colon >= 0 ? locator[(colon + 1)..] : locator;
        }
        return null;
    }

    // ---- Writing ---------------------------------------------------------------------------------

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        // Keep aliases and locators readable: no \u escapes for non-ASCII, '+' or '/'.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static byte[] Encode(List<JsonObject> entries)
    {
        var array = new JsonArray(entries.Select(e => (JsonNode)(e.Parent is null ? e : e.DeepClone())).ToArray());
        return new UTF8Encoding(false).GetBytes(array.ToJsonString(WriteOptions));
    }

    /// <summary>
    /// A change to several files as one unit (S09). Before the first file is replaced, a journal
    /// records what every named file held (or that it did not exist); the journal is removed
    /// after the last replacement. Found at the start of any later read or write, it means the
    /// change was cut short, and the files are put back exactly as they were, so the three key
    /// files (and <c>contracts</c>) always agree with each other. Must be called under the lock.
    /// </summary>
    private void Commit(IReadOnlyList<(string File, List<JsonObject> Rows)> updates, IReadOnlyList<(string File, byte[] Data)>? raw = null)
    {
        var payloads = updates.Select(u => (u.File, Data: Encode(u.Rows))).Concat(raw ?? []).ToList();
        if (payloads.Count == 0) return;
        var names = payloads.Select(p => p.File).ToList();
        if (names.Distinct().Count() != names.Count || !names.All(AliasFiles.Contains))
            throw new StoreError.Malformed(JournalFile, "unexpected files in a transaction");
        if (payloads.Count > 1)
        {
            var before = new JsonObject();
            foreach (var name in names)
                before[name] = File.Exists(FilePath(name)) ? Convert.ToBase64String(File.ReadAllBytes(FilePath(name))) : null;
            AtomicFile.WriteAllText(FilePath(JournalFile), new JsonObject { ["version"] = 1, ["files"] = before }.ToJsonString());
        }
        foreach (var (name, data) in payloads) AtomicFile.WriteAllBytes(FilePath(name), data);
        if (payloads.Count > 1) File.Delete(FilePath(JournalFile));
    }

    /// <summary>Rolls back a change the journal says never finished. Must be called under the lock.</summary>
    private void RecoverIfNeeded()
    {
        var path = FilePath(JournalFile);
        if (!File.Exists(path)) return;
        JsonObject? files = null;
        try
        {
            if (JsonNode.Parse(File.ReadAllBytes(path)) is JsonObject journal && journal["version"]?.GetValue<int>() == 1)
                files = journal["files"] as JsonObject;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { }
        if (files is null)
            throw new StoreError.Malformed(JournalFile, "unreadable; an earlier change did not finish. Restore from a backup or remove the file by hand.");
        foreach (var (name, previous) in files)
        {
            if (!AliasFiles.Contains(name)) continue;
            if (StringValue(previous) is { } encoded) AtomicFile.WriteAllBytes(FilePath(name), Convert.FromBase64String(encoded));
            else if (File.Exists(FilePath(name))) File.Delete(FilePath(name));
        }
        File.Delete(path);
    }

    /// <summary>Removes <c>AtomicFile</c> temporaries a crash left behind. Must be called under the lock.</summary>
    private void RemoveStaleTemporaries()
    {
        if (!System.IO.Directory.Exists(Directory)) return;
        foreach (var name in AliasFiles.Append(JournalFile))
            foreach (var stale in System.IO.Directory.EnumerateFiles(Directory, name + ".tmp-*"))
                try { File.Delete(stale); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Recovery, enumeration and reads share the same transaction boundary as mutations.</summary>
    public static List<(string File, byte[] Data)> SnapshotFiles(string directory, IEnumerable<string> files) =>
        WithWalletLock(directory, () =>
        {
            new TezosClientStore(directory).RecoverIfNeeded();
            return files.Where(f => File.Exists(Path.Combine(directory, f))).Select(f => (f, File.ReadAllBytes(Path.Combine(directory, f)))).ToList();
        });

    private T WithWalletLock<T>(Func<T> body) => WithWalletLock(Directory, body);
    private void WithWalletLock(Action body) => WithWalletLock(Directory, () => { body(); return 0; });

    /// <summary>How long a caller waits for the lock before giving up; octez-client holds it for milliseconds.</summary>
    public static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The wallet lock, tried rather than waited for, so the UI thread is never blocked for good
    /// on a program that keeps it (S09). Readers and backups take it too. A per-directory
    /// in-process lock serialises threads, since the file lock only excludes other processes.
    /// </summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> LocalLocks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Holds the same lock file octez-client takes while it edits the wallet files. Windows has no
    /// <c>lockf</c>; an exclusive open does the same job.
    /// </summary>
    public static T WithWalletLock<T>(string directory, Func<T> body)
    {
        var deadline = DateTime.UtcNow + LockTimeout;
        var local = LocalLocks.GetOrAdd(Path.GetFullPath(directory), _ => new SemaphoreSlim(1, 1));
        if (!local.Wait(LockTimeout)) throw new StoreError.Locked();
        try
        {
            // No directory yet means no files and nobody else to lock out.
            if (!System.IO.Directory.Exists(directory)) return body();
            var path = Path.Combine(directory, LockFile);
            while (true)
            {
                FileStream handle;
                try { handle = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(50); continue; }
                catch (IOException) { throw new StoreError.Locked(); }
                using (handle) return body();
            }
        }
        finally
        {
            local.Release();
        }
    }
}
