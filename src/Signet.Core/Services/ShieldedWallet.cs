using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Signet.Models;

namespace Signet.Services;

/// <summary>
/// A TzEL address of ours: the fields of its payment address (the ML-KEM public keys included,
/// so the record alone can receive) and the next unused WOTS+ key index. The auth tree's leaves
/// live in a separate 2 MB file (<c>IShieldedWalletStore.LoadLeaves</c>) and can be rebuilt from the
/// master key; the signing seed <c>ask_j</c> is never stored, it is derived when a shield is signed.
/// </summary>
public sealed class ShieldedAddress
{
    public int Index { get; set; }
    [JsonPropertyName("dJ")] public string DJ { get; set; } = "";
    public string AuthRoot { get; set; } = "";
    public string PubSeed { get; set; } = "";
    public string NkTag { get; set; } = "";
    public string EkV { get; set; } = "";
    public string EkD { get; set; } = "";
    /// <summary>
    /// The next WOTS+ key index to sign with. Persisted <i>before</i> a signature is made: a key
    /// index used twice would leak the signing key.
    /// </summary>
    public int NextKeyIndex { get; set; }

    [JsonIgnore] public int Id => Index;

    /// <summary>The record as the bridge's scan and witness builder take it.</summary>
    [JsonIgnore]
    public JsonObject BridgeRecord => new()
    {
        ["index"] = Index, ["dJ"] = DJ, ["authRoot"] = AuthRoot, ["pubSeed"] = PubSeed, ["nkTag"] = NkTag, ["ekV"] = EkV, ["ekD"] = EkD,
    };

    /// <summary>
    /// The address as a payment address in the reference JSON form, what another wallet
    /// (Signet or the tzel CLI) takes as a recipient.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, string> PaymentAddressJSON => new SortedDictionary<string, string>(StringComparer.Ordinal)
    {
        ["d_j"] = DJ, ["auth_root"] = AuthRoot, ["auth_pub_seed"] = PubSeed, ["nk_tag"] = NkTag, ["ek_v"] = EkV, ["ek_d"] = EkD,
    };
}

/// <summary>
/// A deposit pool on the rollup funded from layer 1: <c>deposit:&lt;pubkeyHash&gt;</c> is what the
/// ticketer was asked to credit, and only the wallet that knows <c>blind</c> can shield it.
/// </summary>
public sealed class DepositPool
{
    public string PubkeyHash { get; set; } = "";
    public string Blind { get; set; } = "";
    public int AddressIndex { get; set; }
    /// <summary>The rollup's <c>auth_domain</c> the pool id was computed under.</summary>
    public string AuthDomain { get; set; } = "";
    /// <summary>What was sent to the ticketer for this pool, in mutez (the rollup's balance is the truth).</summary>
    public string DepositedMutez { get; set; } = "0";
    /// <summary>The layer 1 operation that funded it, once injected.</summary>
    public string? OperationHash { get; set; }
    /// <summary>
    /// The commitment of the note the latest shield from this pool created, until it is seen
    /// in the tree; the pool stays listed while its balance is nonzero.
    /// </summary>
    public string? ShieldedCm { get; set; }
    public DateTimeOffset Created { get; set; }

    [JsonIgnore] public string Id => PubkeyHash;
}

/// <summary>A note of ours found in the commitment tree.</summary>
public sealed class ShieldedNote
{
    /// <summary>Position in the commitment tree.</summary>
    public int Index { get; set; }
    public string Cm { get; set; } = "";
    public string Rseed { get; set; } = "";
    public string Nullifier { get; set; } = "";
    public string ValueMutez { get; set; } = "0";
    public int AddressIndex { get; set; }
    public bool Spent { get; set; }
    /// <summary>The memo it came with, when there was one (a shielded transfer's).</summary>
    public string? Memo { get; set; }

    [JsonIgnore] public int Id => Index;
    [JsonIgnore] public ulong Value => ulong.TryParse(ValueMutez, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : 0;
}

/// <summary>
/// An unshield handed to the operator: <c>AmountMutez</c> leave for <c>Recipient</c> on layer 1 through
/// the rollup's outbox. Its input notes stay reserved until the rollup spends them (<c>Applied</c>);
/// then its outbox message is looked for on the rollup node, and once the commitment covering
/// it is cemented (about 14 days) the account can execute it on Tezos (<c>ExecutedHash</c>).
/// </summary>
public sealed class ShieldedWithdrawal
{
    public string SubmissionID { get; set; } = "";
    public string Recipient { get; set; } = "";
    public string AmountMutez { get; set; } = "0";
    public string RollupFeeMutez { get; set; } = "0";
    public string ProducerFeeMutez { get; set; } = "0";
    /// <summary>The nullifiers of the notes it spends.</summary>
    public List<string> Nullifiers { get; set; } = [];
    public string? ChangeCm { get; set; }
    /// <summary>The rollup level when it was submitted: its outbox message cannot be earlier.</summary>
    public int SubmittedLevel { get; set; }
    public DateTimeOffset Created { get; set; }
    /// <summary>The operator's layer 1 operation carrying it, when reported.</summary>
    public string? OperationHash { get; set; }
    /// <summary>The rollup has spent every input: the withdrawal is in its outbox.</summary>
    public bool Applied { get; set; }
    public int? OutboxLevel { get; set; }
    public int? MessageIndex { get; set; }
    /// <summary>The layer 1 operation that executed the outbox message.</summary>
    public string? ExecutedHash { get; set; }

    [JsonIgnore] public string Id => SubmissionID;
    [JsonIgnore] public ulong Amount => ShieldedWallet.Mutez(AmountMutez);
    [JsonIgnore] public ulong RollupFee => ShieldedWallet.Mutez(RollupFeeMutez);
    [JsonIgnore] public ulong ProducerFee => ShieldedWallet.Mutez(ProducerFeeMutez);
}

/// <summary>
/// A shielded transfer handed to the operator: <c>AmountMutez</c> to the payment address with
/// <c>RecipientFingerprint</c>. Its input notes stay reserved until the rollup spends them.
/// </summary>
public sealed class ShieldedTransfer
{
    public string SubmissionID { get; set; } = "";
    public string RecipientFingerprint { get; set; } = "";
    /// <summary>The address-book name it was sent to, if any.</summary>
    public string? RecipientName { get; set; }
    public string AmountMutez { get; set; } = "0";
    public string RollupFeeMutez { get; set; } = "0";
    public string ProducerFeeMutez { get; set; } = "0";
    public string Memo { get; set; } = "";
    public List<string> Nullifiers { get; set; } = [];
    public string RecipientCm { get; set; } = "";
    public string ChangeCm { get; set; } = "";
    public DateTimeOffset Created { get; set; }
    public string? OperationHash { get; set; }
    /// <summary>The rollup has spent every input: the recipient has the note.</summary>
    public bool Applied { get; set; }

    [JsonIgnore] public string Id => SubmissionID;
    [JsonIgnore] public ulong Amount => ShieldedWallet.Mutez(AmountMutez);
}

/// <summary>
/// A shielded address of someone else: the payment address as the reference JSON, under a
/// name, with its fingerprint (the hash of the canonical record) for checking against theirs.
/// </summary>
public sealed class ShieldedContact
{
    public string Name { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public Dictionary<string, string> Address { get; set; } = [];
    public DateTimeOffset Created { get; set; }

    [JsonIgnore] public string Id => Fingerprint;
}

/// <summary>
/// The shielded wallet of one account on one network: the master key and everything derived
/// or learned from the rollup. Each tz account has its own, so the dashboard's Shielded row is
/// that account's; they all live in one file per chain in the wallet directory
/// (<c>tzel-&lt;chain&gt;.json</c>, a private file, <c>ShieldedWalletFile</c>). Upstream's value type
/// becomes a class here: <c>Copy()</c> gives the value semantics, and two wallets are equal when
/// their files would be.
/// </summary>
public sealed class ShieldedWallet : IEquatable<ShieldedWallet>
{
    public string Chain { get; set; } = "";
    /// <summary>
    /// The random 251-bit master secret, hex. Everything else derives from it. Kept in the clear
    /// like an unencrypted tz key; a password option is to follow.
    /// </summary>
    public string MasterSk { get; set; } = "";
    public List<ShieldedAddress> Addresses { get; set; } = [];
    /// <summary>Feeds the per-pool blind; every new pool advances it.</summary>
    public ulong DepositNonce { get; set; }
    public List<DepositPool> Pools { get; set; } = [];
    public List<ShieldedNote> Notes { get; set; } = [];
    /// <summary>How many tree positions have been scanned.</summary>
    public int Scanned { get; set; }
    /// <summary>How many rollup nullifiers have been checked against our notes.</summary>
    public int NullifiersSeen { get; set; }
    /// <summary>
    /// Every commitment in the rollup's tree, in order, as far as the scan has read it: an
    /// unshield proves its inputs against the tree, so it needs the whole thing.
    /// </summary>
    public List<string> LeafCms { get; set; } = [];
    /// <summary>Unshields handed to the operator, until their layer 1 side is done.</summary>
    public List<ShieldedWithdrawal> Withdrawals { get; set; } = [];
    /// <summary>Shielded transfers handed to the operator.</summary>
    public List<ShieldedTransfer> Transfers { get; set; } = [];

    public ShieldedWallet() { }

    public ShieldedWallet(string chain, string masterSk)
    {
        Chain = chain;
        MasterSk = masterSk;
    }

    /// <summary>A wallet with a fresh random master key.</summary>
    public static ShieldedWallet Make(string chain)
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        bytes[31] &= 0x07;
        return new ShieldedWallet(chain, HexBytes.Encode(bytes));
    }

    internal static ulong Mutez(string text) => ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : 0;

    /// <summary>Unspent notes, in mutez.</summary>
    [JsonIgnore] public ulong BalanceMutez => Notes.Where(n => !n.Spent).Aggregate(0UL, (sum, n) => unchecked(sum + n.Value));
    [JsonIgnore] public decimal Balance => BalanceMutez / Models.Mutez.PerTez;
    /// <summary>The nullifiers of notes an unshield or transfer in flight will spend.</summary>
    [JsonIgnore]
    public IReadOnlySet<string> ReservedNullifiers =>
        Withdrawals.Where(w => !w.Applied).SelectMany(w => w.Nullifiers).Concat(Transfers.Where(t => !t.Applied).SelectMany(t => t.Nullifiers)).ToHashSet();
    /// <summary>Unspent notes no unshield in flight has claimed.</summary>
    [JsonIgnore]
    public IReadOnlyList<ShieldedNote> AvailableNotes
    {
        get
        {
            var reserved = ReservedNullifiers;
            return Notes.Where(n => !n.Spent && !reserved.Contains(n.Nullifier)).ToList();
        }
    }
    [JsonIgnore] public ulong AvailableMutez => AvailableNotes.Aggregate(0UL, (sum, n) => unchecked(sum + n.Value));
    [JsonIgnore] public ulong ReservedMutez => unchecked(BalanceMutez - AvailableMutez);

    public ShieldedAddress? AddressAt(int index) => Addresses.FirstOrDefault(a => a.Index == index);
    public DepositPool? Pool(string pubkeyHash) => Pools.FirstOrDefault(p => p.PubkeyHash == pubkeyHash);

    /// <summary>
    /// What a scan found (<c>scanned</c>, worked out from <c>base</c>) put onto this wallet, the one on
    /// file now, which may have moved on while the scan ran: a key index spent, an address, a
    /// pool or a record added, a withdrawal executed. The scan owns the notes, the cursors, the
    /// commitments and the records' progress; everything else comes from here, and nothing is
    /// ever rolled back: a key index only grows (an index used twice would leak the signing
    /// key), a spent note stays spent, and what the scan retired or pruned goes.
    /// </summary>
    public ShieldedWallet Merging(ShieldedWallet scanned, ShieldedWallet @base)
    {
        var merged = Copy();
        scanned = scanned.Copy();
        foreach (var address in scanned.Addresses.Where(a => merged.AddressAt(a.Index) is null)) merged.Addresses.Add(address);
        foreach (var address in merged.Addresses)
            if (scanned.AddressAt(address.Index) is { } theirs) address.NextKeyIndex = Math.Max(address.NextKeyIndex, theirs.NextKeyIndex);
        merged.Addresses = merged.Addresses.OrderBy(a => a.Index).ToList();
        merged.DepositNonce = Math.Max(merged.DepositNonce, scanned.DepositNonce);
        var notes = scanned.Notes.ToList();
        foreach (var note in merged.Notes.Where(n => !notes.Any(x => x.Index == n.Index))) notes.Add(note);
        foreach (var note in notes.Where(n => merged.Notes.Any(m => m.Index == n.Index && m.Spent))) note.Spent = true;
        merged.Notes = notes.OrderBy(n => n.Index).ToList();
        merged.Scanned = Math.Max(merged.Scanned, scanned.Scanned);
        merged.NullifiersSeen = Math.Max(merged.NullifiersSeen, scanned.NullifiersSeen);
        if (scanned.LeafCms.Count >= merged.LeafCms.Count) merged.LeafCms = scanned.LeafCms;
        var retired = @base.Pools.Select(p => p.Id).Except(scanned.Pools.Select(p => p.Id)).ToHashSet();
        merged.Pools.RemoveAll(p => retired.Contains(p.Id));
        var prunedWithdrawals = @base.Withdrawals.Select(w => w.Id).Except(scanned.Withdrawals.Select(w => w.Id)).ToHashSet();
        merged.Withdrawals.RemoveAll(w => prunedWithdrawals.Contains(w.Id));
        foreach (var withdrawal in merged.Withdrawals)
        {
            if (scanned.Withdrawals.FirstOrDefault(w => w.Id == withdrawal.Id) is not { } theirs) continue;
            if (theirs.Applied) withdrawal.Applied = true;
            if (withdrawal.OutboxLevel is null && theirs.OutboxLevel is { } level)
            {
                withdrawal.OutboxLevel = level;
                withdrawal.MessageIndex = theirs.MessageIndex;
            }
        }
        var prunedTransfers = @base.Transfers.Select(t => t.Id).Except(scanned.Transfers.Select(t => t.Id)).ToHashSet();
        merged.Transfers.RemoveAll(t => prunedTransfers.Contains(t.Id));
        foreach (var transfer in merged.Transfers.Where(t => scanned.Transfers.Any(s => s.Id == t.Id && s.Applied))) transfer.Applied = true;
        return merged;
    }

    /// <summary>A deep copy, so a change never reaches a wallet it was not made on.</summary>
    public ShieldedWallet Copy() => ShieldedJSON.Clone(this);

    public bool Equals(ShieldedWallet? other) => other is not null && ShieldedJSON.Text(this) == ShieldedJSON.Text(other);
    public override bool Equals(object? obj) => Equals(obj as ShieldedWallet);
    public override int GetHashCode() => HashCode.Combine(Chain, MasterSk, Notes.Count, Scanned);
}

/// <summary><c>tzel-&lt;chain&gt;.json</c>: the network's operator token and one <c>ShieldedWallet</c> per tz account.</summary>
public sealed class ShieldedWalletFile
{
    public const int CurrentVersion = 2;
    public int Version { get; set; } = CurrentVersion;
    public string Chain { get; set; } = "";
    /// <summary>The operator's bearer token for submitting shields (never in the repository, never logged).</summary>
    public string? OperatorToken { get; set; }
    /// <summary>By the account's tz address.</summary>
    public Dictionary<string, ShieldedWallet> Accounts { get; set; } = [];
    /// <summary>
    /// A version 1 file held one wallet for the whole network; it is kept here until an
    /// account adopts it (<c>IShieldedWalletStore.AdoptLegacyWallet</c>).
    /// </summary>
    public ShieldedWallet? Legacy { get; set; }
    /// <summary>Other people's shielded addresses, shared by every account on the network.</summary>
    public List<ShieldedContact> Contacts { get; set; } = [];
}

/// <summary>
/// How the shielded files are written: camelCase keys sorted, ISO 8601 dates, nulls left out,
/// indented with LF, as the Mac's <c>JSONEncoder</c> (<c>.prettyPrinted, .sortedKeys</c>) writes them,
/// so a wallet folder moves between the two.
/// </summary>
public static class ShieldedJSON
{
    private sealed class IsoDate : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Number
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)((reader.GetDouble() + 978_307_200) * 1000))   // Swift's default: seconds since 2001
                : DateTimeOffset.Parse(reader.GetString() ?? "", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
    }

    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
        Converters = { new IsoDate() },
    };

    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;

    /// <summary>The value as canonical text: keys sorted, compact.</summary>
    public static string Text<T>(T value) => Sorted(JsonSerializer.SerializeToNode(value, Options))?.ToJsonString() ?? "null";

    public static byte[] Encode<T>(T value) =>
        Encoding.UTF8.GetBytes((Sorted(JsonSerializer.SerializeToNode(value, Options))?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null").Replace("\r\n", "\n"));

    /// <summary>The node with every object's keys in ordinal order.</summary>
    public static JsonNode? Sorted(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, Sorted(p.Value?.DeepClone())))),
        JsonArray a => new JsonArray(a.Select(x => Sorted(x?.DeepClone())).ToArray()),
        _ => node?.DeepClone(),
    };
}

/// <summary>Where a network's shielded wallets are kept.</summary>
public interface IShieldedWalletStore
{
    ShieldedWallet? Load(Address account);
    void Save(ShieldedWallet wallet, Address account);
    /// <summary>
    /// Changes <c>account</c>'s wallet as it is on file <i>now</i>, in one step no other writer can get
    /// between: every change made after an <c>await</c> goes through here, since a scan or another
    /// operation may have written in the meantime. Starts from <c>initial</c> when there is no
    /// wallet yet and one is given, else does nothing and returns null. Returns the wallet as
    /// written (not written when <c>change</c> leaves it as it was).
    /// </summary>
    ShieldedWallet? Update(Address account, ShieldedWallet? initial, Action<ShieldedWallet> change);
    string? OperatorToken();
    void SetOperatorToken(string? token);
    /// <summary>The version 1 file's single wallet, not yet an account's.</summary>
    ShieldedWallet? LegacyWallet();
    /// <summary>Makes the legacy wallet <c>account</c>'s (nothing happens when there is none or the account has one).</summary>
    void AdoptLegacyWallet(Address account);
    /// <summary>The 65536 × 32 bytes of an address's auth tree leaves, if built.</summary>
    byte[]? LoadLeaves(Address account, int addressIndex);
    void SaveLeaves(byte[] leaves, Address account, int addressIndex);
    /// <summary>The network's shielded address book.</summary>
    IReadOnlyList<ShieldedContact> Contacts();
    void SaveContacts(IReadOnlyList<ShieldedContact> contacts);
}

/// <summary>
/// <c>tzel-&lt;chain&gt;.json</c> and <c>tzel-&lt;chain&gt;-&lt;account&gt;-auth&lt;j&gt;.bin</c> in the wallet directory, written
/// like the key files (S10: private, atomic replace). The JSON is backed up with the wallet;
/// the leaves are not (2 MB each, rebuilt from the master key when missing).
/// </summary>
public sealed class FileShieldedWalletStore(string directory, string chain) : IShieldedWalletStore
{
    public string Directory { get; } = directory;
    public string Chain { get; } = chain;

    /// <summary>
    /// One lock per wallet file in this process: the scan writes from its own task while the
    /// UI thread spends key indexes, and each must see the other's write.
    /// </summary>
    private static readonly Dictionary<string, Lock> FileLocks = new(StringComparer.OrdinalIgnoreCase);

    private T Locked<T>(Func<T> body)
    {
        Lock gate;
        lock (FileLocks)
        {
            var key = Path.GetFullPath(WalletPath);
            if (!FileLocks.TryGetValue(key, out gate!)) FileLocks[key] = gate = new Lock();
        }
        lock (gate) return body();
    }

    private void Locked(Action body) => Locked(() => { body(); return 0; });

    public static string WalletFile(string chain) => $"tzel-{chain}.json";
    public static string LeavesFile(string chain, Address account, int addressIndex) => $"tzel-{chain}-{account.Value}-auth{addressIndex}.bin";
    /// <summary>Where a version 1 file kept its one address's leaves.</summary>
    public static string LegacyLeavesFile(string chain, int addressIndex) => $"tzel-{chain}-auth{addressIndex}.bin";

    private string WalletPath => Path.Combine(Directory, WalletFile(Chain));

    private ShieldedWalletFile? Read()
    {
        if (PrivateFile.Read(WalletPath) is not { } data) return null;
        var header = JsonNode.Parse(data) as JsonObject;
        var version = header?["version"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;
        switch (version)
        {
            case 1:
                // A version 1 file: one wallet for the network, with the token inside it.
                var legacy = JsonSerializer.Deserialize<ShieldedWallet>(data, ShieldedJSON.Options)!;
                return new ShieldedWalletFile
                {
                    Chain = legacy.Chain,
                    OperatorToken = header!["operatorToken"]?.GetValue<string>(),
                    Legacy = legacy,
                };
            case ShieldedWalletFile.CurrentVersion:
                return JsonSerializer.Deserialize<ShieldedWalletFile>(data, ShieldedJSON.Options);
            default:
                throw new ShieldedError.UnsupportedWalletVersion(version);
        }
    }

    private void Write(ShieldedWalletFile file) => PrivateFile.Write(WalletPath, ShieldedJSON.Encode(file));

    public ShieldedWallet? Load(Address account) => Locked(() => Read()?.Accounts.GetValueOrDefault(account.Value));

    public void Save(ShieldedWallet wallet, Address account) => Locked(() =>
    {
        var file = Read() ?? new ShieldedWalletFile { Chain = Chain };
        file.Accounts[account.Value] = wallet.Copy();
        Write(file);
    });

    public ShieldedWallet? Update(Address account, ShieldedWallet? initial, Action<ShieldedWallet> change) => Locked(() =>
    {
        var file = Read() ?? new ShieldedWalletFile { Chain = Chain };
        var stored = file.Accounts.GetValueOrDefault(account.Value);
        if ((stored ?? initial) is not { } before) return null;
        var wallet = before.Copy();
        change(wallet);
        if (!wallet.Equals(before) || stored is null)
        {
            file.Accounts[account.Value] = wallet;
            Write(file);
        }
        return wallet.Copy();
    });

    public string? OperatorToken() => Locked(() => Read()?.OperatorToken);

    public void SetOperatorToken(string? token) => Locked(() =>
    {
        var file = Read() ?? new ShieldedWalletFile { Chain = Chain };
        file.OperatorToken = token;
        Write(file);
    });

    public ShieldedWallet? LegacyWallet() => Locked(() => Read()?.Legacy);

    public void AdoptLegacyWallet(Address account)
    {
        var legacy = Locked(() =>
        {
            if (Read() is not { Legacy: { } wallet } file || file.Accounts.ContainsKey(account.Value)) return null;
            file.Accounts[account.Value] = wallet;
            file.Legacy = null;
            Write(file);
            return wallet;
        });
        if (legacy is null) return;
        foreach (var address in legacy.Addresses)
        {
            var old = Path.Combine(Directory, LegacyLeavesFile(Chain, address.Index));
            var fresh = Path.Combine(Directory, LeavesFile(Chain, account, address.Index));
            try { if (File.Exists(old) && !File.Exists(fresh)) File.Move(old, fresh); }
            catch (IOException) { }
        }
    }

    public byte[]? LoadLeaves(Address account, int addressIndex) =>
        PrivateFile.Read(Path.Combine(Directory, LeavesFile(Chain, account, addressIndex))) is { Length: XMSSAuthTree.LeavesBytes } data ? data : null;

    public void SaveLeaves(byte[] leaves, Address account, int addressIndex)
    {
        if (leaves.Length != XMSSAuthTree.LeavesBytes) throw new ArgumentException("an auth tree has 65536 leaves");
        PrivateFile.Write(Path.Combine(Directory, LeavesFile(Chain, account, addressIndex)), leaves);
    }

    public IReadOnlyList<ShieldedContact> Contacts() => Locked(() => (IReadOnlyList<ShieldedContact>?)Read()?.Contacts ?? []);

    public void SaveContacts(IReadOnlyList<ShieldedContact> contacts) => Locked(() =>
    {
        var file = Read() ?? new ShieldedWalletFile { Chain = Chain };
        file.Contacts = contacts.ToList();
        Write(file);
    });
}

/// <summary>For tests and previews: nothing touches the disk.</summary>
public sealed class InMemoryShieldedWalletStore : IShieldedWalletStore
{
    private readonly Lock gate = new();
    private readonly ShieldedWalletFile file;
    private readonly Dictionary<string, byte[]> leaves = [];
    /// <summary>Every wallet ever saved, oldest first, so tests can check what was persisted when.</summary>
    public List<ShieldedWallet> History { get; } = [];

    public InMemoryShieldedWalletStore(string chain = "shadownet", Dictionary<string, ShieldedWallet>? wallets = null, ShieldedWallet? legacy = null) =>
        file = new ShieldedWalletFile { Chain = chain, Accounts = wallets ?? [], Legacy = legacy };

    public ShieldedWallet? Load(Address account) { lock (gate) return file.Accounts.GetValueOrDefault(account.Value)?.Copy(); }

    public void Save(ShieldedWallet wallet, Address account)
    {
        lock (gate) { file.Accounts[account.Value] = wallet.Copy(); History.Add(wallet.Copy()); }
    }

    public ShieldedWallet? Update(Address account, ShieldedWallet? initial, Action<ShieldedWallet> change)
    {
        lock (gate)
        {
            var stored = file.Accounts.GetValueOrDefault(account.Value);
            if ((stored ?? initial) is not { } before) return null;
            var wallet = before.Copy();
            change(wallet);
            if (!wallet.Equals(before) || stored is null) { file.Accounts[account.Value] = wallet.Copy(); History.Add(wallet.Copy()); }
            return wallet;
        }
    }

    public string? OperatorToken() { lock (gate) return file.OperatorToken; }
    public void SetOperatorToken(string? token) { lock (gate) file.OperatorToken = token; }
    public ShieldedWallet? LegacyWallet() { lock (gate) return file.Legacy?.Copy(); }

    public void AdoptLegacyWallet(Address account)
    {
        lock (gate)
        {
            if (file.Legacy is not { } legacy || file.Accounts.ContainsKey(account.Value)) return;
            file.Accounts[account.Value] = legacy;
            file.Legacy = null;
        }
    }

    public byte[]? LoadLeaves(Address account, int addressIndex) { lock (gate) return leaves.GetValueOrDefault($"{account.Value}/{addressIndex}"); }
    public void SaveLeaves(byte[] leaves, Address account, int addressIndex) { lock (gate) this.leaves[$"{account.Value}/{addressIndex}"] = leaves; }
    public IReadOnlyList<ShieldedContact> Contacts() { lock (gate) return file.Contacts.ToList(); }
    public void SaveContacts(IReadOnlyList<ShieldedContact> contacts) { lock (gate) file.Contacts = contacts.ToList(); }
}

/// <summary>What can go wrong between the wallet, the rollup node, the proving service and the operator.</summary>
public abstract class ShieldedError(string message) : Exception(message), IEquatable<ShieldedError>
{
    private static string Tez(ulong mutez) => AssetBalance.Format(mutez / Models.Mutez.PerTez, "tz");
    private static string Short(string text) => text[..Math.Min(8, text.Length)];

    public sealed class NotAvailable(string network) : ShieldedError($"{network} has no shielded transactions Signet knows how to reach.");
    public sealed class OperatorTokenMissing() : ShieldedError("Shielding needs the TzEL operator's access token. Enter it in Networks under Shielded Transactions.");
    public sealed class RollupMismatch(string served, string expected) : ShieldedError($"The rollup node serves {served}, not the rollup {expected} this network shields on. Nothing was sent.");
    public sealed class RollupNotConfigured(string what) : ShieldedError($"The rollup is not ready: {what}. Nothing was sent.");
    public sealed class TicketerMismatch(string configured, string expected) : ShieldedError($"The rollup's bridge ticketer is {configured}, not {expected}. Nothing was sent.");
    public sealed class PoolNotCredited(string pool) : ShieldedError($"The rollup has not credited deposit pool {Short(pool)}… yet. Try shielding it again in a minute.");
    public sealed class PoolTooSmall(ulong balance, ulong fees) : ShieldedError($"The pool holds {Tez(balance)}, less than the {Tez(fees)} of fees a shield pays. Top it up with another deposit.");
    public sealed class PoolUnknown(string pool) : ShieldedError($"Deposit pool {Short(pool)}… is not one of this wallet's.");
    public sealed class KeysExhausted(int index) : ShieldedError($"Shielded address {index} has used all of its signing keys.");
    public sealed class Proving(string detail) : ShieldedError($"The proving service could not prove the shield: {detail}");
    public sealed class OperatorRefused(string detail) : ShieldedError($"The TzEL operator refused the shield: {detail}");
    public sealed class SubmissionFailed(string detail) : ShieldedError($"The operator could not deliver the shield to the rollup: {detail}");
    public sealed class BadReply(string detail) : ShieldedError($"Unexpected reply from the rollup services: {detail}")
    {
        public string Detail { get; } = detail;
    }
    public sealed class UnsupportedWalletVersion(int version) : ShieldedError($"The shielded wallet file is version {version}, which this Signet does not read.");
    public sealed class TreeMissing(int index) : ShieldedError($"The signing tree of shielded address {index} is missing and could not be rebuilt.");
    public sealed class InsufficientShielded(ulong available, ulong needed)
        : ShieldedError($"The shielded balance available is {Tez(available)}, less than the {Tez(needed)} the unshield and its fees need.");
    public sealed class TooManyInputs(int count) : ShieldedError($"That amount would spend {count} notes; an unshield can spend at most 7. Unshield a smaller amount first.");
    public sealed class BadRecipient(string text) : ShieldedError($"“{text}” is not a Tezos account (tz1 to tz4 or KT1).");
    public sealed class FeeChanged(ulong from, ulong to)
        : ShieldedError($"The rollup's fee changed from {Tez(from)} to {Tez(to)} since the estimate. Nothing was sent; the estimate will be made again.");
    public sealed class TreeStale() : ShieldedError("The rollup's commitment tree moved while the unshield was being prepared. Nothing was sent; try again.");
    public sealed class WithdrawalUnknown(string id) : ShieldedError($"Withdrawal {id} is not one of this wallet's.");
    public sealed class WithdrawalNotReady(string detail) : ShieldedError($"This withdrawal cannot be executed on Tezos yet: {detail}");
    public sealed class BadPaymentAddress(string detail) : ShieldedError($"That is not a shielded payment address: {detail}");
    public sealed class ContactExists(string name) : ShieldedError($"That shielded address is already in the address book as “{name}”.");
    public sealed class MemoTooLong() : ShieldedError("The memo must be at most 1024 bytes.");
    /// <summary>The operator still has it: not a failure, but not delivered within the wait either.</summary>
    public sealed class SubmissionPending(string detail)
        : ShieldedError($"The operator has the request but has not delivered it to the rollup yet: {detail}. It stays listed; refresh later.");

    public bool Equals(ShieldedError? other) => other is not null && other.GetType() == GetType() && other.Message == Message;
    public override bool Equals(object? obj) => Equals(obj as ShieldedError);
    public override int GetHashCode() => HashCode.Combine(GetType(), Message);
}

public static class AddressForging
{
    /// <summary>
    /// The address as Michelson forges a <c>contract</c>: <c>00</c> and the curve tag before an implicit
    /// account's 20-byte hash, <c>01</c> before an originated contract's with a padding byte after.
    /// What a rollup outbox message names as the receiver of a <c>burn</c>. (Upstream <c>Address.forgedContract</c>.)
    /// </summary>
    public static string? ForgedContract(this Address address)
    {
        if (Base58.CheckDecode(address.Value) is not { Length: 23 } bytes) return null;
        var payload = bytes[3..];
        return (bytes[0], bytes[1], bytes[2]) switch
        {
            (6, 161, 159) => HexBytes.Encode([0, 0, .. payload]),
            (6, 161, 161) => HexBytes.Encode([0, 1, .. payload]),
            (6, 161, 164) => HexBytes.Encode([0, 2, .. payload]),
            (6, 161, 166) => HexBytes.Encode([0, 3, .. payload]),
            (2, 90, 121) => HexBytes.Encode([1, .. payload, 0]),
            _ => null,
        };
    }
}
