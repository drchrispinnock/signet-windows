using Signet.Models;

namespace Signet.Services;

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
    /// <summary>The base58 secret key for clear-text and encrypted entries (encrypted ones need the password), else <c>null</c>.</summary>
    string? SecretKey(Wallet wallet);
    /// <summary>Changes an alias everywhere it appears. Fails if the new alias is already taken.</summary>
    void Rename(string alias, string newAlias);
    /// <summary>Removes an alias from every file, secret key included (octez-client's <c>forget address … --force</c>).</summary>
    void Remove(string alias);
    /// <summary>
    /// Brings in wallets from an octez-client style directory, skipping aliases already present.
    /// Returns how many were added.
    /// </summary>
    int ImportWallets(string directory);
}

public static class WalletStoreExtensions
{
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
    }

    private readonly Lock gate = new();
    private readonly List<Wallet> wallets = wallets?.ToList() ?? [];
    private readonly Dictionary<string, string> secrets = [];

    public IReadOnlyList<Wallet> Load()
    {
        lock (gate) return wallets.ToList();
    }

    public void Add(Wallet wallet, string locator)
    {
        lock (gate)
        {
            if (wallets.Any(w => w.Alias == wallet.Alias)) throw new StoreError.AliasExists(wallet.Alias);
            wallets.Add(wallet);
            secrets[wallet.Alias] = locator;
        }
    }

    public void AddWatchOnly(Wallet wallet)
    {
        lock (gate)
        {
            if (wallets.Any(w => w.Alias == wallet.Alias)) throw new StoreError.AliasExists(wallet.Alias);
            wallets.Add(wallet with { KeyKind = KeyKind.None, PublicKey = null });
        }
    }

    public string? SecretKey(Wallet wallet)
    {
        lock (gate) return WalletStoreExtensions.SecretFromLocator(secrets.GetValueOrDefault(wallet.Alias));
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

    public void Remove(string alias)
    {
        lock (gate)
        {
            if (wallets.RemoveAll(w => w.Alias == alias) == 0) throw new StoreError.UnknownAlias(alias);
            secrets.Remove(alias);
        }
    }

    public void Rename(string alias, string newAlias)
    {
        lock (gate)
        {
            if (wallets.Any(w => w.Alias == newAlias)) throw new StoreError.AliasExists(newAlias);
            var index = wallets.FindIndex(w => w.Alias == alias);
            if (index < 0) throw new StoreError.UnknownAlias(alias);
            wallets[index] = wallets[index] with { Alias = newAlias };
            if (secrets.Remove(alias, out var secret)) secrets[newAlias] = secret;
        }
    }
}
