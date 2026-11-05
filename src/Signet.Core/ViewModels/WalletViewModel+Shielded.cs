using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using Signet.Models;
using Signet.Services;

namespace Signet.ViewModels;

/// <summary>A deposit pool and what the rollup holds for it.</summary>
/// <param name="BalanceMutez">null until the rollup has credited the pool.</param>
public sealed record ShieldedPoolStatus(DepositPool Pool, ulong? BalanceMutez)
{
    public string Id => Pool.PubkeyHash;
    public decimal? Balance => BalanceMutez is { } b ? b / Mutez.PerTez : null;
}

/// <summary>What a shield is doing right now.</summary>
public abstract record ShieldProgress
{
    public sealed record Preparing : ShieldProgress;
    /// <summary>Building the address's 65536 signing keys, first time only; the fraction done.</summary>
    public sealed record BuildingKeys(double Fraction) : ShieldProgress;
    public sealed record Depositing : ShieldProgress;
    public sealed record WaitingForLayer1 : ShieldProgress;
    public sealed record WaitingForRollup : ShieldProgress;
    public sealed record Proving : ShieldProgress;
    public sealed record Submitting : ShieldProgress;
    /// <summary>Unshield: reading the rollup's commitment tree, proving, handing over.</summary>
    public sealed record ReadingTree : ShieldProgress;
    public sealed record ProvingUnshield : ShieldProgress;
    public sealed record SubmittingUnshield : ShieldProgress;
    /// <summary>Executing a withdrawal's outbox message on Tezos.</summary>
    public sealed record Executing : ShieldProgress;
    /// <summary>Send shielded: proving and handing over.</summary>
    public sealed record ProvingTransfer : ShieldProgress;
    public sealed record SubmittingTransfer : ShieldProgress;

    public string Text => this switch
    {
        Preparing => "Checking the rollup…",
        BuildingKeys b => $"Creating your shielded address ({(int)(b.Fraction * 100)}%)…",
        Depositing => "Sending the deposit on Tezos…",
        WaitingForLayer1 => "Waiting for the Tezos block…",
        WaitingForRollup => "Waiting for the rollup to credit the deposit…",
        Proving => "Proving the shield (about a minute)…",
        Submitting => "Handing the shield to the operator…",
        ReadingTree => "Reading the rollup's commitment tree…",
        ProvingUnshield => "Proving the unshield (a minute or two)…",
        SubmittingUnshield => "Handing the unshield to the operator…",
        Executing => "Executing the withdrawal on Tezos…",
        ProvingTransfer => "Proving the transfer (a minute or two)…",
        SubmittingTransfer => "Handing the transfer to the operator…",
        _ => "",
    };
}

/// <summary>The dashboard's view of the shielded wallet after a scan.</summary>
public sealed record ShieldedSnapshot(ShieldedWallet? Wallet, IReadOnlyList<ShieldedPoolStatus> Pools, AssetBalance Row)
{
    public IReadOnlyList<ShieldedWithdrawalStatus> Withdrawals { get; init; } = [];
    public IReadOnlyList<ShieldedTransfer> Transfers { get; init; } = [];
}

/// <summary>An unshield and where it stands, as of the last scan.</summary>
/// <param name="CementedLevel">The rollup's last cemented level when it was looked at, if it was.</param>
public sealed record ShieldedWithdrawalStatus(ShieldedWithdrawal Withdrawal, int? CementedLevel)
{
    public string Id => Withdrawal.Id;

    public abstract record WithdrawalState
    {
        /// <summary>Handed to the operator, whose answer was lost: it may yet be applied.</summary>
        public sealed record Unconfirmed : WithdrawalState;
        /// <summary>With the operator; the rollup has not spent the notes yet.</summary>
        public sealed record Submitted : WithdrawalState;
        /// <summary>Applied by the rollup; its outbox message has not been found yet.</summary>
        public sealed record Queued : WithdrawalState;
        /// <summary>In the outbox, waiting for the commitment covering it to be cemented.</summary>
        public sealed record Waiting(int BlocksLeft) : WithdrawalState;
        /// <summary>Cemented: the account can execute it on Tezos.</summary>
        public sealed record Ready : WithdrawalState;
        public sealed record Executed(string Hash) : WithdrawalState;
    }

    public WithdrawalState State
    {
        get
        {
            if (Withdrawal.ExecutedHash is { } hash) return new WithdrawalState.Executed(hash);
            if (!Withdrawal.Applied) return Withdrawal.Submitted ? new WithdrawalState.Submitted() : new WithdrawalState.Unconfirmed();
            if (Withdrawal.OutboxLevel is not { } level || CementedLevel is not { } cemented) return new WithdrawalState.Queued();
            return cemented >= level ? new WithdrawalState.Ready() : new WithdrawalState.Waiting(level - cemented);
        }
    }

    public string Text => State switch
    {
        WithdrawalState.Unconfirmed => "Handed to the operator, no acknowledgement received; the notes stay reserved until the rollup applies it or you forget it",
        WithdrawalState.Submitted => "With the operator",
        WithdrawalState.Queued => "Applied by the rollup, outbox message not found yet",
        WithdrawalState.Waiting w => $"Waiting for the rollup commitment to be cemented (about {Days(w.BlocksLeft)})",
        WithdrawalState.Ready => "Ready to execute on Tezos",
        _ => "Executed on Tezos",
    };

    /// <summary>Shadownet and mainnet make a block every 6 seconds or so.</summary>
    public static string Days(int blocks)
    {
        var hours = blocks * 6.0 / 3600;
        if (hours < 1) return $"{Math.Max(1, (int)(hours * 60))} minutes";
        if (hours < 48) return $"{(int)Math.Ceiling(hours)} hours";
        return $"{(int)Math.Ceiling(hours / 24)} days";
    }
}

/// <summary>A deposit estimate together with the pool it is for and the context it is valid in (S31).</summary>
/// <param name="RollupFeeMutez">What the rollup charged at estimate time, in mutez.</param>
/// <param name="ProducerFeeMutez">The producer's fixed fee, in mutez.</param>
public sealed record ShieldEstimate(TransferEstimate Estimate, WalletViewModel.BridgeContextInfo Context, decimal Amount, DepositPool Pool,
                                    ulong RollupFeeMutez, ulong ProducerFeeMutez)
{
    /// <summary>What the note will hold if the rollup fee is still the same when the pool is shielded.</summary>
    public decimal ShieldedAmount => Math.Max(Amount - (RollupFeeMutez + ProducerFeeMutez) / Mutez.PerTez, 0);
}

/// <summary>
/// An unshield estimate: the notes it will spend and the fees of the moment, in the context it
/// was made in (S31). The rollup fee is checked again before anything is proved.
/// </summary>
/// <param name="Amount">What the recipient receives on Tezos.</param>
/// <param name="Inputs">The notes selected to pay for it, largest first.</param>
public sealed record UnshieldEstimate(WalletViewModel.BridgeContextInfo Context, decimal Amount, ulong AmountMutez, string Recipient,
                                      ulong RollupFeeMutez, ulong ProducerFeeMutez, IReadOnlyList<ShieldedNote> Inputs)
{
    public ulong TotalMutez => AmountMutez + RollupFeeMutez + ProducerFeeMutez;
    public ulong InputMutez => Inputs.Aggregate(0UL, (sum, n) => unchecked(sum + n.Value));
    /// <summary>What comes back as a note to a fresh address of ours.</summary>
    public ulong ChangeMutez => unchecked(InputMutez - TotalMutez);
    public decimal Change => ChangeMutez / Mutez.PerTez;
    public decimal Fees => (RollupFeeMutez + ProducerFeeMutez) / Mutez.PerTez;
}

/// <summary>A shielded transfer estimate (S31): the recipient, the notes it will spend and the fees.</summary>
public sealed record SendShieldedEstimate(WalletViewModel.BridgeContextInfo Context, decimal Amount, ulong AmountMutez, ShieldedContact Recipient, string Memo,
                                          ulong RollupFeeMutez, ulong ProducerFeeMutez, IReadOnlyList<ShieldedNote> Inputs)
{
    public ulong TotalMutez => AmountMutez + RollupFeeMutez + ProducerFeeMutez;
    public ulong InputMutez => Inputs.Aggregate(0UL, (sum, n) => unchecked(sum + n.Value));
    public ulong ChangeMutez => unchecked(InputMutez - TotalMutez);
    public decimal Change => ChangeMutez / Mutez.PerTez;
    public decimal Fees => (RollupFeeMutez + ProducerFeeMutez) / Mutez.PerTez;
}

public sealed record SendShieldedReceipt(ShieldedTransfer Transfer)
{
    public decimal Amount => Transfer.Amount / Mutez.PerTez;
}

/// <summary>One of the account's own shielded addresses as something to hand out.</summary>
/// <param name="Json">The reference JSON, pretty-printed, as another wallet pastes it.</param>
public sealed record ShieldedReceiveAddress(int Index, string Json, string Fingerprint);

public sealed record UnshieldReceipt(ShieldedWithdrawal Withdrawal, ulong ChangeMutez)
{
    public decimal Amount => Withdrawal.Amount / Mutez.PerTez;
}

/// <summary>The layer 1 fee for executing a withdrawal's outbox message, with the proof it was made for.</summary>
public sealed record WithdrawalExecutionEstimate(TransferEstimate Estimate, WalletViewModel.BridgeContextInfo Context, ShieldedWithdrawal Withdrawal, OutboxExecution Execution);

/// <summary>What a finished shield left behind.</summary>
/// <param name="ShieldedMutez">The note's value, in mutez, after the rollup and producer fees.</param>
public sealed record ShieldReceipt(string Pool, string? DepositHash, int? DepositLevel, ulong ShieldedMutez, ulong RollupFeeMutez, ulong ProducerFeeMutez,
                                   string SubmissionID, string? OperationHash, string Cm)
{
    public decimal Shielded => ShieldedMutez / Mutez.PerTez;
}

/// <summary>
/// Shielded tez (TzEL): the flow, as upstream's <c>WalletViewModel+Shielded.swift</c>. Every
/// write made after an <c>await</c> goes through <c>IShieldedWalletStore.Update</c>, onto the wallet
/// as it is on file at that moment.
/// </summary>
public sealed partial class WalletViewModel
{
    [ObservableProperty] private bool isPresentingShield;
    [ObservableProperty] private bool isPresentingUnshield;
    [ObservableProperty] private bool isPresentingSendShielded;
    [ObservableProperty] private bool isPresentingShieldedAddress;
    [ObservableProperty] private bool isPresentingShieldSettings;

    /// <summary>The shielded wallet of the selected account on this network, from its store, as of the last refresh.</summary>
    [ObservableProperty] private ShieldedWallet? shieldedWallet;
    /// <summary>Its deposit pools with the rollup's balances.</summary>
    [ObservableProperty] private IReadOnlyList<ShieldedPoolStatus> shieldedPools = [];
    /// <summary>Its unshields, with where each stands between the operator and layer 1.</summary>
    [ObservableProperty] private IReadOnlyList<ShieldedWithdrawalStatus> shieldedWithdrawals = [];
    /// <summary>Its shielded transfers, until the rollup has applied them (then a week for the record).</summary>
    [ObservableProperty] private IReadOnlyList<ShieldedTransfer> shieldedTransfers = [];
    /// <summary>What a shield in progress is doing, for the Shield sheet.</summary>
    [ObservableProperty] private ShieldProgress? shieldProgress;

    private readonly Dictionary<string, IShieldedWalletStore> shieldedStores = [];
    private Func<string?, string, IShieldedWalletStore> shieldedStoreFactory = (_, _) => new InMemoryShieldedWalletStore();
    private Func<ShieldedConfig, IShieldedRollup> shieldedRollupFactory = config => new RollupNodeService(config);
    /// <summary>The bridge the protocol primitives run in (<c>src/tzel.js</c>, <c>src/shield.js</c>).</summary>
    private TaquitoBridge shieldedBridge = TaquitoBridge.Shared;
    /// <summary>How long a shield waits for the rollup to credit a deposit: this many polls, this far apart.</summary>
    public int ShieldedCreditPolls { get; set; } = 36;
    public TimeSpan ShieldedPollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Sets the shielded wallet stores (by wallet directory and chain), the rollup services and
    /// the bridge; the app passes file stores and the live services, tests their own.
    /// </summary>
    public void ConfigureShielded(Func<string?, string, IShieldedWalletStore>? storeFactory = null, Func<ShieldedConfig, IShieldedRollup>? rollupFactory = null,
                                  TaquitoBridge? bridge = null)
    {
        if (storeFactory is not null) { shieldedStoreFactory = storeFactory; shieldedStores.Clear(); }
        if (rollupFactory is not null) shieldedRollupFactory = rollupFactory;
        if (bridge is not null) shieldedBridge = bridge;
    }

    /// <summary>The file-backed store the app uses: <c>tzel-&lt;chain&gt;.json</c> in the wallet directory.</summary>
    public static IShieldedWalletStore FileShieldedStore(string? directory, string chain) =>
        new FileShieldedWalletStore(directory ?? TezosClientStore.DefaultDirectory, chain);

    // ---- Settings --------------------------------------------------------------------------------

    /// <summary>Whether shielded transactions are on for <c>base</c> (saved or default), without switching.</summary>
    public bool IsShieldedEnabled(Network @base) => Resolved(Network.Named(@base.Name) ?? @base).ShieldedEnabled;

    /// <summary>Turns shielded transactions on or off for <c>base</c>.</summary>
    public void SetShieldedEnabled(bool enabled, Network @base) => ChangeShielded(@base, n => n.UsingShielded(enabled));

    /// <summary>A TzEL item of <c>base</c> as in use (saved, else shipped; "" where neither), without switching.</summary>
    public string ShieldedSetting(ShieldedConfig.Setting setting, Network @base) =>
        state.ShieldedFields(Network.Named(@base.Name) ?? @base).GetValueOrDefault(setting) ?? "";

    /// <summary>
    /// Keeps what the user typed for a TzEL item of <c>base</c>; empty text clears it (back to what
    /// we ship, or blank). False (and no change) when the text is refused. The network can
    /// shield once every item is filled in.
    /// </summary>
    public bool SetShieldedSetting(string text, ShieldedConfig.Setting setting, Network @base)
    {
        var template = Network.Named(@base.Name) ?? @base;
        var trimmed = text.Trim();
        string? kept = null;
        if (trimmed.Length > 0)
        {
            if (setting.Normalised(trimmed) is not { } valid) return false;
            kept = valid;
        }
        state = state.SettingShieldedField(kept, setting, template);
        if (template.Name == Network.Name) Network = Resolved(template);
        else PersistState();
        return true;
    }

    public void UseDefaultShieldedSetting(ShieldedConfig.Setting setting, Network @base) => SetShieldedSetting("", setting, @base);

    private void ChangeShielded(Network @base, Func<Network, Network> change)
    {
        if (@base.Name == Network.Name)
        {
            Network = change(Network);
        }
        else
        {
            state = state.RememberShielded(change(Resolved(Network.Named(@base.Name) ?? @base)));
            PersistState();
        }
    }

    /// <summary>The Shield item: shielded transactions on with known services, and an account we can sign for.</summary>
    public bool CanShield => Network.CanShield && SelectedWallet is { } wallet && wallet.KeyKind.CanSign() && !wallet.Address.IsContract;

    /// <summary>Whether the operator's access token is on file for <c>base</c>'s shielded wallets.</summary>
    public bool ShieldedOperatorTokenIsSet(Network @base)
    {
        if (Resolved(Network.Named(@base.Name) ?? @base).Shielded is null) return false;
        try { return ShieldedStoreFor(@base.Chain).OperatorToken() is { Length: > 0 }; }
        catch (Exception) { return false; }
    }

    /// <summary>Keeps the operator's token in <c>base</c>'s shielded wallet file. Empty text removes it.</summary>
    public void SetShieldedOperatorToken(string token, Network @base)
    {
        if (Resolved(Network.Named(@base.Name) ?? @base).Shielded is null) throw new ShieldedError.NotAvailable(@base.Name);
        var trimmed = token.Trim();
        ShieldedStoreFor(@base.Chain).SetOperatorToken(trimmed.Length == 0 ? null : trimmed);
    }

    /// <summary>
    /// A version 1 file held one shielded wallet for the whole network. It becomes the wallet
    /// of the account that funded its first deposit (asked of TzKT), or of the selected
    /// account when that cannot be told, so nothing shielded is lost.
    /// </summary>
    public async Task AdoptLegacyShieldedWallet()
    {
        if (!Network.CanShield || SelectedWallet is not { } selected) return;
        var store = ShieldedStoreFor(Network.Chain);
        ShieldedWallet? legacy;
        try { legacy = store.LegacyWallet(); }
        catch (Exception) { return; }
        if (legacy is null) return;
        var owner = selected.Address;
        if (legacy.Pools.Select(p => p.OperationHash).OfType<string>().FirstOrDefault() is { } hash && Network.TzktURL is { } tzkt)
        {
            try
            {
                if (await new TzKTService(tzkt).SenderOfOperation(hash) is { } sender && Wallets.Any(w => w.Address == sender)) owner = sender;
            }
            catch (Exception) { }
        }
        try { store.AdoptLegacyWallet(owner); }
        catch (Exception) { }
    }

    /// <summary>The store of <c>chain</c>'s shielded wallets in the current directory, made once. (Upstream <c>shieldedStore(for:)</c>.)</summary>
    public IShieldedWalletStore ShieldedStoreFor(string chain)
    {
        var key = $"{(WalletDirectory is { } d ? Path.GetFullPath(d) : "")}|{chain}";
        if (shieldedStores.TryGetValue(key, out var store)) return store;
        store = shieldedStoreFactory(WalletDirectory, chain);
        shieldedStores[key] = store;
        return store;
    }

    private ShieldedConfig CurrentShieldedConfig =>
        Network.Shielded is { } config && Network.ShieldedEnabled ? config : throw new ShieldedError.NotAvailable(Network.Name);

    // ---- Scan ------------------------------------------------------------------------------------

    /// <summary>
    /// The Shielded row of the dashboard, under Etherlink (or tez), while the network has
    /// shielded transactions on: the wallet's unspent notes, found by scanning the rollup's
    /// commitment tree with its viewing keys. A network without a deployment shows 0.
    /// </summary>
    private static async Task<ShieldedSnapshot?> ShieldedSnapshotFor(Address address, Network network, IShieldedWalletStore? store, IShieldedRollup? rollup, TaquitoBridge bridge)
    {
        if (!network.ShieldedEnabled) return null;
        if (store is null || rollup is null || network.Shielded is null) return new ShieldedSnapshot(null, [], AssetBalance.ShieldedPlaceholder);
        return await ScanShielded(store, address, rollup, bridge);
    }

    private static AssetBalance ShieldedRow(ShieldedWallet? wallet, IReadOnlyList<ShieldedPoolStatus> pools, string? unavailable = null)
    {
        var details = new List<AssetBalance.Detail>();
        var pooled = pools.Select(p => p.BalanceMutez ?? 0).Aggregate(0UL, (a, b) => a > ulong.MaxValue - b ? ulong.MaxValue : a + b);
        if (pooled > 0) details.Add(new AssetBalance.Detail("pooled", "In deposit pools, not yet shielded", pooled / Mutez.PerTez));
        if (wallet?.ReservedMutez is > 0 and var reserved) details.Add(new AssetBalance.Detail("unshielding", "Leaving in an unshield or transfer, not yet applied", reserved / Mutez.PerTez));
        return new AssetBalance("shielded", new AssetBalance.AssetKind.Shielded(), "Shielded", "tz", wallet?.Balance ?? 0) { Details = details, Unavailable = unavailable };
    }

    /// <summary>
    /// Reads what the rollup holds for the wallet: new notes since the last scan (decrypted in
    /// the bridge with the viewing keys), nullifiers that spend ours, and the deposit pools'
    /// balances. Saves the advanced cursors. A rollup that cannot be reached blanks the row.
    /// </summary>
    public static async Task<ShieldedSnapshot> ScanShielded(IShieldedWalletStore store, Address account, IShieldedRollup rollup, TaquitoBridge bridge)
    {
        ShieldedWallet? wallet;
        try { wallet = store.Load(account); }
        catch (Exception e) { return new ShieldedSnapshot(null, [], ShieldedRow(null, [], $"Shielded wallet unreadable: {e.Message}")); }
        if (wallet is null) return new ShieldedSnapshot(null, [], ShieldedRow(null, []));
        var current = wallet.Copy();
        try
        {
            var before = current.Copy();
            var head = await rollup.Head();
            // Notes: fetched a batch at a time (the bridge takes at most 2 MB of arguments). Every
            // commitment is kept (LeafCms, the tree an unshield proves against); a wallet whose
            // copy is shorter than its scan cursor reads again from where the copy ends.
            var size = await rollup.TreeSize(head.Hash);
            var from = Math.Min(Math.Min(current.Scanned, current.LeafCms.Count), size);
            if (current.LeafCms.Count > from) current.LeafCms.RemoveRange(from, current.LeafCms.Count - from);
            if (from < size)
            {
                var scanning = current.Addresses.Count > 0;
                var accountJSON = scanning ? (await bridge.Call("tzelAccount", current.MasterSk)).JsonText : "";
                var addressesJSON = new JsonArray(current.Addresses.Select(a => (JsonNode?)a.BridgeRecord).ToArray()).ToJsonString();
                while (from < size)
                {
                    var to = Math.Min(from + 200, size);
                    var batch = await FetchNotes(rollup, head.Hash, from, to);
                    if (batch.Count != to - from || batch[0].Index != from) throw new ShieldedError.BadReply($"notes {from} to {to - 1} are not all published");
                    current.LeafCms.AddRange(batch.Select(n => n.Hex[..64]));
                    if (scanning)
                    {
                        var notesJSON = new JsonArray(batch.Select(n => (JsonNode?)new JsonObject { ["index"] = n.Index, ["bytes"] = n.Hex }).ToArray()).ToJsonString();
                        var found = await bridge.Call("tzelScan", accountJSON, addressesJSON, notesJSON);
                        foreach (var note in found.ArrayValue ?? [])
                        {
                            if (note["index"]?.IntValue is not { } index || note["cm"]?.StringValue is not { } cm || note["rseed"]?.StringValue is not { } rseed ||
                                note["nullifier"]?.StringValue is not { } nullifier || note["v"]?.StringValue is not { } value || note["addressIndex"]?.IntValue is not { } addressIndex)
                                throw new ShieldedError.BadReply("scan result");
                            if (current.Notes.Any(n => n.Index == index)) continue;
                            current.Notes.Add(new ShieldedNote
                            {
                                Index = index, Cm = cm, Rseed = rseed, Nullifier = nullifier, ValueMutez = value, AddressIndex = addressIndex,
                                Memo = note["memo"]?.StringValue is { Length: > 0 } memo ? memo : null,
                            });
                        }
                    }
                    from = to;
                    current.Scanned = to;
                }
            }
            // Nullifiers: any of ours seen means that note is spent.
            var nullifiers = await rollup.NullifierCount(head.Hash);
            if (current.NullifiersSeen < nullifiers && current.Notes.Count > 0)
            {
                var unspent = current.Notes.Where(n => !n.Spent).Select(n => n.Nullifier).ToHashSet();
                for (var i = current.NullifiersSeen; i < nullifiers; i++)
                {
                    var seen = await rollup.Nullifier(head.Hash, i);
                    if (unspent.Contains(seen) && current.Notes.FirstOrDefault(n => n.Nullifier == seen) is { } note) note.Spent = true;
                }
            }
            current.NullifiersSeen = nullifiers;
            // Withdrawals: applied once the rollup has spent every input; then the outbox message
            // is looked for among the node's pending ones (the recipient as forged, the amount,
            // not before the level it was submitted at) and watched for the cemented level.
            var spent = current.Notes.Where(n => n.Spent).Select(n => n.Nullifier).ToHashSet();
            foreach (var w in current.Withdrawals.Where(w => !w.Applied && w.Nullifiers.All(spent.Contains))) w.Applied = true;
            int? cemented = null;
            if (current.Withdrawals.Any(w => w.Applied && w.ExecutedHash is null))
            {
                cemented = await rollup.CementedLevel();
                if (current.Withdrawals.Any(w => w.Applied && w.OutboxLevel is null))
                {
                    var pending = await rollup.PendingOutbox();
                    var claimed = current.Withdrawals.Where(w => w.OutboxLevel is not null).Select(w => $"{w.OutboxLevel}/{w.MessageIndex ?? 0}").ToHashSet();
                    foreach (var w in current.Withdrawals.Where(w => w.Applied && w.OutboxLevel is null))
                    {
                        if (new Address(w.Recipient).ForgedContract() is not { } receiver) continue;
                        var match = pending
                            .Where(e => e.Entrypoint == "burn" && e.Receiver == receiver && e.AmountMutez == w.Amount && e.OutboxLevel >= w.SubmittedLevel && !claimed.Contains($"{e.OutboxLevel}/{e.MessageIndex}"))
                            .OrderBy(e => e.OutboxLevel).ThenBy(e => e.MessageIndex).FirstOrDefault();
                        if (match is null) continue;
                        w.OutboxLevel = match.OutboxLevel;
                        w.MessageIndex = match.MessageIndex;
                        claimed.Add($"{match.OutboxLevel}/{match.MessageIndex}");
                    }
                }
            }
            // Executed withdrawals and applied transfers stay a week for the record.
            var weekAgo = DateTimeOffset.UtcNow.AddDays(-7);
            current.Withdrawals.RemoveAll(w => w.ExecutedHash is not null && w.Created < weekAgo);
            foreach (var t in current.Transfers.Where(t => !t.Applied && t.Nullifiers.All(spent.Contains))) t.Applied = true;
            current.Transfers.RemoveAll(t => t.Applied && t.Created < weekAgo);
            // Pools: what the rollup holds; a pool drained by a shield whose note we have seen is done.
            var statuses = new List<ShieldedPoolStatus>();
            var seenCms = current.Notes.Select(n => n.Cm).ToHashSet();
            foreach (var pool in current.Pools)
            {
                var balance = await rollup.PoolBalance(head.Hash, pool.PubkeyHash);
                var drained = (balance ?? 0) == 0;
                if (drained && pool.ShieldedCm is { } cm && seenCms.Contains(cm)) continue;
                if (drained && pool.OperationHash is null && pool.Created < DateTimeOffset.UtcNow.AddDays(-1)) continue;
                statuses.Add(new ShieldedPoolStatus(pool, balance));
            }
            current.Pools = statuses.Select(s => s.Pool).ToList();
            // Onto the wallet as it is on file now: a key index may have been spent, an address
            // or a record added while this scan ran, and none of that is rolled back.
            var scanned = current;
            if (store.Update(account, null, latest => Overwrite(latest, latest.Merging(scanned, before))) is { } merged) current = merged;
            var withdrawals = current.Withdrawals.Select(w => new ShieldedWithdrawalStatus(w, cemented)).ToList();
            return new ShieldedSnapshot(current, statuses, ShieldedRow(current, statuses)) { Withdrawals = withdrawals, Transfers = current.Transfers.ToList() };
        }
        catch (Exception e)
        {
            var statuses = current.Pools.Select(p => new ShieldedPoolStatus(p, null)).ToList();
            var withdrawals = current.Withdrawals.Select(w => new ShieldedWithdrawalStatus(w, null)).ToList();
            return new ShieldedSnapshot(current, statuses, ShieldedRow(current, statuses, $"Shielded balance unavailable: {e.Message}"))
            {
                Withdrawals = withdrawals, Transfers = current.Transfers.ToList(),
            };
        }
    }

    /// <summary>Replaces every field of <c>target</c> with <c>source</c>'s (the store hands out the wallet to change in place).</summary>
    private static void Overwrite(ShieldedWallet target, ShieldedWallet source)
    {
        var copy = source.Copy();
        target.Chain = copy.Chain;
        target.MasterSk = copy.MasterSk;
        target.Addresses = copy.Addresses;
        target.DepositNonce = copy.DepositNonce;
        target.Pools = copy.Pools;
        target.Notes = copy.Notes;
        target.Scanned = copy.Scanned;
        target.NullifiersSeen = copy.NullifiersSeen;
        target.LeafCms = copy.LeafCms;
        target.Withdrawals = copy.Withdrawals;
        target.Transfers = copy.Transfers;
    }

    private sealed record FetchedNote(int Index, string Hex);

    /// <summary>The published notes in <c>[from, to)</c>, eight at a time, in order.</summary>
    private static async Task<IReadOnlyList<FetchedNote>> FetchNotes(IShieldedRollup rollup, string block, int from, int to)
    {
        using var slots = new SemaphoreSlim(8);
        var tasks = Enumerable.Range(from, to - from).Select(async index =>
        {
            await slots.WaitAsync();
            try
            {
                if (await rollup.PublishedNote(block, index) is not { } bytes) return null;
                if (bytes.Length != RollupState.PublishedNoteBytes) throw new ShieldedError.BadReply($"note {index} is {bytes.Length} bytes");
                return new FetchedNote(index, HexBytes.Encode(bytes));
            }
            finally { slots.Release(); }
        }).ToList();
        var results = await Task.WhenAll(tasks);
        return results.OfType<FetchedNote>().OrderBy(n => n.Index).ToList();
    }

    // ---- Wallet and address ----------------------------------------------------------------------

    /// <summary>
    /// The network's shielded wallet with its first address ready to receive: made on first use
    /// (a random master key), the address's signing tree built natively (seconds) and kept.
    /// </summary>
    private async Task<ShieldedWallet> EnsureShieldedWallet(Address account)
    {
        var store = ShieldedStoreFor(Network.Chain);
        var wallet = store.Load(account) ?? ShieldedWallet.Make(Network.Chain);
        // The first address takes seconds to build, so the wallet is written only once it is
        // there, onto whatever is on file by then: another caller may have made the wallet
        // meanwhile, and theirs is kept (its address built next time round if it has none).
        var rounds = 0;
        while (wallet.Addresses.Count == 0 && rounds < 2)
        {
            rounds++;
            var first = await MakeAddress(0, wallet, account);
            var masterSk = wallet.MasterSk;
            var created = wallet.Copy();
            created.Addresses.Add(first);
            wallet = store.Update(account, created, latest =>
            {
                if (latest.MasterSk == masterSk && latest.Addresses.Count == 0) latest.Addresses.Add(first);
            }) ?? throw new ShieldedError.TreeMissing(0);
        }
        ShieldedWallet = wallet;
        // A new master key or first address is backed up at once, not on the next launch.
        if (rounds > 0) await BackUp();
        return wallet;
    }

    /// <summary>
    /// Hands a kernel message to the operator. Only a first attempt may supply <c>drop</c>:
    /// a definite refusal of that attempt releases its record. Retries omit it, because
    /// refusing a retry says nothing about whether an earlier attempt was accepted.
    /// Every uncertain outcome keeps the record and its note reservations.
    /// </summary>
    public static async Task<OperatorSubmission> SubmitShielded(string record, Action? drop, Func<Task<OperatorSubmission>> submit)
    {
        try
        {
            return await submit();
        }
        catch (Exception error)
        {
            if (drop is not null && error is ShieldedError.OperatorRefused or ShieldedError.Rejected)
            {
                try { drop(); } catch (Exception) { }
                throw;
            }
            var stub = record.Length > 6 ? record[6..][..Math.Min(8, record.Length - 6)] : "";
            throw new ShieldedError.SubmissionPending($"{error.Message}; the request {stub}… stays listed with its notes reserved until the rollup applies it or you forget it");
        }
    }

    /// <summary><c>SubmitShielded</c> for a retry: no record is dropped, whatever the answer.</summary>
    public static Task<OperatorSubmission> SubmitShielded(string record, Func<Task<OperatorSubmission>> submit) => SubmitShielded(record, null, submit);

    /// <summary>
    /// Address <c>index</c> of <c>wallet</c>: its keys from the bridge, its signing tree built natively
    /// (seconds, reported to the sheet) and kept beside the wallet file.
    /// </summary>
    private async Task<ShieldedAddress> MakeAddress(int index, ShieldedWallet wallet, Address account)
    {
        var keys = await AddressKeys(wallet, index);
        var leaves = await BuildLeaves(keys.AskJ, keys.PubSeed);
        ShieldedStoreFor(Network.Chain).SaveLeaves(leaves, account, index);
        var root = XMSSAuthTree.Root(keys.PubSeedBytes, leaves);
        return new ShieldedAddress
        {
            Index = index, DJ = keys.DJ, AuthRoot = HexBytes.Encode(root), PubSeed = keys.PubSeed, NkTag = keys.NkTag, EkV = keys.EkV, EkD = keys.EkD, NextKeyIndex = 0,
        };
    }

    private sealed record AddressKeysInfo(string DJ, string NkTag, string EkV, string EkD, string AskJ, string PubSeed)
    {
        public byte[] PubSeedBytes => HexBytes.Decode(PubSeed) ?? [];
    }

    private async Task<AddressKeysInfo> AddressKeys(ShieldedWallet wallet, int index)
    {
        var account = await shieldedBridge.Call("tzelAccount", wallet.MasterSk);
        var keys = await shieldedBridge.Call("tzelAddressKeys", account.JsonText, index);
        if (keys["dJ"]?.StringValue is not { } dJ || keys["nkTag"]?.StringValue is not { } nkTag || keys["ekV"]?.StringValue is not { } ekV ||
            keys["ekD"]?.StringValue is not { } ekD || keys["askJ"]?.StringValue is not { } askJ || keys["pubSeed"]?.StringValue is not { } pubSeed ||
            HexBytes.Decode(askJ)?.Length != 32 || HexBytes.Decode(pubSeed)?.Length != 32)
            throw new ShieldedError.BadReply("address keys");
        return new AddressKeysInfo(dJ, nkTag, ekV, ekD, askJ, pubSeed);
    }

    /// <summary>Builds the 65536 leaves off the UI thread, reporting progress to the sheet (at most once per percent).</summary>
    private async Task<byte[]> BuildLeaves(string askJ, string pubSeed)
    {
        if (HexBytes.Decode(askJ) is not { Length: 32 } ask || HexBytes.Decode(pubSeed) is not { Length: 32 } seed) throw new ShieldedError.BadReply("signing seed");
        ShieldProgress = new ShieldProgress.BuildingKeys(0);
        var ui = SynchronizationContext.Current;
        var last = -1;
        return await Task.Run(() => XMSSAuthTree.BuildLeaves(ask, seed, done =>
        {
            var percent = done * 100 / XMSSAuthTree.Size;
            if (Interlocked.Exchange(ref last, percent) == percent) return;
            var fraction = (double)done / XMSSAuthTree.Size;
            if (ui is null) ShieldProgress = new ShieldProgress.BuildingKeys(fraction);
            else ui.Post(_ => ShieldProgress = new ShieldProgress.BuildingKeys(fraction), null);
        }));
    }

    /// <summary>
    /// The leaves of an address's signing tree from the store, rebuilt from the master key when
    /// the file is missing (it is not backed up). The root must be the address's.
    /// </summary>
    private async Task<byte[]> LeavesOf(ShieldedAddress address, ShieldedWallet wallet, Address account)
    {
        var store = ShieldedStoreFor(Network.Chain);
        var pubSeed = HexBytes.Decode(address.PubSeed) ?? throw new ShieldedError.TreeMissing(address.Index);
        if (store.LoadLeaves(account, address.Index) is { } stored && HexBytes.Encode(XMSSAuthTree.Root(pubSeed, stored)) == address.AuthRoot) return stored;
        var keys = await AddressKeys(wallet, address.Index);
        if (keys.PubSeed != address.PubSeed) throw new ShieldedError.TreeMissing(address.Index);
        var leaves = await BuildLeaves(keys.AskJ, keys.PubSeed);
        if (HexBytes.Encode(XMSSAuthTree.Root(pubSeed, leaves)) != address.AuthRoot) throw new ShieldedError.TreeMissing(address.Index);
        store.SaveLeaves(leaves, account, address.Index);
        return leaves;
    }

    private static string JsonText(object value) => JsonSerializer.Serialize(value);

    // ---- Deposit ---------------------------------------------------------------------------------

    /// <summary>
    /// Everything before money moves: the rollup node serves the expected rollup, is configured
    /// and names our ticketer; the wallet and its address exist; a pool is reserved for the next
    /// deposit (an unfunded one is reused). Nothing is sent.
    /// </summary>
    public async Task<DepositPool> PrepareShield()
    {
        var config = CurrentShieldedConfig;
        if (SelectedWallet?.Address is not { } account) throw new WalletError.EmptyAlias();
        var context = BridgeContext;
        ShieldProgress = new ShieldProgress.Preparing();
        try
        {
            var rollup = shieldedRollupFactory(config);
            var served = await rollup.SmartRollupAddress();
            if (served != config.RollupAddress) throw new ShieldedError.RollupMismatch(served, config.RollupAddress);
            var head = await rollup.Head();
            if (!await rollup.VerifierConfigured(head.Hash)) throw new ShieldedError.RollupNotConfigured("its verifier is not installed");
            var ticketer = await rollup.BridgeTicketer(head.Hash);
            if (ticketer != config.Ticketer.Value) throw new ShieldedError.TicketerMismatch(ticketer ?? "unset", config.Ticketer.Value);
            var authDomain = await rollup.AuthDomain(head.Hash);
            var wallet = await EnsureShieldedWallet(account);
            if (BridgeContext != context) throw new WalletError.ContextChanged();
            if (wallet.Pools.FirstOrDefault(p => p.OperationHash is null && p.AuthDomain == authDomain) is { } unfunded) return unfunded;
            var address = wallet.Addresses.FirstOrDefault() ?? throw new ShieldedError.TreeMissing(0);
            var store = ShieldedStoreFor(Network.Chain);
            ulong nonce = 0;
            if (store.Update(account, null, latest => { nonce = latest.DepositNonce; latest.DepositNonce++; }) is null) throw new ShieldedError.TreeMissing(0);
            var made = await shieldedBridge.Call("tzelDepositPool", wallet.MasterSk, address.Index, nonce.ToString(CultureInfo.InvariantCulture), authDomain, address.AuthRoot, address.PubSeed);
            if (made["pubkeyHash"]?.StringValue is not { } pubkeyHash || made["blind"]?.StringValue is not { } blind || HexBytes.Decode(pubkeyHash)?.Length != 32)
                throw new ShieldedError.BadReply("deposit pool");
            var pool = new DepositPool
            {
                PubkeyHash = pubkeyHash, Blind = blind, AddressIndex = address.Index, AuthDomain = authDomain, DepositedMutez = "0",
                Created = DateTimeOffset.UtcNow,
            };
            // The pool is on file before any tez go to it.
            ShieldedWallet = store.Update(account, null, latest => latest.Pools.Add(pool)) ?? throw new ShieldedError.TreeMissing(0);
            await BackUp();
            return pool;
        }
        finally { ShieldProgress = null; }
    }

    /// <summary>The layer 1 fee for depositing <c>amount</c> into <c>pool</c>, and the rollup's fee of the moment.</summary>
    public async Task<ShieldEstimate> EstimateShield(decimal amount, DepositPool pool)
    {
        var config = CurrentShieldedConfig;
        if (SelectedWallet is not { } wallet || BridgeContext is not { } context) throw new WalletError.EmptyAlias();
        if (amount <= 0 || !Mutez.IsValid(Mutez.FromTez(amount))) throw new SendViewModel.SendError.InvalidAmount();
        var chain = this.chain;
        var rollup = shieldedRollupFactory(config);
        var deposit = new ShieldDeposit(config.Ticketer, config.RollupAddress, pool.PubkeyHash, amount);
        var estimate = chain.EstimateShieldDeposit(wallet, deposit);
        var fee = rollup.RequiredTxFee(await rollup.Head());
        var result = new ShieldEstimate(await estimate, context, amount, pool, await fee, config.ProducerFeeMutez);
        if (BridgeContext != context) throw new WalletError.ContextChanged();
        return result;
    }

    /// <summary>
    /// Shields <c>approved.Amount</c> tez from the selected account: the deposit to the ticketer with
    /// the approved limits (S14), the layer 1 block, then <c>ShieldPool</c> once the rollup has
    /// credited the pool. Refused up front without the operator token, so no tez are parked in
    /// a pool that cannot be shielded. A deposit that lands but whose shield fails leaves the
    /// pool listed in the sheet to be shielded again.
    /// </summary>
    public async Task<ShieldReceipt> ShieldTez(ShieldEstimate approved, string? passphrase)
    {
        var config = CurrentShieldedConfig;
        if (SelectedWallet is not { } wallet) throw new WalletError.EmptyAlias();
        if (approved.Estimate.Limits is not { } limits) throw new WalletError.NotEstimated();
        if (approved.Context != BridgeContext || !CanShield) throw new WalletError.ContextChanged();
        var signer = SigningKeyFor(wallet, passphrase) ?? throw new SendViewModel.SendError.NoSecretKey();
        var store = ShieldedStoreFor(Network.Chain);
        if (store.OperatorToken() is not { Length: > 0 }) throw new ShieldedError.OperatorTokenMissing();
        if (store.Load(wallet.Address)?.Pool(approved.Pool.PubkeyHash) is null) throw new ShieldedError.PoolUnknown(approved.Pool.PubkeyHash);
        if (!ulong.TryParse(Mutez.FromTez(approved.Amount), NumberStyles.None, CultureInfo.InvariantCulture, out var added) || added + config.ProducerFeeMutez == 0)
            throw new SendViewModel.SendError.InvalidAmount();

        ShieldProgress = new ShieldProgress.Depositing();
        try
        {
            var chain = this.chain;
            var deposit = new ShieldDeposit(config.Ticketer, config.RollupAddress, approved.Pool.PubkeyHash, approved.Amount);
            var sent = await chain.SendShieldDeposit(wallet, signer, deposit, limits);
            // On file before the block is waited for: the pool is funded (or about to be) by this
            // operation (and put back should a scan have retired it meanwhile: the tez are on their way).
            var funded = store.Update(wallet.Address, null, latest =>
            {
                if (latest.Pool(approved.Pool.PubkeyHash) is null) latest.Pools.Add(ShieldedJSON.Clone(approved.Pool));
                if (latest.Pool(approved.Pool.PubkeyHash) is not { } pool) return;
                pool.OperationHash = sent.Hash;
                var deposited = ShieldedWallet.Mutez(pool.DepositedMutez);
                pool.DepositedMutez = (deposited > ulong.MaxValue - added ? ulong.MaxValue : deposited + added).ToString(CultureInfo.InvariantCulture);
            });
            if (funded is not null) ShieldedWallet = funded;
            ShieldProgress = new ShieldProgress.WaitingForLayer1();
            int level;
            try { level = await chain.WaitForConfirmation(sent.Hash); }
            catch (Exception) when (!sent.Injected) { throw new WalletError.OutcomeUnknown(sent.Hash); }
            return await ShieldPool(approved.Pool.PubkeyHash, sent.Hash, level);
        }
        finally { ShieldProgress = null; }
    }

    // ---- Shield ----------------------------------------------------------------------------------

    /// <summary>
    /// Drains deposit pool <c>pubkeyHash</c> into a note of ours: waits for the rollup to show the
    /// pool's balance, takes the rollup's fee of the moment and the producer's fee off it,
    /// reserves the address's next WOTS+ key (on file before it signs), builds the two output
    /// notes, the signature and the witness in the bridge, has the proving service prove it and
    /// the operator deliver it, then records the note's commitment against the pool.
    /// </summary>
    public async Task<ShieldReceipt> ShieldPool(string pubkeyHash, string? depositHash = null, int? depositLevel = null)
    {
        var config = CurrentShieldedConfig;
        if (SelectedWallet?.Address is not { } account) throw new WalletError.EmptyAlias();
        var context = BridgeContext;
        var store = ShieldedStoreFor(Network.Chain);
        if (store.OperatorToken() is not { Length: > 0 } token) throw new ShieldedError.OperatorTokenMissing();
        if (store.Load(account) is not { } wallet || wallet.Pool(pubkeyHash) is not { } pool) throw new ShieldedError.PoolUnknown(pubkeyHash);
        if (wallet.AddressAt(pool.AddressIndex) is null) throw new ShieldedError.TreeMissing(pool.AddressIndex);
        var rollup = shieldedRollupFactory(config);
        ShieldProgress = new ShieldProgress.WaitingForRollup();
        try
        {
            // The rollup sees the layer 1 deposit a level or two after the block.
            var head = await rollup.Head();
            var balance = await rollup.PoolBalance(head.Hash, pubkeyHash) ?? 0;
            var polls = 0;
            while (balance == 0 && polls < ShieldedCreditPolls)
            {
                await Task.Delay(ShieldedPollInterval);
                polls++;
                head = await rollup.Head();
                balance = await rollup.PoolBalance(head.Hash, pubkeyHash) ?? 0;
            }
            if (balance == 0) throw new ShieldedError.PoolNotCredited(pubkeyHash);
            var fee = await rollup.RequiredTxFee(head);
            var producerFee = config.ProducerFeeMutez;
            if (balance <= fee + producerFee) throw new ShieldedError.PoolTooSmall(balance, fee + producerFee);
            var value = balance - fee - producerFee;
            var authDomain = await rollup.AuthDomain(head.Hash);
            if (authDomain != pool.AuthDomain) throw new ShieldedError.RollupNotConfigured("its authentication domain changed since the deposit");
            if (BridgeContext != context) throw new WalletError.ContextChanged();

            // One signature per key index, ever: the index is spent on file before it is used, taken
            // from the wallet as it is on file now (a scan or another spend may have moved it on
            // since it was read).
            var keyIdx = 0;
            var spent = store.Update(account, null, latest =>
            {
                var at = latest.AddressAt(pool.AddressIndex) ?? throw new ShieldedError.TreeMissing(pool.AddressIndex);
                keyIdx = at.NextKeyIndex;
                if (keyIdx >= XMSSAuthTree.Size) throw new ShieldedError.KeysExhausted(pool.AddressIndex);
                at.NextKeyIndex = keyIdx + 1;
            });
            if (spent?.AddressAt(pool.AddressIndex) is not { } address) throw new ShieldedError.PoolUnknown(pubkeyHash);
            wallet = spent;
            ShieldedWallet = wallet;
            // Windows addition (audit G09): the spent key index is backed up before it signs.
            await BackUp();

            var leaves = await LeavesOf(address, wallet, account);
            var pubSeed = HexBytes.Decode(address.PubSeed) ?? throw new ShieldedError.TreeMissing(address.Index);
            var path = XMSSAuthTree.Path(XMSSAuthTree.Levels(pubSeed, leaves), keyIdx).Select(p => HexBytes.Encode(p)).ToList();
            var keys = await AddressKeys(wallet, address.Index);
            var seeds = await shieldedBridge.Call("tzelAccount", wallet.MasterSk);
            if (seeds["outgoingSeed"]?.StringValue is not { } outgoingSeed) throw new ShieldedError.BadReply("account seeds");
            var request = new JsonObject
            {
                ["askJ"] = keys.AskJ, ["outgoingSeed"] = outgoingSeed, ["address"] = address.BridgeRecord, ["keyIdx"] = keyIdx,
                ["authPath"] = new JsonArray(path.Select(p => (JsonNode?)p).ToArray()),
                ["authDomain"] = authDomain, ["pubkeyHash"] = pubkeyHash, ["blind"] = pool.Blind,
                ["v"] = value.ToString(CultureInfo.InvariantCulture), ["fee"] = fee.ToString(CultureInfo.InvariantCulture), ["producerFee"] = producerFee.ToString(CultureInfo.InvariantCulture),
                ["producerAddress"] = JsonSerializer.SerializeToNode(config.ProducerAddressJSON()),
            };
            var built = await shieldedBridge.Call("tzelBuildShield", request.ToJsonString());
            if (Strings(built["witness"]) is not { Count: 1 + 16 + 133 + 16 + 5 } witness ||
                built["clientCm"]?.StringValue is not { } clientCm || built["clientEnc"]?.StringValue is not { } clientEnc ||
                built["producerCm"]?.StringValue is not { } producerCm || built["producerEnc"]?.StringValue is not { } producerEnc ||
                Strings(built["publicOutputs"]) is not { Count: 9 } outputs)
                throw new ShieldedError.BadReply("shield witness");

            ShieldProgress = new ShieldProgress.Proving();
            var bundle = await rollup.Prove("run_shield", witness);
            var message = new JsonObject
            {
                ["pubkeyHash"] = pubkeyHash, ["fee"] = fee.ToString(CultureInfo.InvariantCulture), ["v"] = value.ToString(CultureInfo.InvariantCulture),
                ["producerFee"] = producerFee.ToString(CultureInfo.InvariantCulture),
                ["proofBytes"] = bundle.ProofBytes, ["outputPreimage"] = Array(bundle.OutputPreimage), ["expectedOutputs"] = Array(outputs),
                ["programHash"] = bundle.ProgramHash,
                ["clientCm"] = clientCm, ["clientEnc"] = clientEnc, ["producerCm"] = producerCm, ["producerEnc"] = producerEnc,
            };
            var encoded = await shieldedBridge.Call("tzelEncodeShieldMessage", message.ToJsonString());
            if (encoded.StringValue is not { } payload || HexBytes.Decode(payload) is null)
                throw new ShieldedError.BadReply($"shield message: {Truncate(encoded.JsonText, 200)}");

            ShieldProgress = new ShieldProgress.Submitting();
            var submission = await rollup.Deliver("shield", payload, config.RollupAddress, token);
            // The note is ours once the scan sees it; the pool is retired when it is seen drained.
            if (store.Update(account, null, latest => { if (latest.Pool(pubkeyHash) is { } p) p.ShieldedCm = clientCm; }) is { } latestWallet)
                ShieldedWallet = latestWallet;
            await RefreshAfterOperation();
            return new ShieldReceipt(pubkeyHash, depositHash, depositLevel, value, fee, producerFee, submission.Id, submission.OperationHash, clientCm);
        }
        finally { ShieldProgress = null; }
    }

    private static List<string>? Strings(JSONValue? value) =>
        value?.ArrayValue is { } items && items.All(i => i.StringValue is not null) ? items.Select(i => i.StringValue!).ToList() : null;

    private static JsonArray Array(IEnumerable<string> items) => new(items.Select(i => (JsonNode?)i).ToArray());

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];

    // ---- Unshield --------------------------------------------------------------------------------

    /// <summary>The kernel takes at most seven inputs per unshield.</summary>
    public const int MaxUnshieldInputs = 7;

    /// <summary>
    /// The notes that pay for <c>total</c>, largest first until it is covered, as the reference
    /// wallet selects (the fewest signatures and the smallest proof).
    /// </summary>
    public static IReadOnlyList<ShieldedNote> SelectNotes(IReadOnlyList<ShieldedNote> notes, ulong total)
    {
        var available = notes.Aggregate(0UL, (sum, n) => unchecked(sum + n.Value));
        if (available < total) throw new ShieldedError.InsufficientShielded(available, total);
        var selected = new List<ShieldedNote>();
        ulong sum = 0;
        foreach (var note in notes.OrderByDescending(n => n.Value))
        {
            selected.Add(note);
            sum = unchecked(sum + note.Value);
            if (sum >= total) break;
        }
        if (selected.Count > MaxUnshieldInputs) throw new ShieldedError.TooManyInputs(selected.Count);
        return selected;
    }

    /// <summary>
    /// What unshielding <c>amount</c> tez to <c>recipient</c> would spend: the rollup's fee of the moment,
    /// the producer's fee and the notes chosen to cover them. Nothing is sent.
    /// </summary>
    public async Task<UnshieldEstimate> EstimateUnshield(decimal amount, string recipient)
    {
        var config = CurrentShieldedConfig;
        if (SelectedWallet is not { } wallet || BridgeContext is not { } context) throw new WalletError.EmptyAlias();
        if (amount <= 0 || !Mutez.IsValid(Mutez.FromTez(amount)) || !ulong.TryParse(Mutez.FromTez(amount), NumberStyles.None, CultureInfo.InvariantCulture, out var amountMutez) || amountMutez == 0)
            throw new SendViewModel.SendError.InvalidAmount();
        var to = recipient.Trim();
        if (!new Address(to).IsValidAccount || !(to.StartsWith("tz", StringComparison.Ordinal) || to.StartsWith("KT1", StringComparison.Ordinal)) ||
            to.StartsWith("tz5", StringComparison.Ordinal) || to.StartsWith("tz6", StringComparison.Ordinal))
            throw new ShieldedError.BadRecipient(to);
        var shielded = ShieldedStoreFor(Network.Chain).Load(wallet.Address);
        var rollup = shieldedRollupFactory(config);
        var fee = await rollup.RequiredTxFee(await rollup.Head());
        ulong total;
        try { total = checked(amountMutez + fee + config.ProducerFeeMutez); }
        catch (OverflowException) { throw new SendViewModel.SendError.InvalidAmount(); }
        var inputs = SelectNotes(shielded?.AvailableNotes ?? [], total);
        if (BridgeContext != context) throw new WalletError.ContextChanged();
        return new UnshieldEstimate(context, amount, amountMutez, to, fee, config.ProducerFeeMutez, inputs);
    }

    /// <summary>
    /// Unshields <c>approved.Amount</c> tez to <c>approved.Recipient</c>: reads the rollup's tree again
    /// (an input spent meanwhile is refused, as is a changed rollup fee), makes a fresh address
    /// for the change and builds its signing tree, spends every input's next WOTS+ key index on
    /// disk <i>before</i> signing, has the bridge build the change and producer notes, the sighash,
    /// the signatures and the witness, proves it and hands it to the operator. The withdrawal is
    /// on file as soon as the operator has it, so its notes stay reserved; the tez reach the
    /// recipient on Tezos once the covering commitment is cemented and the outbox message is
    /// executed (<c>ExecuteWithdrawal</c>). Needs no layer 1 signature, so no password.
    /// </summary>
    public async Task<UnshieldReceipt> UnshieldTez(UnshieldEstimate approved)
    {
        var config = CurrentShieldedConfig;
        if (SelectedWallet?.Address is not { } account) throw new WalletError.EmptyAlias();
        if (approved.Context != BridgeContext || !CanShield) throw new WalletError.ContextChanged();
        var context = BridgeContext;
        var store = ShieldedStoreFor(Network.Chain);
        if (store.OperatorToken() is not { Length: > 0 } token) throw new ShieldedError.OperatorTokenMissing();
        var rollup = shieldedRollupFactory(config);
        ShieldProgress = new ShieldProgress.ReadingTree();
        try
        {
            var spend = await PrepareSpend(approved.Inputs, approved.TotalMutez, approved.RollupFeeMutez, false, account, store, rollup, context);
            var change = spend.ChangeMutez;
            var request = new JsonObject
            {
                ["account"] = JsonNode.Parse(spend.Seeds.JsonText), ["authDomain"] = spend.AuthDomain, ["root"] = spend.Root, ["leafCms"] = Array(spend.Wallet.LeafCms),
                ["vPub"] = approved.AmountMutez.ToString(CultureInfo.InvariantCulture), ["fee"] = spend.Fee.ToString(CultureInfo.InvariantCulture),
                ["producerFee"] = config.ProducerFeeMutez.ToString(CultureInfo.InvariantCulture), ["recipient"] = approved.Recipient,
                ["inputs"] = new JsonArray(spend.InputRecords.Select(r => (JsonNode?)r).ToArray()),
                ["changeAddress"] = spend.ChangeAddress?.BridgeRecord, ["producerAddress"] = JsonSerializer.SerializeToNode(config.ProducerAddressJSON()),
            };
            var built = await shieldedBridge.Call("tzelBuildUnshield", request.ToJsonString());
            var n = spend.Inputs.Count;
            if (Strings(built["witness"]) is not { } witness || witness.Count != 1 + 6 + n * 206 + 15 ||
                Strings(built["nullifiers"]) is not { } nullifiers || !nullifiers.SequenceEqual(spend.Inputs.Select(i => i.Nullifier)) ||
                built["producerCm"]?.StringValue is not { } producerCm || built["producerEnc"]?.StringValue is not { } producerEnc ||
                Strings(built["publicOutputs"]) is not { } outputs || outputs.Count != 2 + n + 7 ||
                built["changeMutez"]?.StringValue != change.ToString(CultureInfo.InvariantCulture))
                throw new ShieldedError.BadReply("unshield witness");
            var changeCm = built["changeCm"]?.StringValue;
            var changeEnc = built["changeEnc"]?.StringValue;
            if ((change > 0) != (changeCm is not null && changeEnc is not null)) throw new ShieldedError.BadReply("unshield change note");

            ShieldProgress = new ShieldProgress.ProvingUnshield();
            var bundle = await rollup.Prove("run_unshield", witness);
            var message = new JsonObject
            {
                ["root"] = spend.Root, ["nullifiers"] = Array(nullifiers), ["vPub"] = approved.AmountMutez.ToString(CultureInfo.InvariantCulture),
                ["fee"] = spend.Fee.ToString(CultureInfo.InvariantCulture), ["recipient"] = approved.Recipient,
                ["cmChange"] = changeCm ?? HexBytes.Encode(XMSSAuthTree.FeltOfInt(0)), ["changeEnc"] = changeEnc, ["cmFee"] = producerCm, ["producerEnc"] = producerEnc,
                ["proofBytes"] = bundle.ProofBytes, ["outputPreimage"] = Array(bundle.OutputPreimage), ["expectedOutputs"] = Array(outputs), ["programHash"] = bundle.ProgramHash,
            };
            var encoded = await shieldedBridge.Call("tzelEncodeUnshieldMessage", message.ToJsonString());
            if (encoded.StringValue is not { } payload || HexBytes.Decode(payload) is null)
                throw new ShieldedError.BadReply($"unshield message: {Truncate(encoded.JsonText, 200)}");

            ShieldProgress = new ShieldProgress.SubmittingUnshield();
            // On file, payload included, before the operator is contacted (S15 for shielded
            // spends): the inputs are reserved from here, whatever the network does next.
            var localID = ShieldedWithdrawal.LocalID();
            var withdrawal = new ShieldedWithdrawal
            {
                SubmissionID = localID, Recipient = approved.Recipient, AmountMutez = approved.AmountMutez.ToString(CultureInfo.InvariantCulture),
                RollupFeeMutez = spend.Fee.ToString(CultureInfo.InvariantCulture), ProducerFeeMutez = config.ProducerFeeMutez.ToString(CultureInfo.InvariantCulture),
                Nullifiers = nullifiers, ChangeCm = changeCm, SubmittedLevel = spend.Head.Level, Created = DateTimeOffset.UtcNow,
                PayloadHex = payload, Submitted = false,
            };
            UpdateWithdrawals(account, store, list => list.Add(ShieldedJSON.Clone(withdrawal)));
            await BackUp();
            var submission = await SubmitShielded(localID, () => UpdateWithdrawals(account, store, list => list.RemoveAll(w => w.Id == localID)),
                () => rollup.Submit("unshield", payload, config.RollupAddress, token));
            withdrawal.SubmissionID = submission.Id;
            withdrawal.Submitted = true;
            withdrawal.OperationHash = submission.OperationHash;
            var acknowledged = ShieldedJSON.Clone(withdrawal);
            UpdateWithdrawals(account, store, list => { var at = list.FindIndex(w => w.Id == localID); if (at >= 0) list[at] = ShieldedJSON.Clone(acknowledged); });
            try
            {
                var delivered = await rollup.AwaitDelivery(submission, token);
                withdrawal.OperationHash = delivered.OperationHash;
                UpdateWithdrawals(account, store, list => { if (list.FirstOrDefault(w => w.Id == withdrawal.Id) is { } w) w.OperationHash = withdrawal.OperationHash; });
            }
            catch (ShieldedError.SubmissionFailed)
            {
                // The operator says it gave up on it: the notes are free again.
                try { UpdateWithdrawals(account, store, list => list.RemoveAll(w => w.Id == submission.Id)); } catch (Exception) { }
                throw;
            }
            catch (Exception)
            {
                // Still with the operator, or its answer could not be read: the record stays and
                // the notes stay reserved; the scan sees it land, or Forget frees them by hand.
            }
            await RefreshAfterOperation();
            return new UnshieldReceipt(withdrawal, change);
        }
        finally { ShieldProgress = null; }
    }

    /// <summary>
    /// What an unshield and a shielded transfer do alike: the inputs checked against the
    /// rollup's tree and fee as they are now, the change address made, every input's key
    /// index spent on disk, and the auth paths taken from the native trees.
    /// </summary>
    private sealed record SpendPreparation(ShieldedWallet Wallet, RollupHead Head, string Root, string AuthDomain, ulong Fee, IReadOnlyList<ShieldedNote> Inputs,
                                           ulong ChangeMutez, ShieldedAddress? ChangeAddress, IReadOnlyList<JsonObject> InputRecords, JSONValue Seeds);

    private async Task<SpendPreparation> PrepareSpend(IReadOnlyList<ShieldedNote> approvedInputs, ulong totalMutez, ulong approvedFee, bool changeAddressAlways,
                                                      Address account, IShieldedWalletStore store, IShieldedRollup rollup, BridgeContextInfo? context)
    {
        // The tree as the rollup has it now, our notes and the nullifiers with it.
        var snapshot = await ScanShielded(store, account, rollup, shieldedBridge);
        if (snapshot.Row.Unavailable is { } problem) throw new ShieldedError.BadReply(problem);
        var wallet = snapshot.Wallet ?? throw new ShieldedError.InsufficientShielded(0, totalMutez);
        var head = await rollup.Head();
        var root = await rollup.TreeRoot(head.Hash);
        var size = await rollup.TreeSize(head.Hash);
        if (wallet.LeafCms.Count != size) throw new ShieldedError.TreeStale();
        var fee = await rollup.RequiredTxFee(head);
        if (fee != approvedFee) throw new ShieldedError.FeeChanged(approvedFee, fee);
        var authDomain = await rollup.AuthDomain(head.Hash);
        var available = wallet.AvailableNotes.GroupBy(n => n.Index).ToDictionary(g => g.Key, g => g.First());
        var inputs = approvedInputs.Select(note =>
            available.TryGetValue(note.Index, out var now) && now.Cm == note.Cm && now.Value == note.Value ? now : throw new ShieldedError.TreeStale()).ToList();
        var sum = inputs.Aggregate(0UL, (s, n) => unchecked(s + n.Value));
        if (sum < totalMutez) throw new ShieldedError.InsufficientShielded(sum, totalMutez);
        var change = sum - totalMutez;
        if (BridgeContext != context) throw new WalletError.ContextChanged();

        // Change goes to a fresh address of ours, made (and its signing tree built) first.
        ShieldedAddress? changeAddress = null;
        if (change > 0 || changeAddressAlways)
            changeAddress = await MakeAddress((wallet.Addresses.Select(a => (int?)a.Index).Max() ?? -1) + 1, wallet, account);
        // One signature per key index, ever: every input's index is spent on file before it is
        // used, taken from the wallet as it is on file now (a scan or another spend may have
        // moved it on since it was read, and may have reserved or spent an input).
        var reserved = new List<(ShieldedNote Note, ShieldedAddress Address, int KeyIdx)>();
        var spent = store.Update(account, null, latest =>
        {
            if (changeAddress is { } made && latest.AddressAt(made.Index) is null) latest.Addresses.Add(ShieldedJSON.Clone(made));
            var taken = latest.ReservedNullifiers;
            reserved.Clear();
            foreach (var note in inputs)
            {
                if (latest.Notes.FirstOrDefault(n => n.Index == note.Index) is not { } now || now.Cm != note.Cm || now.Spent || taken.Contains(now.Nullifier))
                    throw new ShieldedError.TreeStale();
                var at = latest.AddressAt(note.AddressIndex) ?? throw new ShieldedError.TreeMissing(note.AddressIndex);
                var keyIdx = at.NextKeyIndex;
                if (keyIdx >= XMSSAuthTree.Size) throw new ShieldedError.KeysExhausted(note.AddressIndex);
                at.NextKeyIndex = keyIdx + 1;
                reserved.Add((note, ShieldedJSON.Clone(at), keyIdx));
            }
        }) ?? throw new ShieldedError.InsufficientShielded(0, totalMutez);
        wallet = spent;
        ShieldedWallet = wallet;
        if (changeAddress is { } changeMade) changeAddress = wallet.AddressAt(changeMade.Index);

        // Each input's auth path from its address's native tree.
        var trees = new Dictionary<int, IReadOnlyList<byte[]>>();
        var inputRecords = new List<JsonObject>();
        foreach (var r in reserved)
        {
            if (!trees.ContainsKey(r.Address.Index))
            {
                var leaves = await LeavesOf(r.Address, wallet, account);
                var pubSeed = HexBytes.Decode(r.Address.PubSeed) ?? throw new ShieldedError.TreeMissing(r.Address.Index);
                trees[r.Address.Index] = XMSSAuthTree.Levels(pubSeed, leaves);
            }
            var path = XMSSAuthTree.Path(trees[r.Address.Index], r.KeyIdx).Select(p => HexBytes.Encode(p));
            inputRecords.Add(new JsonObject
            {
                ["note"] = new JsonObject { ["index"] = r.Note.Index, ["cm"] = r.Note.Cm, ["rseed"] = r.Note.Rseed, ["v"] = r.Note.ValueMutez },
                ["address"] = r.Address.BridgeRecord, ["keyIdx"] = r.KeyIdx, ["authPath"] = Array(path),
            });
        }
        var seeds = await shieldedBridge.Call("tzelAccount", wallet.MasterSk);
        return new SpendPreparation(wallet, head, root, authDomain, fee, inputs, change, changeAddress, inputRecords, seeds);
    }

    private void UpdateWithdrawals(Address account, IShieldedWalletStore store, Action<List<ShieldedWithdrawal>> change)
    {
        if (store.Update(account, null, latest => change(latest.Withdrawals)) is { } latest) ShieldedWallet = latest;
    }

    private void UpdateTransfers(Address account, IShieldedWalletStore store, Action<List<ShieldedTransfer>> change)
    {
        if (store.Update(account, null, latest => change(latest.Transfers)) is { } latest) ShieldedWallet = latest;
    }

    // ---- Shielded addresses ----------------------------------------------------------------------

    /// <summary>
    /// A shielded recipient is nothing but a payment address (the 2496-byte record: diversifier,
    /// auth root, public seed, nullifier tag and two ML-KEM keys); the protocol has no shorter
    /// name for one. Reads the reference JSON another wallet printed, or a saved contact's, and
    /// names it by its fingerprint, the hash of the canonical record.
    /// </summary>
    public async Task<ShieldedContact> ShieldedRecipient(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) throw new ShieldedError.BadPaymentAddress("nothing was given");
        JSONValue parsed;
        try { parsed = await shieldedBridge.Call("tzelPaymentAddress", trimmed); }
        catch (Exception e) { throw new ShieldedError.BadPaymentAddress(e.Message); }
        if (parsed["fingerprint"]?.StringValue is not { } fingerprint || HexBytes.Decode(fingerprint)?.Length != 32 || parsed["address"]?.ObjectValue is not { } fields)
            throw new ShieldedError.BadPaymentAddress("unexpected reply");
        var address = fields.Where(f => f.Value.StringValue is not null).ToDictionary(f => f.Key, f => f.Value.StringValue!);
        if (address.Count != 6) throw new ShieldedError.BadPaymentAddress("fields missing");
        if (ShieldedContacts().FirstOrDefault(c => c.Fingerprint == fingerprint) is { } known) return known;
        return new ShieldedContact { Name = "", Fingerprint = fingerprint, Address = address, Created = DateTimeOffset.UtcNow };
    }

    /// <summary>The network's shielded address book (shared by every account in the directory).</summary>
    public IReadOnlyList<ShieldedContact> ShieldedContacts() =>
        Network.Shielded is null ? [] : ShieldedStoreFor(Network.Chain).Contacts();

    /// <summary>Adds <c>contact</c> under <c>name</c>; the same address twice is refused.</summary>
    public void AddShieldedContact(ShieldedContact contact, string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) throw new WalletError.EmptyAlias();
        var store = ShieldedStoreFor(Network.Chain);
        var contacts = store.Contacts().ToList();
        if (contacts.FirstOrDefault(c => c.Fingerprint == contact.Fingerprint) is { } existing) throw new ShieldedError.ContactExists(existing.Name);
        if (contacts.Any(c => string.Equals(c.Name, trimmed, StringComparison.OrdinalIgnoreCase))) throw new WalletError.AliasExists(trimmed);
        contacts.Add(new ShieldedContact { Name = trimmed, Fingerprint = contact.Fingerprint, Address = contact.Address, Created = DateTimeOffset.UtcNow });
        store.SaveContacts(contacts);
    }

    public void RemoveShieldedContact(string fingerprint)
    {
        var store = ShieldedStoreFor(Network.Chain);
        store.SaveContacts(store.Contacts().Where(c => c.Fingerprint != fingerprint).ToList());
    }

    /// <summary>
    /// The account's newest shielded address to hand out (the wallet and its first address
    /// are made if need be), as the JSON another wallet pastes, with its fingerprint.
    /// </summary>
    public async Task<ShieldedReceiveAddress> ShieldedReceiveAddressForSelected()
    {
        _ = CurrentShieldedConfig;
        if (SelectedWallet?.Address is not { } account) throw new WalletError.EmptyAlias();
        ShieldProgress = new ShieldProgress.Preparing();
        try
        {
            var wallet = await EnsureShieldedWallet(account);
            var address = wallet.Addresses.MaxBy(a => a.Index) ?? throw new ShieldedError.TreeMissing(0);
            return await ReceiveAddress(address);
        }
        finally { ShieldProgress = null; }
    }

    /// <summary>
    /// A fresh shielded address of the account (its signing tree built, seconds), so that
    /// what is handed to one payer links to nothing handed to another.
    /// </summary>
    public async Task<ShieldedReceiveAddress> MakeShieldedAddress()
    {
        _ = CurrentShieldedConfig;
        if (SelectedWallet?.Address is not { } account) throw new WalletError.EmptyAlias();
        var store = ShieldedStoreFor(Network.Chain);
        ShieldProgress = new ShieldProgress.Preparing();
        try
        {
            var wallet = await EnsureShieldedWallet(account);
            var index = (wallet.Addresses.Select(a => (int?)a.Index).Max() ?? -1) + 1;
            var made = await MakeAddress(index, wallet, account);
            // Onto the wallet as it is on file now; the same index made meanwhile is the same address.
            var saved = store.Update(account, null, latest => { if (latest.AddressAt(made.Index) is null) latest.Addresses.Add(ShieldedJSON.Clone(made)); })
                ?? throw new ShieldedError.TreeMissing(index);
            ShieldedWallet = saved;
            await BackUp();
            return await ReceiveAddress(saved.AddressAt(index) ?? made);
        }
        finally { ShieldProgress = null; }
    }

    private async Task<ShieldedReceiveAddress> ReceiveAddress(ShieldedAddress address)
    {
        var parsed = await shieldedBridge.Call("tzelPaymentAddress", JsonSerializer.Serialize(address.PaymentAddressJSON));
        if (parsed["fingerprint"]?.StringValue is not { } fingerprint) throw new ShieldedError.BadReply("payment address");
        var json = JsonSerializer.Serialize(address.PaymentAddressJSON, new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n");
        return new ShieldedReceiveAddress(address.Index, json, fingerprint);
    }

    // ---- Send shielded ---------------------------------------------------------------------------

    /// <summary>What sending <c>amount</c> tez to <c>recipient</c> with <c>memo</c> would spend. Nothing is sent.</summary>
    public async Task<SendShieldedEstimate> EstimateSendShielded(decimal amount, ShieldedContact recipient, string memo)
    {
        var config = CurrentShieldedConfig;
        if (SelectedWallet is not { } wallet || BridgeContext is not { } context) throw new WalletError.EmptyAlias();
        if (amount <= 0 || !Mutez.IsValid(Mutez.FromTez(amount)) || !ulong.TryParse(Mutez.FromTez(amount), NumberStyles.None, CultureInfo.InvariantCulture, out var amountMutez) || amountMutez == 0)
            throw new SendViewModel.SendError.InvalidAmount();
        if (System.Text.Encoding.UTF8.GetByteCount(memo) > 1024) throw new ShieldedError.MemoTooLong();
        var shielded = ShieldedStoreFor(Network.Chain).Load(wallet.Address);
        var rollup = shieldedRollupFactory(config);
        var fee = await rollup.RequiredTxFee(await rollup.Head());
        ulong total;
        try { total = checked(amountMutez + fee + config.ProducerFeeMutez); }
        catch (OverflowException) { throw new SendViewModel.SendError.InvalidAmount(); }
        var inputs = SelectNotes(shielded?.AvailableNotes ?? [], total);
        if (BridgeContext != context) throw new WalletError.ContextChanged();
        return new SendShieldedEstimate(context, amount, amountMutez, recipient, memo, fee, config.ProducerFeeMutez, inputs);
    }

    /// <summary>
    /// Sends <c>approved.Amount</c> tez to <c>approved.Recipient</c> as a shielded transfer: the same
    /// preparation as an unshield (a change address is always made, since the kernel takes
    /// exactly three output notes), then <c>tzelBuildTransfer</c>, <c>run_transfer</c>, the operator;
    /// the transfer is on file as soon as the operator has it. The recipient sees the note on
    /// their next scan; nothing identifies them on the rollup but their own viewing keys.
    /// </summary>
    public async Task<SendShieldedReceipt> SendShielded(SendShieldedEstimate approved)
    {
        var config = CurrentShieldedConfig;
        if (SelectedWallet?.Address is not { } account) throw new WalletError.EmptyAlias();
        if (approved.Context != BridgeContext || !CanShield) throw new WalletError.ContextChanged();
        var context = BridgeContext;
        var store = ShieldedStoreFor(Network.Chain);
        if (store.OperatorToken() is not { Length: > 0 } token) throw new ShieldedError.OperatorTokenMissing();
        var rollup = shieldedRollupFactory(config);
        ShieldProgress = new ShieldProgress.ReadingTree();
        try
        {
            var spend = await PrepareSpend(approved.Inputs, approved.TotalMutez, approved.RollupFeeMutez, true, account, store, rollup, context);
            var changeAddress = spend.ChangeAddress ?? throw new ShieldedError.TreeMissing(spend.Wallet.Addresses.Count);
            var request = new JsonObject
            {
                ["account"] = JsonNode.Parse(spend.Seeds.JsonText), ["authDomain"] = spend.AuthDomain, ["root"] = spend.Root, ["leafCms"] = Array(spend.Wallet.LeafCms),
                ["amount"] = approved.AmountMutez.ToString(CultureInfo.InvariantCulture), ["fee"] = spend.Fee.ToString(CultureInfo.InvariantCulture),
                ["producerFee"] = config.ProducerFeeMutez.ToString(CultureInfo.InvariantCulture),
                ["recipient"] = JsonSerializer.SerializeToNode(approved.Recipient.Address), ["memo"] = approved.Memo,
                ["inputs"] = new JsonArray(spend.InputRecords.Select(r => (JsonNode?)r).ToArray()),
                ["changeAddress"] = changeAddress.BridgeRecord, ["producerAddress"] = JsonSerializer.SerializeToNode(config.ProducerAddressJSON()),
            };
            var built = await shieldedBridge.Call("tzelBuildTransfer", request.ToJsonString());
            var n = spend.Inputs.Count;
            if (Strings(built["witness"]) is not { } witness || witness.Count != 1 + 4 + n * 206 + 24 ||
                Strings(built["nullifiers"]) is not { } nullifiers || !nullifiers.SequenceEqual(spend.Inputs.Select(i => i.Nullifier)) ||
                Strings(built["cms"]) is not { Count: 3 } cms || Strings(built["encs"]) is not { Count: 3 } encs ||
                Strings(built["publicOutputs"]) is not { } outputs || outputs.Count != 2 + n + 7 ||
                built["changeMutez"]?.StringValue != spend.ChangeMutez.ToString(CultureInfo.InvariantCulture) ||
                built["recipientFingerprint"]?.StringValue != approved.Recipient.Fingerprint)
                throw new ShieldedError.BadReply("transfer witness");

            ShieldProgress = new ShieldProgress.ProvingTransfer();
            var bundle = await rollup.Prove("run_transfer", witness);
            var message = new JsonObject
            {
                ["root"] = spend.Root, ["nullifiers"] = Array(nullifiers), ["fee"] = spend.Fee.ToString(CultureInfo.InvariantCulture), ["cms"] = Array(cms), ["encs"] = Array(encs),
                ["proofBytes"] = bundle.ProofBytes, ["outputPreimage"] = Array(bundle.OutputPreimage), ["expectedOutputs"] = Array(outputs), ["programHash"] = bundle.ProgramHash,
            };
            var encoded = await shieldedBridge.Call("tzelEncodeTransferMessage", message.ToJsonString());
            if (encoded.StringValue is not { } payload || HexBytes.Decode(payload) is null)
                throw new ShieldedError.BadReply($"transfer message: {Truncate(encoded.JsonText, 200)}");

            ShieldProgress = new ShieldProgress.SubmittingTransfer();
            var localID = ShieldedWithdrawal.LocalID();
            var transfer = new ShieldedTransfer
            {
                SubmissionID = localID, RecipientFingerprint = approved.Recipient.Fingerprint,
                RecipientName = approved.Recipient.Name.Length == 0 ? null : approved.Recipient.Name,
                AmountMutez = approved.AmountMutez.ToString(CultureInfo.InvariantCulture), RollupFeeMutez = spend.Fee.ToString(CultureInfo.InvariantCulture),
                ProducerFeeMutez = config.ProducerFeeMutez.ToString(CultureInfo.InvariantCulture), Memo = approved.Memo, Nullifiers = nullifiers,
                RecipientCm = cms[0], ChangeCm = cms[1], Created = DateTimeOffset.UtcNow, PayloadHex = payload, Submitted = false,
            };
            UpdateTransfers(account, store, list => list.Add(ShieldedJSON.Clone(transfer)));
            await BackUp();
            var submission = await SubmitShielded(localID, () => UpdateTransfers(account, store, list => list.RemoveAll(t => t.Id == localID)),
                () => rollup.Submit("transfer", payload, config.RollupAddress, token));
            transfer.SubmissionID = submission.Id;
            transfer.Submitted = true;
            transfer.OperationHash = submission.OperationHash;
            var acknowledged = ShieldedJSON.Clone(transfer);
            UpdateTransfers(account, store, list => { var at = list.FindIndex(t => t.Id == localID); if (at >= 0) list[at] = ShieldedJSON.Clone(acknowledged); });
            try
            {
                var delivered = await rollup.AwaitDelivery(submission, token);
                transfer.OperationHash = delivered.OperationHash;
                UpdateTransfers(account, store, list => { if (list.FirstOrDefault(t => t.Id == transfer.Id) is { } t) t.OperationHash = transfer.OperationHash; });
            }
            catch (ShieldedError.SubmissionFailed)
            {
                try { UpdateTransfers(account, store, list => list.RemoveAll(t => t.Id == submission.Id)); } catch (Exception) { }
                throw;
            }
            catch (Exception)
            {
                // Still with the operator, or its answer could not be read: the record and its
                // reservation stay (see the unshield).
            }
            await RefreshAfterOperation();
            return new SendShieldedReceipt(transfer);
        }
        finally { ShieldProgress = null; }
    }

    /// <summary>
    /// Hands an unacknowledged unshield (a <c>local:</c> record whose first submission got no
    /// answer) to the operator again, with the exact payload kept from the first attempt. The
    /// same message cannot spend the notes twice: the rollup refuses a repeated nullifier, so
    /// at worst the operator reports the duplicate as failed and the scan settles it.
    /// </summary>
    public async Task ResubmitWithdrawal(string id)
    {
        var config = CurrentShieldedConfig;
        if (SelectedWallet?.Address is not { } account) throw new WalletError.EmptyAlias();
        var store = ShieldedStoreFor(Network.Chain);
        if (store.Load(account)?.Withdrawals.FirstOrDefault(w => w.Id == id) is not { } record) throw new ShieldedError.WithdrawalUnknown(id);
        if (record.Submitted || record.PayloadHex is not { } payload) throw new ShieldedError.WithdrawalNotReady("the operator has acknowledged it already");
        if (store.OperatorToken() is not { Length: > 0 } token) throw new ShieldedError.OperatorTokenMissing();
        var rollup = shieldedRollupFactory(config);
        ShieldProgress = new ShieldProgress.SubmittingUnshield();
        try
        {
            var submission = await SubmitShielded(id, () => rollup.Submit("unshield", payload, config.RollupAddress, token));
            UpdateWithdrawals(account, store, list =>
            {
                if (list.FirstOrDefault(w => w.Id == id) is not { } w) return;
                w.SubmissionID = submission.Id;
                w.Submitted = true;
                w.OperationHash = submission.OperationHash;
            });
        }
        finally { ShieldProgress = null; }
        await RefreshAfterOperation();
    }

    /// <summary><c>ResubmitWithdrawal</c> for a shielded transfer.</summary>
    public async Task ResubmitTransfer(string id)
    {
        var config = CurrentShieldedConfig;
        if (SelectedWallet?.Address is not { } account) throw new WalletError.EmptyAlias();
        var store = ShieldedStoreFor(Network.Chain);
        if (store.Load(account)?.Transfers.FirstOrDefault(t => t.Id == id) is not { } record) throw new ShieldedError.WithdrawalUnknown(id);
        if (record.Submitted || record.PayloadHex is not { } payload) throw new ShieldedError.WithdrawalNotReady("the operator has acknowledged it already");
        if (store.OperatorToken() is not { Length: > 0 } token) throw new ShieldedError.OperatorTokenMissing();
        var rollup = shieldedRollupFactory(config);
        ShieldProgress = new ShieldProgress.SubmittingTransfer();
        try
        {
            var submission = await SubmitShielded(id, () => rollup.Submit("transfer", payload, config.RollupAddress, token));
            UpdateTransfers(account, store, list =>
            {
                if (list.FirstOrDefault(t => t.Id == id) is not { } t) return;
                t.SubmissionID = submission.Id;
                t.Submitted = true;
                t.OperationHash = submission.OperationHash;
            });
        }
        finally { ShieldProgress = null; }
        await RefreshAfterOperation();
    }

    /// <summary>Drops a transfer the rollup has not applied, freeing its notes (see <c>ForgetWithdrawal</c>).</summary>
    public void ForgetTransfer(string id)
    {
        if (SelectedWallet?.Address is not { } account) throw new WalletError.EmptyAlias();
        var store = ShieldedStoreFor(Network.Chain);
        if (store.Load(account)?.Transfers.FirstOrDefault(t => t.Id == id) is not { } transfer) throw new ShieldedError.WithdrawalUnknown(id);
        if (transfer.Applied) throw new ShieldedError.WithdrawalNotReady("the rollup has applied it");
        UpdateTransfers(account, store, list => list.RemoveAll(t => t.Id == id));
        ShieldedTransfers = ShieldedTransfers.Where(t => t.Id != id).ToList();
    }

    /// <summary>
    /// Drops a withdrawal the rollup has not applied, freeing its notes. For one the operator
    /// never delivered; if it is delivered after all, the scan marks the notes spent anyway.
    /// </summary>
    public void ForgetWithdrawal(string id)
    {
        if (SelectedWallet?.Address is not { } account) throw new WalletError.EmptyAlias();
        var store = ShieldedStoreFor(Network.Chain);
        if (store.Load(account)?.Withdrawals.FirstOrDefault(w => w.Id == id) is not { } withdrawal) throw new ShieldedError.WithdrawalUnknown(id);
        if (withdrawal.Applied) throw new ShieldedError.WithdrawalNotReady("the rollup has applied it");
        UpdateWithdrawals(account, store, list => list.RemoveAll(w => w.Id == id));
        ShieldedWithdrawals = ShieldedWithdrawals.Where(w => w.Id != id).ToList();
    }

    /// <summary>
    /// The layer 1 fee for executing withdrawal <c>id</c>'s outbox message, with the rollup node's
    /// proof against the last cemented commitment. Refused until that commitment covers it.
    /// </summary>
    public async Task<WithdrawalExecutionEstimate> EstimateWithdrawalExecution(string id)
    {
        var config = CurrentShieldedConfig;
        if (SelectedWallet is not { } wallet || BridgeContext is not { } context) throw new WalletError.EmptyAlias();
        if (ShieldedStoreFor(Network.Chain).Load(wallet.Address)?.Withdrawals.FirstOrDefault(w => w.Id == id) is not { } withdrawal) throw new ShieldedError.WithdrawalUnknown(id);
        if (withdrawal.ExecutedHash is not null) throw new ShieldedError.WithdrawalNotReady("it has been executed already");
        if (!withdrawal.Applied || withdrawal.OutboxLevel is not { } level || withdrawal.MessageIndex is not { } index)
            throw new ShieldedError.WithdrawalNotReady("the rollup has not queued it yet");
        var rollup = shieldedRollupFactory(config);
        if (await rollup.CementedLevel() < level)
            throw new ShieldedError.WithdrawalNotReady("the rollup commitment covering it is not cemented yet (about 14 days after the unshield)");
        var proof = await rollup.OutboxProofFor(level, index);
        var execution = new OutboxExecution(config.RollupAddress, proof.Commitment, proof.Proof);
        var estimate = await chain.EstimateOutboxExecution(wallet, execution);
        if (BridgeContext != context) throw new WalletError.ContextChanged();
        return new WithdrawalExecutionEstimate(estimate, context, withdrawal, execution);
    }

    /// <summary>
    /// Executes the outbox message with the approved limits (S14) from the selected account,
    /// records the operation against the withdrawal and waits for its block.
    /// </summary>
    public async Task<string> ExecuteWithdrawal(WithdrawalExecutionEstimate approved, string? passphrase)
    {
        if (SelectedWallet is not { } wallet) throw new WalletError.EmptyAlias();
        if (approved.Estimate.Limits is not { } limits) throw new WalletError.NotEstimated();
        if (approved.Context != BridgeContext || !CanShield) throw new WalletError.ContextChanged();
        var signer = SigningKeyFor(wallet, passphrase) ?? throw new SendViewModel.SendError.NoSecretKey();
        var store = ShieldedStoreFor(Network.Chain);
        ShieldProgress = new ShieldProgress.Executing();
        try
        {
            var chain = this.chain;
            var sent = await chain.SendOutboxExecution(wallet, signer, approved.Execution, limits);
            UpdateWithdrawals(wallet.Address, store, list => { if (list.FirstOrDefault(w => w.Id == approved.Withdrawal.Id) is { } w) w.ExecutedHash = sent.Hash; });
            ShieldProgress = new ShieldProgress.WaitingForLayer1();
            try { await chain.WaitForConfirmation(sent.Hash); }
            catch (Exception) when (!sent.Injected) { throw new WalletError.OutcomeUnknown(sent.Hash); }
            await RefreshAfterOperation();
            return sent.Hash;
        }
        finally { ShieldProgress = null; }
    }
}
