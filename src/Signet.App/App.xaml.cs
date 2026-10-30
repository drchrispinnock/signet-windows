using Microsoft.UI.Xaml;
using Signet.Services;
using Signet.ViewModels;

namespace Signet.App;

/// <summary>Upstream <c>SignetApp</c>: builds the model on the real services and opens the one window.</summary>
public partial class App : Application
{
    private Window? window;
    private WalletViewModel? model;
    /// <summary>A <c>signet://</c> link this launch was started with, handed to the model once it exists.</summary>
    private readonly Uri? launchURL;

    /// <summary>Started with <c>--preview</c>: mock chain and in-memory accounts (<c>MakePreviewModel</c>).</summary>
    private readonly bool preview;

    public App(Uri? launchURL = null, bool preview = false)
    {
        this.launchURL = launchURL;
        this.preview = preview;
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            // Windows-only: an exception escaping a UI event (an async handler) ends the process with
            // 0xC000027B and no trace. Keep the details where support can read them.
            try
            {
                var path = Path.Combine(Path.GetDirectoryName(TaquitoBridge.LogFilePath)!, "crash.log");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} {e.Exception}{Environment.NewLine}{Environment.NewLine}");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        };
    }

    /// <summary>
    /// Another launch was redirected here (<c>Program</c>), on a background thread: a link goes to the
    /// model like upstream's <c>onOpenURL</c>, and the window comes forward either way.
    /// </summary>
    public void Activated(Uri? url) => window?.DispatcherQueue.TryEnqueue(() =>
    {
        if (url is not null) model?.HandleIncomingURL(url);
        window.Activate();
    });

    /// <summary>The app's model, on the wallet directory and the live chain.</summary>
    public static WalletViewModel MakeModel()
    {
        // Signed operations survive a restart, so a lost reply can be followed up (S15).
        TaquitoBridge.Shared.OperationJournal = new OperationJournal(TezosClientStore.DefaultDirectory);
        return MakeWalletModel();
    }

    private static WalletViewModel MakeWalletModel() => new(
        chainFactory: network => new TaquitoChainService(network),
        keyGenerator: new KeyGenerator(),
        keyImporter: new BridgeKeyImporter(),
        importSource: TezosClientStore.OctezClientDirectory,
        directorySettings: new WalletDirectorySettings(),
        storeFactory: directory => (new TezosClientStore(directory), new FileAppStateStore(directory)),
        backupSettings: new BackupSettings(),
        dappStorageFactory: directory => new OctezConnectStorage(directory),
        startDApps: true,
        multisig: new BridgeMultisigService(),
        ledger: new BridgeLedgerService());

    /// <summary>
    /// Windows-only: the <c>--preview</c> model, upstream's test-host model and <c>#Preview</c> data in a
    /// real window. Mock chain, in-memory accounts and proposals, no wallet directory, no dApp client.
    /// Captain Stake is held with a placeholder key, so the baker-only screens (Baking, Governance)
    /// show their full state; the mock chain accepts any signature.
    /// </summary>
    public static WalletViewModel MakePreviewModel()
    {
        var store = new InMemoryWalletStore();
        store.AddWithSecretKey(new Models.Wallet("Captain Stake", MockChainService.CaptainStake, publicKey: "edpkPreviewCaptainStake", keyKind: Models.KeyKind.Unencrypted), "edskPreview");
        foreach (var wallet in WalletViewModel.SampleWallets) store.AddWithSecretKey(wallet with { PublicKey = "edpkPreview" + wallet.Alias.Replace(" ", "") }, "edskPreview");
        return new WalletViewModel(chain: new MockChainService(), walletStore: store, multisig: new MockMultisigService(["edpkPreviewCaptainStake", "edpkPreviewMyAccount"], 2),
            ledger: new MockLedgerService([MockLedgerService.SampleDevice]));
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Views.Disclaimer.Suppressed = preview;
        model = preview ? MakePreviewModel() : MakeModel();
        window = new MainWindow(model);
        if (launchURL is not null) model.HandleIncomingURL(launchURL);
        window.Activate();
    }
}
