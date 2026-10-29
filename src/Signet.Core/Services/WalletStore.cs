using System.Security.Cryptography;
using System.Text.Json;
using Signet.Models;

namespace Signet.Services;

/// <summary>
/// Captures the exact account entries whose removal is being approved. Kept only for the flow;
/// the secret (encrypted when applicable) is needed to verify that same entry's password.
/// </summary>
public sealed record WalletRemovalSnapshot(Wallet Wallet, string StorageID, byte[] Fingerprint, string? SecretKey)
{
    public bool Equals(WalletRemovalSnapshot? other) =>
        other is not null && Wallet == other.Wallet && StorageID == other.StorageID && Fingerprint.SequenceEqual(other.Fingerprint) && SecretKey == other.SecretKey;
    public override int GetHashCode() => HashCode.Combine(Wallet, StorageID);

    // Never print secrets: records would otherwise include the key in ToString().
    private bool PrintMembers(System.Text.StringBuilder builder) => false;
}

/// <summary>Where wallets and their secret keys live.</summary>
public interface IWalletStore
{
    IReadOnlyList<Wallet> Load();
    /// <summary>
    /// Adds a wallet whose <c>secret_keys</c> entry is <c>locator</c> (<c>unencrypted:…</c>, <c>encrypted:…</c>,
    /// <c>ledger://…</c>). Fails if the alias is already taken.
    /// </summary>
    void Add(Wallet wallet, string locator);
    /// <summary>Adds an address we hold no key for (address book). Fails if the alias is already taken.</summary>
    void AddWatchOnly(Wallet wallet);
    /// <summary>
    /// The base58 secret key for clear-text and encrypted entries (encrypted ones need the password), else <c>null</c>.
    /// The alias on disk must still map to <c>wallet.Address</c>: a <c>Wallet</c> captured by a sheet may
    /// outlive a directory switch or an external edit, and the alias alone must never pick a key.
    /// </summary>
    string? SecretKey(Wallet wallet);
    /// <summary>Changes an alias everywhere it appears. Fails if the new alias is already taken.</summary>
    void Rename(string alias, string newAlias);
    /// <summary>
    /// Captures the account entries and storage identity before password verification.
    /// Fails if the stored account no longer matches the wallet being approved.
    /// </summary>
    WalletRemovalSnapshot RemovalSnapshot(Wallet wallet);
    /// <summary>
    /// Compares the snapshot and removes the alias from every file, secret key included,
    /// while holding the same storage lock (octez-client's <c>forget address … --force</c>).
    /// </summary>
    void Remove(Wallet wallet, WalletRemovalSnapshot snapshot);
    /// <summary>
    /// Brings in wallets from an octez-client style directory, skipping aliases already present.
    /// Returns how many were added.
    /// </summary>
    int ImportWallets(string directory);
    /// <summary>Named contracts (octez-client's <c>contracts</c> file): the multisigs the user has created or added.</summary>
    IReadOnlyList<MultisigContract> LoadContracts();
    /// <summary>Records a contract alias. Fails if the alias is already taken.</summary>
    void AddContract(MultisigContract contract);
}

public static class WalletStoreExtensions
{
    public static void Remove(this IWalletStore store, Wallet wallet) => store.Remove(wallet, store.RemovalSnapshot(wallet));

    /// <summary>Adds a wallet and its base58 secret key (clear or octez-encrypted).</summary>
    public static void AddWithSecretKey(this IWalletStore store, Wallet wallet, string secretKey) =>
        store.Add(wallet, KeyKindExtensions.Locator(secretKey));

    /// <summary>The key part of an <c>unencrypted:</c> or <c>encrypted:</c> locator, else <c>null</c>.</summary>
    internal static string? SecretFromLocator(string? locator)
    {
        if (locator is null) return null;
        foreach (var prefix in new[] { "unencrypted:", "encrypted:" })
            if (locator.StartsWith(prefix, StringComparison.Ordinal)) return locator[prefix.Length..];
        return null;
    }
}

/// <summary>For previews and tests. Nothing is persisted.</summary>
public sealed class InMemoryWalletStore(IEnumerable<Wallet>? wallets = null) : IWalletStore
{
    public abstract class StoreError(string message) : Exception(message)
    {
        public sealed class AliasExists(string alias) : StoreError($"An alias named “{alias}” already exists.");
        public sealed class UnknownAlias(string alias) : StoreError($"No alias named “{alias}” exists.");
        public sealed class AddressMismatch(string alias) : StoreError($"“{alias}” no longer names the account it did.");
        public sealed class WalletChanged(string alias) : StoreError($"Account “{alias}” changed while removal was being confirmed. Nothing was removed; please try again.");
    }

    private readonly Lock gate = new();
    private readonly string storageID = Guid.NewGuid().ToString();
    private readonly List<Wallet> wallets = wallets?.ToList() ?? [];
    private readonly Dictionary<string, string> secrets = [];
    private readonly List<MultisigContract> contracts = [];

    /// <summary>Accounts first, then named contracts as watch-only entries, like <c>TezosClientStore.Load()</c>.</summary>
    private List<Wallet> AllWallets() =>
        wallets.Concat(contracts.Select(c => new Wallet(c.Alias, c.Address, keyKind: KeyKind.None))).ToList();

    private bool AliasTaken(string alias) => wallets.Any(w => w.Alias == alias) || contracts.Any(c => c.Alias == alias);

    public IReadOnlyList<Wallet> Load()
    {
        lock (gate) return AllWallets();
    }

    public void Add(Wallet wallet, string locator)
    {
        lock (gate)
        {
            if (AliasTaken(wallet.Alias)) throw new StoreError.AliasExists(wallet.Alias);
            wallets.Add(wallet);
            secrets[wallet.Alias] = locator;
        }
    }

    public void AddWatchOnly(Wallet wallet)
    {
        if (wallet.Address.IsContract)
        {
            AddContract(new MultisigContract(wallet.Alias, wallet.Address));
            return;
        }
        lock (gate)
        {
            if (AliasTaken(wallet.Alias)) throw new StoreError.AliasExists(wallet.Alias);
            wallets.Add(wallet with { KeyKind = KeyKind.None, PublicKey = null });
        }
    }

    public string? SecretKey(Wallet wallet)
    {
        lock (gate)
        {
            if (!secrets.TryGetValue(wallet.Alias, out var locator)) return null;
            if (wallets.FirstOrDefault(w => w.Alias == wallet.Alias)?.Address != wallet.Address) throw new StoreError.AddressMismatch(wallet.Alias);
            return WalletStoreExtensions.SecretFromLocator(locator);
        }
    }

    public int ImportWallets(string directory)
    {
        var incoming = new TezosClientStore(directory).Load();
        lock (gate)
        {
            var existing = wallets.Select(w => w.Alias).ToHashSet();
            var additions = incoming.Where(w => !existing.Contains(w.Alias)).ToList();
            wallets.AddRange(additions);
            return additions.Count;
        }
    }

    public WalletRemovalSnapshot RemovalSnapshot(Wallet wallet)
    {
        lock (gate) return Snapshot(wallet);
    }

    private WalletRemovalSnapshot Snapshot(Wallet wallet)
    {
        var stored = AllWallets().FirstOrDefault(w => w.Alias == wallet.Alias) ?? throw new StoreError.UnknownAlias(wallet.Alias);
        if (stored.Address != wallet.Address) throw new StoreError.AddressMismatch(wallet.Alias);
        if (stored != wallet) throw new StoreError.WalletChanged(wallet.Alias);
        var locator = secrets.GetValueOrDefault(wallet.Alias);
        var data = JsonSerializer.SerializeToUtf8Bytes(new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["locator"] = locator,
            ["wallet"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["alias"] = stored.Alias, ["address"] = stored.Address.Value, ["publicKey"] = stored.PublicKey,
                ["keyKind"] = stored.KeyKind.ToString(), ["ledgerKey"] = stored.LedgerKey?.Locator,
            },
        });
        return new WalletRemovalSnapshot(wallet, storageID, SHA256.HashData(data), WalletStoreExtensions.SecretFromLocator(locator));
    }

    public void Remove(Wallet wallet, WalletRemovalSnapshot expected)
    {
        lock (gate)
        {
            var current = Snapshot(wallet);
            if (expected.Wallet != wallet || expected.StorageID != current.StorageID || !expected.Fingerprint.SequenceEqual(current.Fingerprint))
                throw new StoreError.WalletChanged(wallet.Alias);
            wallets.RemoveAll(w => w.Alias == wallet.Alias);
            contracts.RemoveAll(c => c.Alias == wallet.Alias);
            secrets.Remove(wallet.Alias);
        }
    }

    public IReadOnlyList<MultisigContract> LoadContracts()
    {
        lock (gate) return contracts.ToList();
    }

    public void AddContract(MultisigContract contract)
    {
        lock (gate)
        {
            if (AliasTaken(contract.Alias)) throw new StoreError.AliasExists(contract.Alias);
            contracts.Add(contract);
        }
    }

    public void Rename(string alias, string newAlias)
    {
        lock (gate)
        {
            if (AliasTaken(newAlias)) throw new StoreError.AliasExists(newAlias);
            var index = wallets.FindIndex(w => w.Alias == alias);
            if (index >= 0)
            {
                wallets[index] = wallets[index] with { Alias = newAlias };
                if (secrets.Remove(alias, out var secret)) secrets[newAlias] = secret;
                return;
            }
            var contract = contracts.FindIndex(c => c.Alias == alias);
            if (contract < 0) throw new StoreError.UnknownAlias(alias);
            contracts[contract] = contracts[contract] with { Alias = newAlias };
        }
    }
}
