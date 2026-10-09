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
        public sealed class LockTimeout(string path) : StoreError($"The wallet is locked by another program ({path}). Close it and try again.");
    }

    public const string PublicKeyHashesFile = "public_key_hashs";
    public const string PublicKeysFile = "public_keys";
    public const string SecretKeysFile = "secret_keys";
    public const string LockFile = "wallet_lock";

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Signet's own wallet directory: <c>%USERPROFILE%\.signet</c> (upstream <c>~/.signet</c>).</summary>
    public static string DefaultDirectory => Path.Combine(Home, ".signet");

    /// <summary>Where octez-client keeps its wallet: <c>%USERPROFILE%\.tezos-client</c>.</summary>
    public static string OctezClientDirectory => Path.Combine(Home, ".tezos-client");

    public static readonly IReadOnlyList<string> WalletFiles = [PublicKeyHashesFile, PublicKeysFile, SecretKeysFile];

    public string Directory { get; } = directory ?? DefaultDirectory;

    /// <summary>True when the directory already holds a <c>public_key_hashs</c> file.</summary>
    public bool HasWallets => File.Exists(FilePath(PublicKeyHashesFile));

    private string FilePath(string file) => Path.Combine(Directory, file);

    // ---- IWalletStore ----------------------------------------------------------------------------

    public IReadOnlyList<Wallet> Load()
    {
        var hashes = ReadEntries(PublicKeyHashesFile);
        var publicKeys = Index(ReadEntries(PublicKeysFile));
        var secretKeys = Index(ReadEntries(SecretKeysFile));

        var wallets = new List<Wallet>();
        foreach (var (name, value) in hashes)
        {
            if (StringValue(value) is not { } address) continue;
            var locator = StringValue(secretKeys.GetValueOrDefault(name));
            wallets.Add(new Wallet(
                alias: name,
                address: new Address(address),
                publicKey: PublicKey(publicKeys.GetValueOrDefault(name)),
                keyKind: KeyKindExtensions.FromLocator(locator),
                ledgerKey: locator is null ? null : LedgerKey.FromLocator(locator)));
        }
        return wallets;
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
            var hashes = ReadRaw(PublicKeyHashesFile);
            var publicKeys = ReadRaw(PublicKeysFile);
            var secretKeys = ReadRaw(SecretKeysFile);

            if (new[] { hashes, publicKeys, secretKeys }.Any(list => list.Any(e => Name(e) == wallet.Alias)))
                throw new StoreError.AliasExists(wallet.Alias);

            hashes.Add(new JsonObject { ["name"] = wallet.Alias, ["value"] = wallet.Address.Value });
            publicKeys.Add(new JsonObject { ["name"] = wallet.Alias, ["value"] = new JsonObject { ["locator"] = publicKeyLocator, ["key"] = publicKey } });
            secretKeys.Add(new JsonObject { ["name"] = wallet.Alias, ["value"] = locator });

            Write(hashes, PublicKeyHashesFile);
            Write(publicKeys, PublicKeysFile);
            Write(secretKeys, SecretKeysFile);
        });
    }

    public void AddWatchOnly(Wallet wallet)
    {
        System.IO.Directory.CreateDirectory(Directory);
        WithWalletLock(() =>
        {
            var hashes = ReadRaw(PublicKeyHashesFile);
            var taken = new[] { hashes, ReadRaw(PublicKeysFile), ReadRaw(SecretKeysFile) }.Any(list => list.Any(e => Name(e) == wallet.Alias));
            if (taken) throw new StoreError.AliasExists(wallet.Alias);
            hashes.Add(new JsonObject { ["name"] = wallet.Alias, ["value"] = wallet.Address.Value });
            Write(hashes, PublicKeyHashesFile);
        });
    }

    public void Rename(string alias, string newAlias)
    {
        WithWalletLock(() =>
        {
            var files = WalletFiles.Select(f => (File: f, Entries: ReadRaw(f))).ToList();
            if (files.Any(f => f.Entries.Any(e => Name(e) == newAlias))) throw new StoreError.AliasExists(newAlias);
            if (!files.First(f => f.File == PublicKeyHashesFile).Entries.Any(e => Name(e) == alias)) throw new StoreError.UnknownAlias(alias);
            foreach (var (file, entries) in files)
            {
                foreach (var entry in entries.Where(e => Name(e) == alias)) entry["name"] = newAlias;
                Write(entries, file);
            }
        });
    }

    public void Remove(string alias)
    {
        WithWalletLock(() =>
        {
            var files = WalletFiles.Select(f => (File: f, Entries: ReadRaw(f))).ToList();
            if (!files.Any(f => f.Entries.Any(e => Name(e) == alias))) throw new StoreError.UnknownAlias(alias);
            foreach (var (file, entries) in files)
            {
                var kept = entries.Where(e => Name(e) != alias).ToList();
                if (kept.Count != entries.Count) Write(kept, file);
            }
        });
    }

    public int ImportWallets(string directory)
    {
        var source = new TezosClientStore(directory);
        if (!source.HasWallets) return 0;
        System.IO.Directory.CreateDirectory(Directory);
        return WithWalletLock(() =>
        {
            var existing = ReadEntries(PublicKeyHashesFile).Select(e => e.Name).ToHashSet();
            var incoming = source.ReadEntries(PublicKeyHashesFile).Select(e => e.Name).Where(n => !existing.Contains(n)).ToList();
            var accepted = incoming.ToHashSet();
            foreach (var file in WalletFiles)
            {
                var sourcePath = Path.Combine(directory, file);
                if (!File.Exists(sourcePath)) continue;
                var target = FilePath(file);
                if (!File.Exists(target))
                {
                    // Cold start: take an exact copy of the file.
                    File.Copy(sourcePath, target);
                }
                else
                {
                    // Merge: append only aliases we do not already have.
                    var entries = ReadRaw(file);
                    var additions = source.ReadRaw(file).Where(e => Name(e) is { } n && accepted.Contains(n)).ToList();
                    if (additions.Count == 0) continue;
                    entries.AddRange(additions);
                    Write(entries, file);
                }
            }
            return incoming.Count;
        });
    }

    /// <summary>
    /// The base58 secret key for <c>unencrypted:</c> and <c>encrypted:</c> entries (the caller supplies the
    /// password for the latter); <c>null</c> for ledger, remote and watch-only aliases.
    /// </summary>
    public string? SecretKey(Wallet wallet) =>
        WalletStoreExtensions.SecretFromLocator(StringValue(Index(ReadEntries(SecretKeysFile)).GetValueOrDefault(wallet.Alias)));

    // ---- Parsing ---------------------------------------------------------------------------------

    private List<JsonObject> ReadRaw(string file)
    {
        var path = FilePath(file);
        if (!File.Exists(path)) return [];
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return [];
        JsonNode? root;
        try { root = JsonNode.Parse(text); }
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

    private void Write(List<JsonObject> entries, string file)
    {
        var array = new JsonArray(entries.Select(e => (JsonNode)(e.Parent is null ? e : e.DeepClone())).ToArray());
        AtomicFile.WriteAllText(FilePath(file), array.ToJsonString(WriteOptions));
    }

    /// <summary>
    /// Holds the same lock file octez-client takes while it edits the wallet files. Windows has no
    /// <c>lockf</c>; an exclusive open does the same job, and waits while someone else holds it.
    /// </summary>
    private T WithWalletLock<T>(Func<T> body)
    {
        var path = FilePath(LockFile);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            FileStream? handle = null;
            try { handle = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(50); continue; }
            catch (IOException) { throw new StoreError.LockTimeout(path); }
            using (handle) return body();
        }
    }

    private void WithWalletLock(Action body) => WithWalletLock(() => { body(); return 0; });
}
