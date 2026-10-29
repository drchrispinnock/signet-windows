using Microsoft.UI.Xaml;
using Signet.Services;
using Signet.ViewModels;

namespace Signet.App;

/// <summary>Upstream <c>SignetApp</c>: builds the model on the real services and opens the one window.</summary>
public partial class App : Application
{
    private Window? window;

    public App()
    {
        InitializeComponent();
    }

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
        backupSettings: new BackupSettings());

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new MainWindow(MakeModel());
        window.Activate();
    }
}
