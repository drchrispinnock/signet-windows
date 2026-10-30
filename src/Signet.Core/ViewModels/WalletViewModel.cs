using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Signet.Models;
using Signet.Services;

namespace Signet.ViewModels;

/// <summary>
/// Drives the default screen. Owns the list of wallets, the selected one and everything loaded for it.
/// Touched on the UI thread only: awaits resume there, so property changes reach bindings where they expect them.
///
/// Not ported yet: shielded transactions (<c>WalletViewModel+Shielded.swift</c>), Etherlink (the
/// Layer 2 monitor and chain, Bridge, alias linking), multisig, dApps (<c>DAppConnectionManager</c>),
/// Ledger (<c>ConnectLedger</c>), Buy and the per-network Layer 2 and shielded settings.
/// </summary>
public sealed partial class WalletViewModel : ObservableObject
{
    public abstract class WalletError(string message) : Exception(message), IEquatable<WalletError>
    {
        public sealed class EmptyAlias() : WalletError("Give the account a name.");
        public sealed class AliasExists(string alias) : WalletError($"An account named “{alias}” already exists.");
        public sealed class AddressExists(string alias) : WalletError($"That address is already in the list as “{alias}”.");
        public sealed class InvalidAddress() : WalletError("That is not a valid Tezos address.");
        public sealed class UnsupportedScheme(AddressScheme scheme)
            : WalletError(scheme.UnavailableReason() ?? $"{scheme.RawValue()} is not supported yet.");
        public sealed class PassphraseTooShort() : WalletError($"Use a password of at least {MinimumPassphraseLength} characters.");
        public sealed class DirectoryChanged() : WalletError("The wallet directory changed while this was being confirmed. Nothing was done; please try again.");
        public sealed class NotEstimated() : WalletError("The fee has not been estimated yet; wait for the estimate and try again.");
        public sealed class ContextChanged()
            : WalletError("The account, network or node changed since the fee was estimated. Nothing was sent; the estimate will be made again.");
        public sealed class OutcomeUnknown(string hash)
            : WalletError($"The node did not confirm receiving operation {hash[..Math.Min(12, hash.Length)]}… and it has not appeared in a block yet. Check the explorer before trying again; do not repeat it blindly.");

        public bool Equals(WalletError? other) => other is not null && other.GetType() == GetType() && other.Message == Message;
        public override bool Equals(object? obj) => Equals(obj as WalletError);
        public override int GetHashCode() => HashCode.Combine(GetType(), Message);
    }

    /// <summary>
    /// Every account: those from the wallet directory (accounts, address book, contracts), then
    /// the keys held for this session only. Assigning replaces the stored part.
    /// </summary>
    public IReadOnlyList<Wallet> Wallets
    {
        get => [.. storedWallets, .. SessionWallets];
        private set
        {
            storedWallets = value.Where(w => !w.IsSessionOnly).ToList();
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedWallet));
            OnPropertyChanged(nameof(MultisigContracts));
        }
    }
    private IReadOnlyList<Wallet> storedWallets;

    /// <summary>Paper keys imported for this run only: in memory, never in the directory or a backup.</summary>
    public IReadOnlyList<Wallet> SessionWallets
    {
        get => sessionWallets;
        private set
        {
            sessionWallets = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Wallets));
            OnPropertyChanged(nameof(SelectedWallet));
            OnPropertyChanged(nameof(CanRememberSelectedKey));
        }
    }
    private IReadOnlyList<Wallet> sessionWallets = [];
    private readonly SessionKeyStore sessionKeys = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedWallet), nameof(CanExportSelectedKey), nameof(CanRememberSelectedKey), nameof(CanGovern))]
    private string? selectedWalletID;

    [ObservableProperty] private bool isPresentingCreateWallet;
    [ObservableProperty] private bool isPresentingRenameWallet;
    [ObservableProperty] private bool isPresentingReceive;
    [ObservableProperty] private bool isPresentingAddAddress;
    [ObservableProperty] private bool isPresentingSend;
    [ObservableProperty] private bool isPresentingFaucet;
    [ObservableProperty] private bool isPresentingStaking;
    [ObservableProperty] private bool isPresentingBaking;
    [ObservableProperty] private bool isPresentingConnectLedger;
    [ObservableProperty] private bool isPresentingImportAccount;
    [ObservableProperty] private bool isPresentingExportKey;
    [ObservableProperty] private bool isPresentingForget;
    [ObservableProperty] private bool isPresentingRememberKey;
    [ObservableProperty] private bool isPresentingGovernance;
    [ObservableProperty] private bool isPresentingNetworks;
    [ObservableProperty] private bool isPresentingBuy;
    [ObservableProperty] private bool isPresentingConnectDApp;
    [ObservableProperty] private bool isPresentingCreateMultisig;
    [ObservableProperty] private bool isPresentingAddMultisig;
    [ObservableProperty] private bool isPresentingSignMultisig;
    /// <summary>What the Sign multisig sheet opens on when something else (the delegate row) asks for it.</summary>
    [ObservableProperty] private MultisigSheetPreset? signMultisigPreset;
    [ObservableProperty] private bool isPresentingSubmitMultisig;

    /// <summary>
    /// Named multisig contracts: the KT1 entries of the address book (octez-client's <c>contracts</c>),
    /// which is where created and added multisigs go.
    /// </summary>
    public IReadOnlyList<MultisigContract> MultisigContracts =>
        Wallets.Where(w => w.Address.IsContract).Select(w => new MultisigContract(w.Alias, w.Address)).ToList();

    /// <summary>Multisig actions proposed here, with the signatures gathered so far (all networks).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CurrentMultisigProposals))]
    private IReadOnlyList<MultisigProposal> multisigProposals = [];

    /// <summary>A pairing code that arrived by URL, waiting for the Connect dApp sheet to pick it up.</summary>
    [ObservableProperty] private string? pendingPairingCode;

    /// <summary>
    /// Handles a <c>signet://</c> link. Only TZIP-10 pairing links are understood; the code is handed
    /// to the Connect dApp sheet, which pairs and reports as if it had been pasted.
    /// </summary>
    public void HandleIncomingURL(Uri url)
    {
        if (DAppPairingLink.Code(url) is not { } code)
        {
            ErrorMessage = "Signet does not understand that link.";
            return;
        }
        PendingPairingCode = code;
        IsPresentingConnectDApp = true;
    }

    /// <summary>dApp connections over Octez Connect (TZIP-10).</summary>
    [ObservableProperty] private DAppConnectionManager dapps;

    /// <summary>Export shows keys Signet holds on disk; Ledger and watch-only entries have nothing to show.</summary>
    public bool CanExportSelectedKey => SelectedWallet?.KeyKind is KeyKind.Unencrypted or KeyKind.Encrypted;

    /// <summary>Governance is for bakers whose key we can sign with.</summary>
    public bool CanGovern => SelectedWallet?.KeyKind.CanSign() == true && DelegateInfo?.IsBaker == true;

    [ObservableProperty] private IReadOnlyList<string> domains = [];
    [ObservableProperty] private IReadOnlyList<AssetBalance> assets = [];
    /// <summary>Fungible (DeFi) tokens, shown on the Assets tab rather than in the balance list.</summary>
    [ObservableProperty] private IReadOnlyList<AssetBalance> tokens = [];
    [ObservableProperty] private TezBalance? tezBalance;
    [ObservableProperty] private IReadOnlyList<NFT> nfts = [];
    /// <summary>Whether <c>Tokens</c> and <c>Nfts</c> are the whole story (S26).</summary>
    [ObservableProperty] private Portfolio.PortfolioState portfolioState = new Portfolio.PortfolioState.Complete();
    [ObservableProperty] private IReadOnlyList<TezosTransaction> transactions = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanGovern))] private DelegateInfo? delegateInfo;
    public const int RecentTransactionLimit = 25;

    public enum ActivityTab { Transactions, Assets, Nfts }
    [ObservableProperty] private ActivityTab activityTabSelection = ActivityTab.Transactions;
    [ObservableProperty] private bool isLoading;
    /// <summary>True when the node has no record of the selected address (never funded on this network).</summary>
    [ObservableProperty] private bool accountNotOnChain;

    public abstract record FaucetStatus
    {
        public sealed record Idle : FaucetStatus;
        public sealed record Solving(int Done, int Total) : FaucetStatus;
        public sealed record Sent(string Hash) : FaucetStatus;
        public sealed record Received(string Hash) : FaucetStatus;
        public sealed record Failed(string Message) : FaucetStatus;
    }
    [ObservableProperty] private FaucetStatus faucet = new FaucetStatus.Idle();
    /// <summary>The faucet request in flight, so switching accounts (or the user) can cancel the proof of work.</summary>
    private CancellationTokenSource? faucetTask;
    public const double FaucetAmountTez = 100;
    [ObservableProperty] private bool isCreatingWallet;
    [ObservableProperty] private string? errorMessage;

    /// <summary>Watches the configured node for the status bar.</summary>
    public NodeMonitor NodeMonitor { get; }

    /// <summary>
    /// The network and node the app talks to. Changing either swaps the chain service and
    /// reloads the dashboard. Assign a <c>Network</c> straight from <c>Network.All</c> to use its default
    /// node; <c>SwitchNetwork</c> applies the custom node saved for it, if any.
    /// </summary>
    public Network Network
    {
        get => network;
        set
        {
            if (value == network) return;
            network = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CurrentMultisigProposals));
            NodeMonitor.Network = value;
            chain = chainFactory(value);
            state = state.Remember(value) with { NetworkName = value.Name };
            PersistState();
            ClearChainData();
            _ = Refresh();
        }
    }
    private Network network;

    /// <summary>Everything on the dashboard that came from the chain for the previous context (S16).</summary>
    private void ClearChainData()
    {
        operationGeneration = Guid.NewGuid();
        refreshGeneration++;
        IsLoading = false;
        Assets = [];
        Tokens = [];
        Domains = [];
        Nfts = [];
        Transactions = [];
        DelegateInfo = null;
        TezBalance = null;
        AccountNotOnChain = false;
        PortfolioState = new Portfolio.PortfolioState.Complete();
        Faucet = new FaucetStatus.Idle();
    }

    /// <summary>
    /// Which load is current. Every refresh captures the generation, the account, the network and
    /// the directory it started under and publishes nothing unless all four still hold (S16).
    /// </summary>
    private Guid operationGeneration = Guid.NewGuid();
    private int refreshGeneration;
    private sealed record RefreshContext(int Generation, Address Address, Network Network, Guid Revision);
    private RefreshContext? CurrentContext =>
        SelectedWallet is { } wallet ? new RefreshContext(refreshGeneration, wallet.Address, Network, walletDirectoryRevision) : null;

    /// <summary><c>base</c> with the node and links the user saved for it (<c>AppState.Resolved</c>).</summary>
    public Network Resolved(Network @base) => state.Resolved(@base);

    /// <summary>The link in use (saved or default) for <c>service</c> on <c>base</c>, without switching to it.</summary>
    public Uri? ServiceURL(Network.Service service, Network @base) => Resolved(Network.Named(@base.Name) ?? @base).UrlFor(service);

    /// <summary>
    /// Points <c>service</c> on <c>base</c> at what the user typed, or turns it off for empty text.
    /// False (and no change) when the text is not a URL, or for a faucet on mainnet.
    /// </summary>
    public bool SetServiceURL(string text, Network.Service service, Network @base)
    {
        if (!@base.Offers(service)) return false;
        var trimmed = text.Trim();
        Uri? url = null;
        if (trimmed.Length > 0)
        {
            if (Network.NodeURL(trimmed) is not { } parsed) return false;
            url = parsed;
        }
        Change(@base, n => n.Using(url, service));
        return true;
    }

    /// <summary>Goes back to the link we ship for <c>service</c> on <c>base</c>.</summary>
    public void UseDefaultServiceURL(Network.Service service, Network @base) => Change(@base, n => n.Using(n.DefaultURLFor(service), service));

    /// <summary>
    /// Applies a per-network change: through <c>Network</c> (which persists and reloads) when
    /// <c>base</c> is in use, else straight into the saved state.
    /// </summary>
    private void Change(Network @base, Func<Network, Network> change)
    {
        if (@base.Name == Network.Name)
        {
            Network = change(Network);
        }
        else
        {
            var changed = change(Resolved(Network.Named(@base.Name) ?? @base));
            state = state.RememberServices(changed);
            PersistState();
        }
    }

    /// <summary>Switches to another network, talking to whatever node was last set for it.</summary>
    public void SwitchNetwork(Network @base) => Network = Resolved(@base);

    /// <summary>Points the current network at the node typed in Networks. False (and no change) when the text is not a host or http(s) URL.</summary>
    public bool SetNode(string text) => SetNode(text, Network);

    /// <summary>Goes back to the node we ship for the current network.</summary>
    public void UseDefaultNode() => UseDefaultNode(Network);

    /// <summary>How the node monitor reaches a node; injectable for tests.</summary>
    private readonly NodeMonitor.Probe? nodeProbe;

    /// <summary>
    /// Asks the node typed in Networks which chain it serves before it is adopted, so a node for
    /// the wrong network is warned about as it is entered, not only by the red status bar
    /// afterwards. Networks that pin no chain id (Weeklynet, Quantumnet, Custom) are <c>Unpinned</c>.
    /// Nothing is changed by asking. (Upstream's <c>layer:</c> argument waits for Etherlink.)
    /// </summary>
    public async Task<NodeIdentity> CheckNode(string text, Network @base)
    {
        if (Network.NodeURL(text) is not { } url) return new NodeIdentity.Unchecked(Network.NodeURLProblem(text));
        var template = Network.Named(@base.Name) ?? @base;
        return await NodeMonitor.Identity(url, template.ExpectedChainID, nodeProbe ?? NodeMonitor.HttpProbe);
    }

    /// <summary>The node in use (saved or default) for any network, without switching to it.</summary>
    public Uri NodeURL(Network @base) => Resolved(Network.Named(@base.Name) ?? @base).RpcURL;

    /// <summary>Saves the node for <c>base</c>. Applies immediately if <c>base</c> is the network in use.</summary>
    public bool SetNode(string text, Network @base)
    {
        if (Network.NodeURL(text) is not { } url) return false;
        if (@base.Name == Network.Name)
        {
            Network = Network.UsingNode(url);
        }
        else
        {
            var template = Network.Named(@base.Name) ?? @base;
            state = state.RememberNode(template.UsingNode(url));
            PersistState();
        }
        return true;
    }

    /// <summary>Goes back to the shipped node for <c>base</c>.</summary>
    public void UseDefaultNode(Network @base)
    {
        var template = Network.Named(@base.Name) ?? @base;
        if (@base.Name == Network.Name)
        {
            Network = Network.UsingNode(template.DefaultRPCURL);
        }
        else
        {
            state = state.RememberNode(template);
            PersistState();
        }
    }

    private IChainService chain;
    private readonly Func<Network, IChainService> chainFactory;

    /// <summary>The directory the wallet files live in, when the stores are file-backed.</summary>
    [ObservableProperty] private string? walletDirectory;
    private Guid walletDirectoryRevision = Guid.NewGuid();
    private readonly WalletDirectorySettings? directorySettings;
    private readonly BackupSettings? backupSettings;

    /// <summary>Result of the most recent backup attempt, for Settings.</summary>
    [ObservableProperty] private (DateTimeOffset Date, WalletBackup.Outcome Outcome)? lastBackup;
    [ObservableProperty] private string? lastBackupError;
    public string? BackupDirectory => backupSettings?.Directory;
    public int BackupGenerations => backupSettings?.Generations ?? BackupSettings.DefaultGenerations;
    public bool IsUsingDefaultBackupDirectory =>
        backupSettings is null || PathUtility.SamePath(backupSettings.Directory, backupSettings.DefaultDirectory);
    public string? DefaultBackupDirectory => backupSettings?.DefaultDirectory;
    private readonly Func<string, (IWalletStore Wallets, IAppStateStore State)>? storeFactory;
    private readonly KeyGenerator keyGenerator;
    /// <summary>Pasted secret keys and recovery phrases.</summary>
    public IKeyImporter KeyImporter { get; }
    private IWalletStore walletStore;
    private IAppStateStore stateStore;
    private AppState state;
    /// <summary>An octez-client directory to offer for import, or <c>null</c> to never offer.</summary>
    private readonly string? importSource;

    /// <summary>Number of octez-client aliases available to import, or 0 when there is nothing to offer.</summary>
    [ObservableProperty] private int importableWalletCount;

    /// <param name="chain">A fixed chain service (tests and previews). Ignored when <c>chainFactory</c> is given.</param>
    /// <param name="chainFactory">Builds the chain service for a network; the app passes <c>TaquitoChainService</c>.</param>
    public WalletViewModel(
        IReadOnlyList<Wallet>? wallets = null,
        IChainService? chain = null,
        Func<Network, IChainService>? chainFactory = null,
        KeyGenerator? keyGenerator = null,
        IKeyImporter? keyImporter = null,
        IWalletStore? walletStore = null,
        string? importSource = null,
        IAppStateStore? stateStore = null,
        WalletDirectorySettings? directorySettings = null,
        Func<string, (IWalletStore Wallets, IAppStateStore State)>? storeFactory = null,
        BackupSettings? backupSettings = null,
        NodeMonitor.Probe? nodeProbe = null,
        IBridgeStorage? dappStorage = null,
        Func<string, IBridgeStorage>? dappStorageFactory = null,
        bool startDApps = false,
        IMultisigService? multisig = null,
        IMultisigProposalStore? proposalStore = null)
    {
        this.backupSettings = backupSettings;
        var factory = chainFactory ?? (_ => chain ?? new MockChainService());
        // File-backed mode: build both stores for the configured directory.
        walletStore ??= new InMemoryWalletStore();
        stateStore ??= new InMemoryAppStateStore();
        string? directory = null;
        if (directorySettings is not null && storeFactory is not null)
        {
            directory = directorySettings.Current;
            (walletStore, stateStore) = storeFactory(directory);
        }
        this.walletDirectory = directory;
        this.directorySettings = directorySettings;
        this.storeFactory = storeFactory;
        state = stateStore.Load();
        this.stateStore = stateStore;
        network = state.Resolved(Network.Named(state.NetworkName) ?? Network.Mainnet);
        this.nodeProbe = nodeProbe;
        NodeMonitor = new NodeMonitor(network, nodeProbe);
        this.chainFactory = factory;
        this.chain = factory(network);
        this.keyGenerator = keyGenerator ?? new KeyGenerator();
        KeyImporter = keyImporter ?? new MockKeyImporter();
        this.walletStore = walletStore;
        this.importSource = importSource;
        multisigService = multisig ?? new MockMultisigService();
        this.proposalStore = proposalStore ?? (directory is not null ? new FileMultisigProposalStore(directory) : new InMemoryMultisigProposalStore());
        multisigProposals = this.proposalStore.Load();

        string? loadError = null;
        IReadOnlyList<Wallet> loaded;
        if (wallets is not null)
        {
            loaded = wallets;
        }
        else
        {
            try { loaded = walletStore.Load(); }
            catch (Exception e)
            {
                loaded = [];
                loadError = $"Could not load accounts: {e.Message}";
            }
        }
        storedWallets = loaded;
        // Restore the wallet that was showing last time, if it still exists.
        var remembered = state.SelectedWalletAlias;
        selectedWalletID = loaded.FirstOrDefault(w => w.Alias == remembered)?.Id ?? loaded.FirstOrDefault()?.Id;
        errorMessage = loadError;
        importableWalletCount = CountImportable(importSource, loaded);

        // Startup backup of the key files.
        _ = BackUp();

        this.dappStorageFactory = dappStorageFactory;
        startsDApps = startDApps;
        dapps = MakeDApps(dappStorage ?? (directory is not null ? dappStorageFactory?.Invoke(directory) : null) ?? new InMemoryBridgeStorage());
        if (startDApps && Wallets.Count > 0) _ = dapps.Start();

        // A new head means balances or history may have moved; refresh (not while one is running).
        NodeMonitor.OnNewHead = level =>
        {
            if (IsLoading || SelectedWallet is null) return;
            _ = Refresh();
        };
    }

    /// <summary>
    /// Copies the wallet files to the backup directory, keeping the configured number of generations.
    /// Runs off the UI thread; results land in <c>LastBackup</c> / <c>LastBackupError</c>.
    /// </summary>
    public async Task BackUp(bool force = false)
    {
        if (backupSettings is null || WalletDirectory is not { } walletDirectory) return;
        var job = new WalletBackup(walletDirectory, backupSettings.Directory, backupSettings.Generations);
        try
        {
            var outcome = await Task.Run(() => job.Run(force: force));
            LastBackup = (DateTimeOffset.Now, outcome);
            LastBackupError = null;
        }
        catch (Exception e)
        {
            LastBackupError = e.Message;
            Trace.WriteLine($"Signet: backup failed: {e.Message}");
        }
    }

    public void SetBackupDirectory(string path)
    {
        backupSettings?.SetDirectory(path);
        OnPropertyChanged(nameof(BackupDirectory));
        OnPropertyChanged(nameof(IsUsingDefaultBackupDirectory));
        _ = BackUp();
    }

    public void SetBackupGenerations(int count)
    {
        backupSettings?.SetGenerations(count);
        OnPropertyChanged(nameof(BackupGenerations));
        _ = BackUp();
    }

    public Wallet? SelectedWallet => Wallets.FirstOrDefault(w => w.Id == SelectedWalletID) ?? Wallets.FirstOrDefault();

    /// <summary>
    /// Builds the Octez Connect storage for a wallet directory: each directory is its own dApp
    /// session (S08).
    /// </summary>
    private readonly Func<string, IBridgeStorage>? dappStorageFactory;
    private readonly bool startsDApps;

    private DAppConnectionManager MakeDApps(IBridgeStorage storage)
    {
        // Its own bridge, reaching public endpoints only (S12), sharing the operation journal.
        var bridge = new TaquitoBridge(requirePublicEndpoints: true) { OperationJournal = TaquitoBridge.Shared.OperationJournal };
        return new DAppConnectionManager(storage, () => Wallets, SigningKeyFor, bridge);
    }

    /// <summary>
    /// A directory change ends the dApp session: the old client is stopped (its waiting requests
    /// aborted) and a new manager is built on the new directory's storage, so sessions paired
    /// under one wallet can never ask about another's accounts (S08).
    /// </summary>
    private void ReplaceDApps(string directory)
    {
        var old = Dapps;
        old.Invalidate();
        var fresh = MakeDApps(dappStorageFactory?.Invoke(directory) ?? new InMemoryBridgeStorage());
        Dapps = fresh;
        var start = startsDApps && Wallets.Count > 0;
        _ = Swap();

        async Task Swap()
        {
            await old.Stop();
            if (start && ReferenceEquals(Dapps, fresh)) await fresh.Start();
        }
    }

    /// <summary>
    /// Points the app at a different wallet directory: rebuilds the stores, reloads the wallets and
    /// restores whatever that directory remembers. The choice is kept in preferences.
    /// </summary>
    public void ChangeWalletDirectory(string path)
    {
        if (storeFactory is null || directorySettings is null) return;
        var directory = PathUtility.Standardized(path);
        if (WalletDirectory is { } current && PathUtility.SamePath(directory, current)) return;
        walletDirectoryRevision = Guid.NewGuid();
        directorySettings.Set(directory);
        (walletStore, stateStore) = storeFactory(directory);
        WalletDirectory = directory;
        state = stateStore.Load();
        proposalStore = new FileMultisigProposalStore(directory);
        MultisigProposals = proposalStore.Load();

        try
        {
            Wallets = walletStore.Load();
            ErrorMessage = null;
        }
        catch (Exception e)
        {
            Wallets = [];
            ErrorMessage = $"Could not load accounts: {e.Message}";
        }
        SelectedWalletID = Wallets.FirstOrDefault(w => w.Alias == state.SelectedWalletAlias)?.Id ?? Wallets.FirstOrDefault()?.Id;
        if (Network.Named(state.NetworkName) is { } remembered) SwitchNetwork(remembered);
        ImportableWalletCount = CountImportable(importSource, Wallets);
        OnPropertyChanged(nameof(IsUsingDefaultWalletDirectory));
        ReplaceDApps(directory);
        ClearChainData();
        _ = Refresh();
        _ = BackUp();
    }

    public bool IsUsingDefaultWalletDirectory =>
        directorySettings is null || WalletDirectory is null || PathUtility.SamePath(WalletDirectory, directorySettings.DefaultDirectory);

    public string? DefaultWalletDirectory => directorySettings?.DefaultDirectory;

    private sealed class Snapshot
    {
        public TezBalance? TezBalance;
        public List<AssetBalance> Assets = [];
        public Portfolio Portfolio = Portfolio.Empty;
        public IReadOnlyList<string> Domains = [];
        public IReadOnlyList<TezosTransaction> Transactions = [];
        public DelegateInfo? DelegateInfo;
        public bool NotOnChain;
        public string? Error;
    }

    /// <summary>
    /// Loads the dashboard for the selected account. Everything is fetched into a local snapshot
    /// through the chain service captured at the start, and published in one step only if the
    /// account, network, directory and refresh generation are still the ones it was started for;
    /// a slow reply from a previous context can never land on the screen (S16). Two refreshes of
    /// the same context may both publish: both describe the same account on the same chain.
    /// </summary>
    public async Task Refresh()
    {
        if (CurrentContext is not { } mine)
        {
            ClearChainData();
            return;
        }
        var chain = this.chain;
        var address = mine.Address;
        IsLoading = true;
        ErrorMessage = null;

        var snapshot = new Snapshot();
        // Every load starts at once, as upstream's `async let`s do; one that throws before it
        // returns a task fails like one that throws later.
        static Task<T> Start<T>(Func<Task<T>> load)
        {
            try { return load(); }
            catch (Exception e) { return Task.FromException<T>(e); }
        }
        var tez = Start(() => chain.TezBalanceFor(address));
        var holdings = Start(() => chain.PortfolioFor(address));
        var names = Start(() => chain.DomainsFor(address));
        var history = Start(() => chain.RecentTransactionsFor(address, RecentTransactionLimit));
        var delegation = Start(() => chain.DelegateInfoFor(address));
        try
        {
            var tezBalance = await tez;
            snapshot.TezBalance = tezBalance;
            snapshot.Assets = [new AssetBalance("tez", new AssetBalance.AssetKind.Tez(), "Tezos", "tz", tezBalance.Total) { Details = tezBalance.Breakdown }];
            try { snapshot.Portfolio = await holdings; }
            catch (Exception) { snapshot.Portfolio = new Portfolio([], [], new Portfolio.PortfolioState.Unavailable("Could not read the account's tokens.")); }
            snapshot.Domains = await names;
            snapshot.Transactions = await history;
            try { snapshot.DelegateInfo = await delegation; } catch (Exception) { }
        }
        catch (ChainError.AccountNotOnChain)
        {
            // Not on layer 1: no balance rows at all.
            snapshot.NotOnChain = true;
            try { snapshot.Portfolio = await holdings; } catch (Exception) { }
            try { snapshot.Domains = await names; } catch (Exception) { }
        }
        catch (Exception e)
        {
            snapshot.Error = e.Message;
        }
        Observe(holdings, names, history, delegation);

        // Publish only into the context the load was started for.
        if (CurrentContext != mine) return;
        IsLoading = false;
        if (snapshot.Error is { } error)
        {
            ErrorMessage = error;
            return;
        }
        // Windows-only: publish a list only when its contents changed. SwiftUI diffs what it is
        // given, so upstream assigns freely; here every change notification rebuilds rows and
        // reloads their pictures, and a refresh runs on every new block (the dashboard flickered).
        // Records compare by content (NFT, AssetBalance and BakerRecord include their lists).
        TezBalance = snapshot.TezBalance;
        if (!Assets.SequenceEqual(snapshot.Assets)) Assets = snapshot.Assets;
        PortfolioState = snapshot.Portfolio.State;
        // An unavailable indexer keeps the last known lists (they are from this same context).
        if (snapshot.Portfolio.State is not Portfolio.PortfolioState.Unavailable)
        {
            if (!Tokens.SequenceEqual(snapshot.Portfolio.Tokens)) Tokens = snapshot.Portfolio.Tokens;
            if (!Nfts.SequenceEqual(snapshot.Portfolio.Nfts)) Nfts = snapshot.Portfolio.Nfts;
        }
        if (!Domains.SequenceEqual(snapshot.Domains)) Domains = snapshot.Domains;
        if (!Transactions.SequenceEqual(snapshot.Transactions)) Transactions = snapshot.Transactions;
        DelegateInfo = snapshot.DelegateInfo;
        AccountNotOnChain = snapshot.NotOnChain;
    }

    /// <summary>Marks the loads a refresh gave up on as observed, so their failures never surface as unobserved exceptions.</summary>
    private static void Observe(params Task[] tasks)
    {
        foreach (var task in tasks) _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }

    public void Select(Wallet wallet)
    {
        if (wallet.Id == SelectedWalletID) return;
        SelectedWalletID = wallet.Id;
        RememberSelection();
        CancelFaucetRequest();
        ClearChainData();
        _ = Refresh();
    }

    public const int MinimumPassphraseLength = 8;

    /// <summary>A default alias that is not already taken: "My Account", then "My Account 2", "My Account 3", …</summary>
    public string SuggestedAlias(string @base = "My Account")
    {
        var taken = Wallets.Select(w => w.Alias.ToLowerInvariant()).ToHashSet();
        if (!taken.Contains(@base.ToLowerInvariant())) return @base;
        var n = 2;
        while (taken.Contains($"{@base} {n}".ToLowerInvariant())) n++;
        return $"{@base} {n}";
    }

    /// <summary>
    /// Generates a key for <c>scheme</c>, writes it to the store (encrypted with <c>passphrase</c> when given),
    /// and adds and selects the new wallet.
    /// </summary>
    public async Task CreateWallet(string alias, AddressScheme scheme, string? passphrase = null)
    {
        var name = alias.Trim();
        if (name.Length == 0) throw new WalletError.EmptyAlias();
        if (Wallets.Any(w => w.Alias == name)) throw new WalletError.AliasExists(name);
        if (!scheme.IsSupported()) throw new WalletError.UnsupportedScheme(scheme);

        IsCreatingWallet = true;
        try
        {
            if (passphrase is not null && passphrase.Length < MinimumPassphraseLength) throw new WalletError.PassphraseTooShort();
            var material = await keyGenerator.Generate(scheme);
            var secretKey = material.SecretKey;
            var kind = KeyKind.Unencrypted;
            if (passphrase is not null)
            {
                secretKey = await keyGenerator.Encrypt(material.SecretKey, passphrase);
                kind = KeyKind.Encrypted;
            }
            var wallet = new Wallet(name, new Address(material.Address), scheme, material.PublicKey, kind);
            walletStore.AddWithSecretKey(wallet, secretKey);
            Wallets = TryLoad() ?? [.. Wallets, wallet];
            Select(wallet);
            await BackUp();
        }
        finally
        {
            IsCreatingWallet = false;
        }
    }

    private IReadOnlyList<Wallet>? TryLoad()
    {
        try { return walletStore.Load(); }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Removes the selected account from the key store (secret key included). Encrypted keys must
    /// be unlocked with <c>passphrase</c> first, so a key cannot be thrown away by someone who does not
    /// know its password. Earlier backups keep their copies.
    /// </summary>
    public async Task ForgetSelectedWallet(string? passphrase)
    {
        if (SelectedWallet is not { } wallet) throw new WalletError.EmptyAlias();
        if (wallet.IsSessionOnly)
        {
            await ForgetSessionWallet(wallet, passphrase);
            return;
        }
        var revision = walletDirectoryRevision;
        var store = walletStore;
        var snapshot = store.RemovalSnapshot(wallet);
        if (wallet.KeyKind == KeyKind.Encrypted)
        {
            if (string.IsNullOrEmpty(passphrase)) throw new SendViewModel.SendError.PassphraseRequired();
            if (snapshot.SecretKey is not { } stored) throw new SendViewModel.SendError.NoSecretKey();
            await KeyImporter.Decrypt(stored, passphrase);
        }
        // Even switching away and back invalidates approval. The captured store checks all account
        // entries against the password-verified snapshot under the same lock as deletion.
        if (walletDirectoryRevision != revision) throw new WalletError.DirectoryChanged();
        store.Remove(wallet, snapshot);
        try { Wallets = store.Load(); }
        catch (Exception) { Wallets = Wallets.Where(w => w.Id != wallet.Id).ToList(); }
        SelectAfterRemoval();
        await BackUp();
    }

    /// <summary>
    /// Drops a session-only key: wiped from memory now (it was never on disk). An encrypted one
    /// is opened first, as for a stored key, so the password proves the owner is asking. The
    /// entry is pinned before the password check and only that exact entry is removed after it
    /// (S07): a rename, replacement or re-import under the same alias in the meantime means
    /// nothing is removed.
    /// </summary>
    private async Task ForgetSessionWallet(Wallet wallet, string? passphrase)
    {
        var snapshot = sessionKeys.RemovalSnapshotFor(wallet) ?? throw new SendViewModel.SendError.NoSecretKey();
        if (wallet.KeyKind == KeyKind.Encrypted)
        {
            if (string.IsNullOrEmpty(passphrase)) throw new SendViewModel.SendError.PassphraseRequired();
            await KeyImporter.Decrypt(snapshot.SecretKey, passphrase);
        }
        sessionKeys.Remove(snapshot);
        SessionWallets = sessionKeys.Wallets;
        SelectAfterRemoval();
    }

    private void SelectAfterRemoval()
    {
        if (Wallets.FirstOrDefault() is { } next)
        {
            Select(next);
        }
        else
        {
            SelectedWalletID = null;
            RememberSelection();
            Domains = []; Assets = []; Tokens = []; Nfts = []; Transactions = []; DelegateInfo = null; TezBalance = null;
        }
    }

    /// <summary>What Export puts on screen: the clear key, and the encrypted form when that is how it is stored.</summary>
    public sealed record ExportedKey(string Clear, string? Encrypted)
    {
        private bool PrintMembers(System.Text.StringBuilder builder) => false;
    }

    /// <summary>
    /// The selected account's secret key. Encrypted keys are opened with <c>passphrase</c>; a wrong one
    /// surfaces as <c>ChainError.WrongPassphrase</c>.
    /// </summary>
    public async Task<ExportedKey> ExportSecretKey(string? passphrase)
    {
        if (SelectedWallet is not { } wallet) throw new WalletError.EmptyAlias();
        if (SoftwareSecret(wallet) is not { } stored) throw new SendViewModel.SendError.NoSecretKey();
        switch (wallet.KeyKind)
        {
            case KeyKind.Unencrypted:
                return new ExportedKey(stored, null);
            case KeyKind.Encrypted:
                if (string.IsNullOrEmpty(passphrase)) throw new SendViewModel.SendError.PassphraseRequired();
                return new ExportedKey(await KeyImporter.Decrypt(stored, passphrase), stored);
            default:
                throw new SendViewModel.SendError.NoSecretKey();
        }
    }

    /// <summary>
    /// Adds an account from key material obtained elsewhere (a pasted key or a recovery phrase).
    /// Stores it clear, or encrypted with <c>storePassphrase</c>; an already-encrypted key is kept as is.
    /// With <c>keepForSession</c> the key is a paper key for this run only: held in memory (encrypted
    /// there too when a password is given), never written to the directory or a backup, and gone
    /// when Signet quits.
    /// </summary>
    public async Task ImportAccount(string alias, KeyMaterial material, bool alreadyEncrypted = false, string? storePassphrase = null, bool keepForSession = false)
    {
        var name = alias.Trim();
        if (name.Length == 0) throw new WalletError.EmptyAlias();
        RequireFree(name, material.Address);

        var secretKey = material.SecretKey;
        var kind = alreadyEncrypted ? KeyKind.Encrypted : KeyKind.Unencrypted;
        if (!alreadyEncrypted && storePassphrase is not null)
        {
            if (storePassphrase.Length < MinimumPassphraseLength) throw new WalletError.PassphraseTooShort();
            secretKey = await keyGenerator.Encrypt(material.SecretKey, storePassphrase);
            kind = KeyKind.Encrypted;
        }
        // Another import or a rename may have taken the name or address during the encryption.
        RequireFree(name, material.Address);
        var wallet = new Wallet(name, new Address(material.Address), material.Scheme, material.PublicKey, kind);
        if (keepForSession)
        {
            wallet = wallet with { IsSessionOnly = true };
            sessionKeys.Add(wallet, secretKey);
            SessionWallets = sessionKeys.Wallets;
            Select(wallet);
            return;
        }
        walletStore.AddWithSecretKey(wallet, secretKey);
        Wallets = TryLoad() ?? [.. Wallets, wallet];
        Select(wallet);
        await BackUp();
    }

    /// <summary>Remember key is for the selected account only while its key would be forgotten at quit.</summary>
    public bool CanRememberSelectedKey => SelectedWallet?.IsSessionOnly == true;

    /// <summary>
    /// Moves the selected session-only key into the wallet directory, so it is kept like any
    /// other account (and backed up). An encrypted key is written as it is held; a clear one
    /// can be encrypted with <c>storePassphrase</c> on the way. The entry is pinned first and
    /// removed from memory only if it is unchanged after the encryption wait (S07); if the
    /// directory then refuses it, the key goes back into memory and nothing is lost.
    /// </summary>
    public async Task RememberSelectedWallet(string? storePassphrase = null)
    {
        if (SelectedWallet is not { IsSessionOnly: true } wallet) throw new WalletError.EmptyAlias();
        var snapshot = sessionKeys.RemovalSnapshotFor(wallet) ?? throw new SendViewModel.SendError.NoSecretKey();
        var revision = walletDirectoryRevision;
        var secretKey = snapshot.SecretKey;
        var stored = snapshot.Wallet with { IsSessionOnly = false };
        if (stored.KeyKind == KeyKind.Unencrypted && !string.IsNullOrEmpty(storePassphrase))
        {
            if (storePassphrase.Length < MinimumPassphraseLength) throw new WalletError.PassphraseTooShort();
            secretKey = await keyGenerator.Encrypt(secretKey, storePassphrase);
            stored = stored with { KeyKind = KeyKind.Encrypted };
        }
        if (walletDirectoryRevision != revision) throw new WalletError.DirectoryChanged();
        // Against the directory as it is now, not the list loaded earlier: another program may
        // have written to it since.
        var onDisk = walletStore.Load();
        if (onDisk.Any(w => w.Alias == stored.Alias)) throw new WalletError.AliasExists(stored.Alias);
        if (onDisk.FirstOrDefault(w => w.Address == stored.Address) is { } taken) throw new WalletError.AddressExists(taken.Alias);
        sessionKeys.Remove(snapshot);
        try
        {
            walletStore.AddWithSecretKey(stored, secretKey);
        }
        catch
        {
            try { sessionKeys.Add(snapshot.Wallet, snapshot.SecretKey); } catch (SessionKeyStore.StoreError) { }
            SessionWallets = sessionKeys.Wallets;
            throw;
        }
        SessionWallets = sessionKeys.Wallets;
        Wallets = TryLoad() ?? [.. Wallets, stored];
        Select(Wallets.FirstOrDefault(w => w.Alias == stored.Alias) ?? stored);
        await BackUp();
    }

    /// <summary>Aliases and addresses are unique across stored, session-only and watch-only accounts.</summary>
    private void RequireFree(string alias, string address)
    {
        if (Wallets.Any(w => w.Alias == alias)) throw new WalletError.AliasExists(alias);
        if (Wallets.FirstOrDefault(w => w.Address.Value == address) is { } existing) throw new WalletError.AddressExists(existing.Alias);
    }

    /// <summary>Copies wallets from the octez-client directory into Signet's own. Returns how many were added.</summary>
    public int ImportFromOctezClient()
    {
        if (importSource is null) return 0;
        var added = walletStore.ImportWallets(importSource);
        Wallets = walletStore.Load();
        if (SelectedWallet is null) SelectedWalletID = Wallets.FirstOrDefault()?.Id;
        else OnPropertyChanged(nameof(SelectedWallet));
        ImportableWalletCount = CountImportable(importSource, Wallets);
        _ = Refresh();
        return added;
    }

    /// <summary>Aliases in <c>source</c> that are not already in <c>existing</c>.</summary>
    private static int CountImportable(string? source, IReadOnlyList<Wallet> existing)
    {
        if (source is null) return 0;
        var store = new TezosClientStore(source);
        if (!store.HasWallets) return 0;
        IReadOnlyList<Wallet> candidates;
        try { candidates = store.Load(); }
        catch (Exception) { return 0; }
        var known = existing.Select(w => w.Alias).ToHashSet();
        return candidates.Count(c => !known.Contains(c.Alias));
    }

    /// <summary>Adds an address book entry: an alias for an address we hold no key for.</summary>
    public async Task AddWatchOnlyWallet(string alias, string rawAddress)
    {
        var name = alias.Trim();
        var address = new Address(rawAddress.Trim());
        if (name.Length == 0) throw new WalletError.EmptyAlias();
        if (!address.IsValidAccount) throw new WalletError.InvalidAddress();
        if (Wallets.Any(w => w.Alias == name)) throw new WalletError.AliasExists(name);
        if (Wallets.FirstOrDefault(w => w.Address == address) is { } existing) throw new WalletError.AddressExists(existing.Alias);

        var wallet = new Wallet(name, address, keyKind: KeyKind.None);
        walletStore.AddWatchOnly(wallet);
        Wallets = TryLoad() ?? [.. Wallets, wallet];
        Select(wallet);
        await BackUp();
    }

    /// <summary>
    /// Asks the current testnet's faucet to fund the selected address, then polls until the
    /// account appears on chain. The work runs in a cancellable task (S21): selecting another
    /// account or calling <c>CancelFaucetRequest</c> stops the proof of work.
    /// </summary>
    public async Task RequestTestTez(double amount = FaucetAmountTez)
    {
        if (SelectedWallet is not { } wallet || FaucetService.For(Network) is not { } service) return;
        faucetTask?.Cancel();
        var cancel = new CancellationTokenSource();
        faucetTask = cancel;
        var address = wallet.Address;
        Faucet = new FaucetStatus.Solving(0, 1);
        var ui = SynchronizationContext.Current;
        try
        {
            // Solving runs off the UI thread; cancelling stops waiting for it.
            var worker = Task.Run(() => service.RequestTez(address, amount, (done, total) =>
            {
                void Report() { if (!cancel.IsCancellationRequested && Faucet is FaucetStatus.Solving) Faucet = new FaucetStatus.Solving(done, total); }
                if (ui is null) Report(); else ui.Post(_ => Report(), null);
            }), cancel.Token);
            var hash = await worker.WaitAsync(cancel.Token);
            if (cancel.IsCancellationRequested) return;
            Faucet = new FaucetStatus.Sent(hash);
            // The faucet's transfer needs a block; refresh until the balance moves (or give up quietly).
            var before = TezBalance?.Spendable ?? 0;
            for (var i = 0; i < 12 && SelectedWallet?.Address == address && !cancel.IsCancellationRequested; i++)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(5), cancel.Token); } catch (TaskCanceledException) { return; }
                await Refresh();
                if (!AccountNotOnChain && (TezBalance?.Spendable ?? 0) > before) { Faucet = new FaucetStatus.Received(hash); break; }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            if (!cancel.IsCancellationRequested) Faucet = new FaucetStatus.Failed(e.Message);
        }
        finally
        {
            if (faucetTask == cancel) faucetTask = null;
        }
    }

    /// <summary>Stops a faucet request in flight and clears its status.</summary>
    public void CancelFaucetRequest()
    {
        faucetTask?.Cancel();
        faucetTask = null;
        Faucet = new FaucetStatus.Idle();
    }

    /// <summary>
    /// Runs a staking-family operation for the selected wallet with the limits of the estimate
    /// the user approved (S14) and waits one block on the chain service the operation went
    /// through (S16). Returns the operation hash and the block it landed in, then refreshes.
    /// </summary>
    public async Task<(string Hash, int Level)> PerformStaking(StakingOperation operation, OperationLimits? limits, string? passphrase)
    {
        if (SelectedWallet is not { } wallet) throw new WalletError.EmptyAlias();
        if (limits is null) throw new WalletError.NotEstimated();
        var signer = SigningKeyFor(wallet, passphrase) ?? throw new SendViewModel.SendError.NoSecretKey();
        var chain = this.chain;
        var sent = await chain.PerformStaking(operation, wallet, signer, limits);
        int level;
        try { level = await chain.WaitForConfirmation(sent.Hash); }
        catch (Exception) when (!sent.Injected) { throw new WalletError.OutcomeUnknown(sent.Hash); }
        await RefreshAfterOperation();
        return (sent.Hash, level);
    }

    /// <summary>
    /// Refreshes now and again a little later: the node confirms a block before the indexer has it,
    /// so history fetched immediately after an operation often misses it.
    /// </summary>
    public async Task RefreshAfterOperation()
    {
        await Refresh();
        _ = Later();
        async Task Later()
        {
            foreach (var delay in new[] { 6, 15 })
            {
                await Task.Delay(TimeSpan.FromSeconds(delay));
                await Refresh();
            }
        }
    }

    /// <summary>Fee estimate for a staking-family operation from the selected wallet.</summary>
    public Task<TransferEstimate> EstimateStaking(StakingOperation operation) =>
        SelectedWallet is { } wallet ? chain.EstimateStaking(operation, wallet) : throw new WalletError.EmptyAlias();

    /// <summary>
    /// The buy widget for the selected account at <c>provider</c>. With a key we can sign for, the
    /// address is pre-validated (a signed per-provider message) so the user has nothing to prove in
    /// the widget; otherwise the address is only pre-filled.
    /// </summary>
    public async Task<Uri> BuyURL(BuyProvider provider, string fiat, string? passphrase, bool embedded = true, bool dark = false)
    {
        var wallet = SelectedWallet ?? throw new WalletError.EmptyAlias();
        var code = MtPelerin.RandomCode();
        SignedPayload? proof = null;
        if (provider.OwnershipPayload(code) is { } payload && SigningKeyFor(wallet, passphrase) is { } signer)
            proof = await chain.SignPayload(signer, payload);
        return provider.BuyURL(wallet.Address, fiat, code, proof, embedded, dark);
    }

    public Task<GovernanceInfo> GovernanceInfoForSelected() =>
        SelectedWallet is { } wallet ? chain.GovernanceInfoFor(wallet.Address) : throw new WalletError.EmptyAlias();

    /// <summary>Upvotes or casts a ballot for the selected baker and waits one block.</summary>
    public async Task<(string Hash, int Level)> PerformGovernance(GovernanceOperation operation, string? passphrase)
    {
        if (SelectedWallet is not { } wallet) throw new WalletError.EmptyAlias();
        var signer = SigningKeyFor(wallet, passphrase) ?? throw new SendViewModel.SendError.NoSecretKey();
        var chain = this.chain;
        var hash = await chain.PerformGovernance(operation, wallet, signer);
        var level = await chain.WaitForConfirmation(hash);
        await RefreshAfterOperation();
        return (hash, level);
    }

    public async Task<IReadOnlyList<BakerCandidate>> Bakers()
    {
        try { return await chain.Bakers(60); }
        catch (Exception) { return []; }
    }

    /// <summary>Proof of possession for one of our tz4 wallets (needed to make it a consensus or companion key).</summary>
    public async Task<string> ProofOfPossession(Wallet wallet, string? passphrase)
    {
        var signer = SigningKeyFor(wallet, passphrase) ?? throw new SendViewModel.SendError.NoSecretKey();
        return await chain.ProofOfPossession(signer);
    }

    /// <summary>The live chain service, for flows that run their own lookups (Send).</summary>
    public IChainService ChainService => chain;

    /// <summary>
    /// A software key's secret (clear or octez-encrypted) from the directory, or from memory for
    /// a session-only key; both check the alias still maps to the wallet's address.
    /// </summary>
    private string? SoftwareSecret(Wallet wallet) => wallet.IsSessionOnly ? sessionKeys.SecretKey(wallet) : walletStore.SecretKey(wallet);

    /// <summary>
    /// What signs for one of our wallets: its secret key from the store (with <c>passphrase</c> if
    /// encrypted) or its Ledger key. <c>null</c> for watch-only and remote-signer aliases.
    /// </summary>
    public SigningKey? SigningKeyFor(Wallet wallet, string? passphrase) => wallet.KeyKind switch
    {
        KeyKind.Unencrypted or KeyKind.Encrypted => SoftwareSecret(wallet) is { } secretKey
            ? new SigningKey.Secret(secretKey, wallet.KeyKind == KeyKind.Encrypted ? passphrase : null, wallet.Address)
            : null,
        KeyKind.Ledger => wallet.LedgerKey is { } key ? new SigningKey.Ledger(key, wallet.Address) : null,
        _ => null,
    };

    /// <summary>
    /// How to label an address in lists: our own alias (verified), else the indexer's name, else the
    /// shortened address. The flag says whether the name came from our records.
    /// </summary>
    public (string Name, bool IsOurs) DisplayName(Address address, string? indexerAlias)
    {
        if (Wallets.FirstOrDefault(w => w.Address == address) is { } mine) return (mine.Alias, true);
        if (!string.IsNullOrEmpty(indexerAlias)) return (indexerAlias, false);
        return (address.Shortened(), false);
    }

    /// <summary>The TzProfiles name (via TzKT, always mainnet) for any address, or <c>null</c> if it has none.</summary>
    public async Task<string?> ProfileName(Address address)
    {
        try
        {
            var name = (await chain.AccountProfileFor(address))?.Name?.Trim();
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Tezos Domains names for any address, for previews while typing.</summary>
    public async Task<IReadOnlyList<string>> DomainNames(Address address)
    {
        try { return await chain.DomainsFor(address); }
        catch (Exception) { return []; }
    }

    /// <summary>Renames the selected wallet's alias in the store and keeps it selected.</summary>
    public void RenameSelectedWallet(string newAlias)
    {
        if (SelectedWallet is not { } wallet) return;
        var name = newAlias.Trim();
        if (name.Length == 0) throw new WalletError.EmptyAlias();
        if (name == wallet.Alias) return;
        if (Wallets.Any(w => w.Alias == name)) throw new WalletError.AliasExists(name);

        if (wallet.IsSessionOnly)
        {
            sessionKeys.Rename(wallet.Alias, name);
            SessionWallets = sessionKeys.Wallets;
        }
        else
        {
            walletStore.Rename(wallet.Alias, name);
            Wallets = walletStore.Load();
        }
        SelectedWalletID = name;
        RememberSelection();
        if (!wallet.IsSessionOnly) _ = BackUp();
    }

    private void RememberSelection()
    {
        state = state with { SelectedWalletAlias = SelectedWalletID };
        PersistState();
    }

    private void PersistState()
    {
        try { stateStore.Save(state); }
        catch (Exception e) { Trace.WriteLine($"Signet: could not save state: {e.Message}"); }
    }

    // ---- Multisig --------------------------------------------------------------------------------

    private readonly IMultisigService multisigService;
    public IMultisigService Multisig => multisigService.ForNetwork(Network);
    private IMultisigProposalStore proposalStore;

    /// <summary>Where the Sign multisig sheet should start.</summary>
    public sealed record MultisigSheetPreset(MultisigContract Contract, bool Delegating = false);

    public abstract class MultisigError(string message) : Exception(message)
    {
        public sealed class InvalidAddress() : MultisigError("That is not a contract address (KT1…).");
        public sealed class NotAMultisig(string hash)
            : MultisigError($"That contract is not an octez-client multisig (its script hash is {hash[..Math.Min(16, hash.Length)]}…), so Signet cannot sign for it.");
        public sealed class NotAMember() : MultisigError("None of your accounts is a signer of that multisig.");
        public sealed class InvalidKey(string key) : MultisigError($"“{key[..Math.Min(16, key.Length)]}…” is not a public key (edpk, sppk, p2pk, BLpk or mdpk).");
        public sealed class ThresholdOutOfRange(int threshold, int keys)
            : MultisigError($"The threshold must be between 1 and the number of keys ({keys}), not {threshold}.");
        public sealed class CannotSign(string alias) : MultisigError($"Signet cannot sign with “{alias}”.");
        public sealed class AlreadySigned(string alias) : MultisigError($"“{alias}” has already signed this proposal.");
        public sealed class Stale() : MultisigError("The multisig has moved on since this was proposed; the signatures no longer apply.");
        public sealed class NotRevealed(Address address)
            : MultisigError($"{address.Shortened()} has not revealed its public key on this network yet (it has never sent an operation). Ask its owner for the public key instead.");
        public sealed class NotASignerCandidate(string text) : MultisigError($"“{text[..Math.Min(20, text.Length)]}…” is neither a public key nor a tz address.");
        public sealed class KeyAddressMismatch(Address wanted, Address got)
            : MultisigError($"The node answered with a key for {got.Shortened()}, not {wanted.Shortened()}. Nothing was added; check the node or paste the public key.");
        public sealed class WrongNetwork(string name) : MultisigError($"This proposal was made on {name}; switch to that network to sign it.");
        public sealed class Tampered()
            : MultisigError("This proposal does not match what the multisig would sign for it now. Nothing was signed; discard it and propose it again.");
        public sealed class AwaitingConfirmation(string hash, Address address)
            : MultisigError($"Operation {hash} has not been confirmed. Its contract address is {address.Value}. Open Send from the paying account to recover this operation; do not create another contract.");
        public sealed class InvalidAmount() : MultisigError("The initial balance must be a number of tez, zero or more.");
        public sealed class ContextChanged(Address address)
            : MultisigError($"The wallet directory or network changed while the multisig {address.Shortened()} was being set up. It exists on chain but was not recorded here; add it with Add multisig… where it belongs.");
    }

    /// <summary>Proposals for the network in use, newest first.</summary>
    public IReadOnlyList<MultisigProposal> CurrentMultisigProposals =>
        MultisigProposals.Where(p => p.NetworkName == Network.Name).OrderByDescending(p => p.Created).ToList();

    /// <summary>Accounts whose public key we know, so they can be put on a multisig.</summary>
    public IReadOnlyList<Wallet> MultisigKeyCandidates => Wallets.Where(w => w.PublicKey is not null).ToList();

    public static bool IsPublicKey(string key) =>
        // The node checks the key properly at origination; this only keeps obvious garbage out of the list.
        new[] { "edpk", "sppk", "p2pk", "BLpk", "mdpk" }.Any(p => key.StartsWith(p, StringComparison.Ordinal)) && key.Length > 6 && key.All(char.IsLetterOrDigit);

    /// <summary>
    /// A signer for a new multisig from what the user gave: a public key as is, or a tz address
    /// (pasted, from the address book or one of ours) resolved to the key it has revealed on chain.
    /// </summary>
    public async Task<string> MultisigSignerKey(string text)
    {
        var trimmed = text.Trim();
        if (IsPublicKey(trimmed)) return trimmed;
        var address = new Address(trimmed);
        if (!address.IsValidAccount || address.IsContract) throw new MultisigError.NotASignerCandidate(trimmed);
        if (Wallets.FirstOrDefault(w => w.Address == address) is { PublicKey: { } mine }) return mine;
        var key = await Multisig.RevealedPublicKey(Network.RpcURL, address) ?? throw new MultisigError.NotRevealed(address);
        // The node is not trusted to say whose key that is: it must hash to the address asked for.
        if (!IsPublicKey(key)) throw new MultisigError.InvalidKey(key);
        var derived = await Multisig.AddressForPublicKey(key);
        if (derived != address) throw new MultisigError.KeyAddressMismatch(address, derived);
        return key;
    }

    public Task<MultisigInfo> MultisigInfo(MultisigContract contract) => Multisig.Info(Network.RpcURL, contract.Address);

    /// <summary>Fee and burn for deploying the multisig, with <c>amount</c> tez moved into it on creation.</summary>
    public async Task<TransferEstimate> EstimateCreateMultisig(int threshold, IReadOnlyList<string> keys, decimal amount = 0)
    {
        var wallet = SelectedWallet ?? throw new WalletError.EmptyAlias();
        CheckMultisig(threshold, keys, amount);
        return await Multisig.EstimateOriginate(Network.RpcURL, wallet, threshold, keys, amount);
    }

    private static void CheckMultisig(int threshold, IReadOnlyList<string> keys, decimal amount)
    {
        if (keys.FirstOrDefault(k => !IsPublicKey(k)) is { } bad) throw new MultisigError.InvalidKey(bad);
        if (threshold < 1 || threshold > keys.Count) throw new MultisigError.ThresholdOutOfRange(threshold, keys.Count);
        if (amount < 0 || !Mutez.IsValid(Mutez.FromTez(amount))) throw new MultisigError.InvalidAmount();
    }

    /// <summary>
    /// Deploys a multisig from the selected account with the approved estimate's limits (S14),
    /// waits for its block and records it under <c>alias</c>.
    /// </summary>
    public async Task<MultisigContract> CreateMultisig(string alias, int threshold, IReadOnlyList<string> keys, decimal amount, OperationLimits? limits, string? passphrase)
    {
        if (limits is null) throw new WalletError.NotEstimated();
        var wallet = SelectedWallet ?? throw new WalletError.EmptyAlias();
        var name = alias.Trim();
        if (name.Length == 0) throw new WalletError.EmptyAlias();
        if (Wallets.Any(w => w.Alias == name)) throw new WalletError.AliasExists(name);
        CheckMultisig(threshold, keys, amount);
        var signer = SigningKeyFor(wallet, passphrase) ?? throw new MultisigError.CannotSign(wallet.Alias);
        var chain = this.chain;
        var context = (walletDirectoryRevision, Network, operationGeneration);
        var (hash, address) = await Multisig.Originate(Network.RpcURL, signer, threshold, keys, amount, limits);
        try { await chain.WaitForConfirmation(hash); }
        catch (Exception) { throw new MultisigError.AwaitingConfirmation(hash, address); }
        return await RecordContract(new MultisigContract(name, address), context);
    }

    /// <summary>
    /// Writes the contract alias, puts it in the address book and shows it. The alias goes to the
    /// directory the operation started in: after a directory or network switch it is refused,
    /// and the address is reported so the user can add it where it belongs (S16).
    /// </summary>
    private async Task<MultisigContract> RecordContract(MultisigContract contract, (Guid Revision, Network Network, Guid Generation) context)
    {
        if (walletDirectoryRevision != context.Revision || Network != context.Network || operationGeneration != context.Generation)
            throw new MultisigError.ContextChanged(contract.Address);
        walletStore.AddContract(contract);
        var entry = new Wallet(contract.Alias, contract.Address, keyKind: KeyKind.None);
        try { Wallets = walletStore.Load(); }
        catch (Exception) { Wallets = [.. Wallets, entry]; }
        Select(Wallets.FirstOrDefault(w => w.Alias == contract.Alias) ?? entry);
        await BackUp();
        return contract;
    }

    /// <summary>
    /// Records an existing multisig under <c>alias</c>, after checking it is the generic multisig and
    /// one of our accounts is among its keys.
    /// </summary>
    public async Task<MultisigContract> AddMultisig(string alias, string rawAddress)
    {
        var name = alias.Trim();
        var address = new Address(rawAddress.Trim());
        if (name.Length == 0) throw new WalletError.EmptyAlias();
        if (!address.IsContract || !address.IsValidAccount) throw new MultisigError.InvalidAddress();
        if (Wallets.Any(w => w.Alias == name)) throw new WalletError.AliasExists(name);
        if (Wallets.FirstOrDefault(w => w.Address == address) is { } existing) throw new WalletError.AddressExists(existing.Alias);
        var context = (walletDirectoryRevision, Network, operationGeneration);
        var info = await Multisig.Info(Network.RpcURL, address);
        if (!info.IsGenericMultisig) throw new MultisigError.NotAMultisig(info.ScriptHash);
        if (info.Members(Wallets).Count == 0) throw new MultisigError.NotAMember();
        return await RecordContract(new MultisigContract(name, address), context);
    }

    /// <summary>Fetches the contract's state and makes an unsigned proposal for this transfer at its current counter.</summary>
    public Task<MultisigProposal> ProposeMultisigTransfer(MultisigContract contract, decimal amount, Address destination) =>
        ProposeMultisig(new MultisigAction.Transfer(Mutez.FromTez(amount), destination.Value), contract);

    /// <summary>Fetches the contract's state and makes an unsigned proposal for this action at its current counter.</summary>
    public async Task<MultisigProposal> ProposeMultisig(MultisigAction action, MultisigContract contract)
    {
        var context = (walletDirectoryRevision, Network, operationGeneration);
        var (info, chainID, bytes) = await Multisig.Prepare(Network.RpcURL, contract.Address, action);
        if (walletDirectoryRevision != context.walletDirectoryRevision || Network != context.Network || operationGeneration != context.operationGeneration)
            throw new WalletError.DirectoryChanged();
        if (!info.IsGenericMultisig) throw new MultisigError.NotAMultisig(info.ScriptHash);
        // The same action proposed twice is the same proposal: reuse it so signatures accumulate.
        if (MultisigProposals.FirstOrDefault(p => p.ContractAddress == contract.Address.Value && p.NetworkName == Network.Name && p.Bytes == bytes) is { } existing)
            return existing;
        var proposal = new MultisigProposal(contract.Alias, contract.Address.Value, Network.Name, chainID, info.Counter, info.Threshold, info.Keys, action, bytes);
        bool Same(MultisigProposal p) => p.ContractAddress == proposal.ContractAddress && p.NetworkName == proposal.NetworkName && p.Bytes == proposal.Bytes;
        MultisigProposals = proposalStore.Update(stored => { if (!stored.Any(Same)) stored.Add(proposal); });
        return MultisigProposals.FirstOrDefault(Same) ?? proposal;
    }

    /// <summary>
    /// Signs a proposal with one of our accounts (a key of the multisig) and keeps the signature with it.
    ///
    /// The proposal file is not trusted. What gets signed is rebuilt by the bridge from the
    /// proposal's chain, contract, counter and action (the fields the sheet displays), never its
    /// stored bytes, and the chain must agree: the contract is the generic multisig, at that
    /// counter, with those keys, and packs those same bytes. Any difference means nothing is signed.
    /// </summary>
    public async Task<MultisigSignature> SignMultisigProposal(MultisigProposal proposal, Wallet wallet, string? passphrase)
    {
        if (wallet.PublicKey is not { } publicKey || !proposal.Keys.Contains(publicKey)) throw new MultisigError.NotAMember();
        if (proposal.HasSignature(publicKey)) throw new MultisigError.AlreadySigned(wallet.Alias);
        if (proposal.NetworkName != Network.Name) throw new MultisigError.WrongNetwork(proposal.NetworkName);
        var revision = walletDirectoryRevision;
        var rpcURL = Network.RpcURL;
        var generation = operationGeneration;
        var service = Multisig;

        var live = await service.Prepare(rpcURL, proposal.Contract.Address, proposal.Action);
        if (!live.Info.IsGenericMultisig) throw new MultisigError.NotAMultisig(live.Info.ScriptHash);
        if (live.ChainID != proposal.ChainID || live.Info.Counter != proposal.Counter) throw new MultisigError.Stale();
        if (!live.Info.Keys.SequenceEqual(proposal.Keys) || live.Info.Threshold != proposal.Threshold || !live.Info.Keys.Contains(publicKey)) throw new MultisigError.Stale();
        if (live.Bytes != proposal.Bytes) throw new MultisigError.Tampered();

        if (walletDirectoryRevision != revision || operationGeneration != generation) throw new WalletError.DirectoryChanged();
        var signer = SigningKeyFor(wallet, passphrase) ?? throw new MultisigError.CannotSign(wallet.Alias);
        var signed = await service.Sign(signer, proposal);
        if (signed.Bytes != proposal.Bytes) throw new MultisigError.Tampered();
        if (walletDirectoryRevision != revision || operationGeneration != generation || Network.RpcURL != rpcURL) throw new WalletError.DirectoryChanged();

        var signature = new MultisigSignature(publicKey, signed.Signature);
        UpdateProposal(proposal.Id, p => p with { Signatures = [.. p.Signatures, signature] });
        return signature;
    }

    /// <summary>Keeps a signature another signer sent us (verified against the keys when submitting).</summary>
    public void AddMultisigSignature(string signature, MultisigProposal proposal)
    {
        var trimmed = signature.Trim();
        if (trimmed.Length == 0) return;
        // Check against what is stored, not the caller's copy, so repeated pastes do not pile up.
        UpdateProposal(proposal.Id, stored => stored.Signatures.Any(s => s.Signature == trimmed)
            ? stored
            : stored with { Signatures = [.. stored.Signatures, new MultisigSignature(null, trimmed)] });
    }

    public void RemoveMultisigProposal(MultisigProposal proposal) =>
        MultisigProposals = proposalStore.Update(stored => stored.RemoveAll(p => p.Id == proposal.Id));

    /// <summary>Changes one stored proposal; the change is applied to the copy on disk, not the one in memory.</summary>
    private void UpdateProposal(Guid id, Func<MultisigProposal, MultisigProposal> change) =>
        MultisigProposals = proposalStore.Update(stored =>
        {
            var index = stored.FindIndex(p => p.Id == id);
            if (index >= 0) stored[index] = change(stored[index]);
        });

    public Task<TransferEstimate> EstimateSubmitMultisig(MultisigProposal proposal, Wallet wallet) =>
        Multisig.EstimateSubmit(Network.RpcURL, wallet, proposal, proposal.Signatures.Select(s => s.Signature).ToList());

    /// <summary>
    /// Submits the proposal with its signatures from <c>wallet</c> (which pays the fee), waits one block
    /// and forgets the proposal. Returns the operation hash.
    /// </summary>
    public async Task<string> SubmitMultisig(MultisigProposal proposal, Wallet wallet, OperationLimits? limits, string? passphrase)
    {
        if (limits is null) throw new WalletError.NotEstimated();
        var signer = SigningKeyFor(wallet, passphrase) ?? throw new MultisigError.CannotSign(wallet.Alias);
        var chain = this.chain;
        var revision = walletDirectoryRevision;
        var generation = operationGeneration;
        var hash = await Multisig.Submit(Network.RpcURL, signer, proposal, proposal.Signatures.Select(s => s.Signature).ToList(), limits);
        await chain.WaitForConfirmation(hash);
        // The proposal file belongs to the directory the submission started in.
        if (walletDirectoryRevision == revision && operationGeneration == generation) RemoveMultisigProposal(proposal);
        await RefreshAfterOperation();
        return hash;
    }

    public static readonly IReadOnlyList<Wallet> SampleWallets =
    [
        new("My Account", new Address("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb"), keyKind: KeyKind.Unencrypted),
        new("Savings", new Address("tz2BFTyPeYRzxd5aiBchbXN3WCZhx7BqbMBq"), keyKind: KeyKind.Encrypted),
    ];

    public static WalletViewModel Preview() => new(SampleWallets, new MockChainService());
}
