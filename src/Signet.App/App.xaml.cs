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

    public App(Uri? launchURL = null)
    {
        this.launchURL = launchURL;
        InitializeComponent();
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
        startDApps: true);

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        model = MakeModel();
        window = new MainWindow(model);
        if (launchURL is not null) model.HandleIncomingURL(launchURL);
        window.Activate();
    }
}
