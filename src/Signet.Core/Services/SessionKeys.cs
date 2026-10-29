using System.Security.Cryptography;
using System.Text;
using Signet.Models;

namespace Signet.Services;

/// <summary>
/// A secret held only for this run of the app: a paper key imported for one session. The bytes
/// live in a pinned array (so the garbage collector never leaves copies behind when it compacts),
/// are wiped when the key is forgotten or the object goes away, and never touch the wallet
/// directory or a backup.
///
/// What this cannot do: the key still has to be handed to the bridge as a string for the one
/// operation it signs, exactly like a key read from disk, and those copies are the runtime's.
/// Windows: upstream also <c>mlock</c>s the bytes and turns core dumps off; neither is done here.
/// </summary>
public sealed class SessionSecret
{
    private readonly byte[] buffer;

    public SessionSecret(string secret)
    {
        var bytes = Encoding.UTF8.GetBytes(secret);
        buffer = GC.AllocateArray<byte>(bytes.Length, pinned: true);
        bytes.CopyTo(buffer, 0);
        CryptographicOperations.ZeroMemory(bytes);
    }

    public string Value => Encoding.UTF8.GetString(buffer);

    /// <summary>Overwrites the secret with zeros; <c>Value</c> is empty of meaning afterwards.</summary>
    public void Wipe() => CryptographicOperations.ZeroMemory(buffer);

    ~SessionSecret() => Wipe();
}

/// <summary>
/// The session-only accounts and their secrets: the in-memory counterpart of <c>IWalletStore</c> for
/// paper keys. Aliases and addresses are unique here as in the directory; the model keeps the
/// two sets apart. Every entry has an identity of its own and a generation that each change
/// bumps, so a removal decided after an await (the password check) can be matched against the
/// exact entry it verified (S07): a renamed, replaced or re-imported account is never the one
/// that gets wiped. Touched on the UI thread only.
/// </summary>
public sealed class SessionKeyStore
{
    public abstract class StoreError(string message) : Exception(message)
    {
        public sealed class AliasExists(string alias) : StoreError($"An account named “{alias}” already exists for this session.");
        public sealed class AddressExists(string alias) : StoreError($"“{alias}” already holds that address for this session.");
        public sealed class AddressMismatch(string alias) : StoreError($"“{alias}” no longer names the account it did.");
        public sealed class WalletChanged(string alias)
            : StoreError($"Account “{alias}” changed while removal was being confirmed. Nothing was removed; please try again.");
    }

    /// <summary>
    /// What a removal was decided on: the entry's identity, its state at that moment and the
    /// secret the password was checked against. <c>Remove(matching:)</c> takes only this.
    /// </summary>
    public sealed record RemovalSnapshot
    {
        internal RemovalSnapshot(Guid id, int generation, Wallet wallet, string secretKey)
        {
            Id = id; Generation = generation; Wallet = wallet; SecretKey = secretKey;
        }
        internal Guid Id { get; }
        internal int Generation { get; }
        public Wallet Wallet { get; }
        public string SecretKey { get; }

        private bool PrintMembers(StringBuilder builder) => false;
    }

    private sealed class Entry(Wallet wallet, SessionSecret secret)
    {
        public Guid Id { get; } = Guid.NewGuid();
        public Wallet Wallet { get; set; } = wallet;
        public int Generation { get; set; }
        public SessionSecret Secret { get; } = secret;
    }

    private readonly List<Entry> entries = [];

    public IReadOnlyList<Wallet> Wallets => entries.Select(e => e.Wallet).ToList();
    public bool IsEmpty => entries.Count == 0;

    /// <summary>
    /// Holds <c>secretKey</c> (clear or octez-encrypted base58) for <c>wallet</c>, marked session-only.
    /// Never replaces an entry: a taken alias or address is refused.
    /// </summary>
    public void Add(Wallet wallet, string secretKey)
    {
        var entry = wallet with { IsSessionOnly = true };
        if (entries.Any(e => e.Wallet.Alias == entry.Alias)) throw new StoreError.AliasExists(entry.Alias);
        if (entries.FirstOrDefault(e => e.Wallet.Address == entry.Address) is { } taken) throw new StoreError.AddressExists(taken.Wallet.Alias);
        entries.Add(new Entry(entry, new SessionSecret(secretKey)));
    }

    private Entry? EntryFor(Wallet wallet)
    {
        if (entries.FirstOrDefault(e => e.Wallet.Alias == wallet.Alias) is not { } stored) return null;
        if (stored.Wallet.Address != wallet.Address) throw new StoreError.AddressMismatch(wallet.Alias);
        return stored;
    }

    /// <summary>
    /// The secret for <c>wallet</c>, as long as its alias still maps to its address (a sheet's
    /// <c>Wallet</c> can outlive a rename), else <c>null</c> when it is not a session key.
    /// </summary>
    public string? SecretKey(Wallet wallet) => EntryFor(wallet)?.Secret.Value;

    /// <summary>
    /// The entry <c>wallet</c> names right now, pinned for a later <c>Remove(matching:)</c>; <c>null</c> when it
    /// is not a session key.
    /// </summary>
    public RemovalSnapshot? RemovalSnapshotFor(Wallet wallet) =>
        EntryFor(wallet) is { } stored ? new RemovalSnapshot(stored.Id, stored.Generation, stored.Wallet, stored.Secret.Value) : null;

    public void Rename(string alias, string newAlias)
    {
        if (entries.FirstOrDefault(e => e.Wallet.Alias == alias) is not { } entry) return;
        if (entries.Any(e => e.Wallet.Alias == newAlias)) throw new StoreError.AliasExists(newAlias);
        entry.Wallet = entry.Wallet with { Alias = newAlias };
        entry.Generation++;
    }

    /// <summary>
    /// Wipes the secret and drops the account, but only the very entry the snapshot was taken
    /// of, unchanged since: same identity, generation, account and secret.
    /// </summary>
    public void Remove(RemovalSnapshot snapshot)
    {
        var index = entries.FindIndex(e => e.Id == snapshot.Id);
        if (index < 0 || entries[index].Generation != snapshot.Generation || entries[index].Wallet != snapshot.Wallet ||
            entries[index].Secret.Value != snapshot.SecretKey)
            throw new StoreError.WalletChanged(snapshot.Wallet.Alias);
        entries[index].Secret.Wipe();
        entries.RemoveAt(index);
    }

    public void RemoveAll()
    {
        foreach (var entry in entries) entry.Secret.Wipe();
        entries.Clear();
    }
}
