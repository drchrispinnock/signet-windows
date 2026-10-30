using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Signet.App.Services;
using Signet.Services;
using Signet.ViewModels;

namespace Signet.App.Views;

/// <summary>
/// The default screen from docs/spec/EXAMPLE.png: header, action row, asset list, NFT grid.
/// With no wallets yet it shows a prompt to create the first address instead. Sheets are
/// ContentDialogs, opened when the model sets one of its <c>IsPresenting…</c> flags.
/// </summary>
public sealed partial class WalletHomeView : UserControl
{
    private readonly WalletViewModel model;
    private bool dialogOpen;

    public WalletHomeView(WalletViewModel model)
    {
        this.model = model;
        InitializeComponent();
        Identity.Model = model;
        BadgeHost.Content = new NetworkBadgeMenu(model);
        ActionsHost.Content = new ActionButtonsView(model);
        ActivityHost.Content = new ActivityTabsView(model);
        StatusHost.Content = new NodeStatusBar(model.NodeMonitor);
        OperationsMenu.Opening += (_, _) => OperationsMenuItems.Fill(OperationsMenu, model, OpenSettings, CheckForUpdates);
        OperationsMenuItems.InstallShortcuts(this, model, OpenSettings);
        model.PropertyChanged += Changed;
        UpdaterService.Shared.PropertyChanged += UpdaterChanged;
        dapps = model.Dapps;
        dapps.PropertyChanged += DAppsChanged;
        Update();
        UpdateBalances();
        Loaded += async (_, _) =>
        {
            model.NodeMonitor.Start();
            await Disclaimer.ShowIfNeeded(XamlRoot);
            // A sheet asked for before the window was up (a signet:// link at launch) opens now.
            _ = PresentSheets();
            await model.Refresh();
        };
    }

    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(WalletViewModel.SelectedWallet) or nameof(WalletViewModel.Wallets) or nameof(WalletViewModel.ImportableWalletCount):
                Update();
                UpdateBalances();
                break;
            case nameof(WalletViewModel.Assets) or nameof(WalletViewModel.AccountNotOnChain) or nameof(WalletViewModel.Network):
                UpdateBalances();
                break;
            case nameof(WalletViewModel.ErrorMessage):
                ErrorLine.Text = model.ErrorMessage ?? "";
                ErrorLine.Visibility = model.ErrorMessage is null ? Visibility.Collapsed : Visibility.Visible;
                break;
            case { } name when name.StartsWith("IsPresenting", StringComparison.Ordinal):
                _ = PresentSheets();
                break;
            case nameof(WalletViewModel.Dapps):
                // A directory change brings a new manager; follow its queue instead of the old one's.
                dapps.PropertyChanged -= DAppsChanged;
                dapps = model.Dapps;
                dapps.PropertyChanged += DAppsChanged;
                _ = PresentSheets();
                break;
        }
    }

    private DAppConnectionManager dapps;

    /// <summary>The front of the dApp queue changed: show its request as soon as no other sheet is up (upstream's <c>.sheet(item:)</c>).</summary>
    private void DAppsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DAppConnectionManager.Current)) _ = PresentSheets();
    }

    private void Update()
    {
        var hasWallet = model.SelectedWallet is not null;
        Dashboard.Visibility = hasWallet ? Visibility.Visible : Visibility.Collapsed;
        WelcomeHost.Visibility = hasWallet ? Visibility.Collapsed : Visibility.Visible;
        WelcomeHost.Content = hasWallet ? null : new NoWalletsView(model);
    }

    private void UpdateBalances()
    {
        BalancesHost.Children.Clear();
        if (model.SelectedWallet is not { } wallet) return;
        if (model.AccountNotOnChain)
        {
            BalancesHost.Children.Add(new AccountNotOnChainView(model, wallet));
        }
        else
        {
            BalancesHost.Children.Add(new AssetListView { Assets = model.Assets });
            BalancesHost.Children.Add(new DelegateRowView(model));
        }
    }

    /// <summary>
    /// Settings has no flag on the model: upstream opens its own Settings scene with
    /// <c>SettingsLink</c>. Here it is one more dialog, queued like the sheets.
    /// </summary>
    private bool settingsRequested;

    public void OpenSettings()
    {
        settingsRequested = true;
        _ = PresentSheets();
    }

    /// <summary>"Check for Updates…", or an update the daily check found: the update sheet, queued like the others.</summary>
    private bool updateRequested;
    /// <summary>The version already offered this session, so a background check offers each one once.</summary>
    private string? offeredVersion;

    public void CheckForUpdates()
    {
        updateRequested = true;
        _ = PresentSheets();
    }

    private void UpdaterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(UpdaterService.State)) return;
        // Offer what the background check found (with automatic downloads on, the update is staged
        // silently and installed at the next launch, as Sparkle does).
        if (UpdaterService.Shared.State is UpdaterService.UpdateState.Available available && available.Version != offeredVersion
            && !UpdaterService.Shared.AutomaticallyDownloadsUpdates)
        {
            offeredVersion = available.Version;
            DispatcherQueue.TryEnqueue(CheckForUpdates);
        }
    }

    /// <summary>Opens the sheet the model asks for. Only one ContentDialog can be open at a time, like a Mac sheet.</summary>
    private async Task PresentSheets()
    {
        if (dialogOpen || XamlRoot is null) return;
        ContentDialog? dialog = null;
        Action? reset = null;
        if (model.Dapps.Current is { } request) { dialog = new DAppRequestSheet(model, request); reset = () => { }; }
        else if (model.IsPresentingConnectDApp) { dialog = new ConnectDAppSheet(model); reset = () => model.IsPresentingConnectDApp = false; }
        else if (model.IsPresentingCreateWallet) { dialog = new CreateWalletSheet(model); reset = () => model.IsPresentingCreateWallet = false; }
        else if (model.IsPresentingImportAccount) { dialog = new ImportAccountSheet(model); reset = () => model.IsPresentingImportAccount = false; }
        else if (model.IsPresentingRenameWallet) { dialog = new RenameWalletSheet(model); reset = () => model.IsPresentingRenameWallet = false; }
        else if (model.IsPresentingAddAddress) { dialog = new AddAddressSheet(model); reset = () => model.IsPresentingAddAddress = false; }
        else if (model.IsPresentingExportKey) { dialog = new ExportKeySheet(model); reset = () => model.IsPresentingExportKey = false; }
        else if (model.IsPresentingForget) { dialog = new ForgetAccountSheet(model); reset = () => model.IsPresentingForget = false; }
        else if (model.IsPresentingRememberKey) { dialog = new RememberKeySheet(model); reset = () => model.IsPresentingRememberKey = false; }
        else if (model.IsPresentingReceive && model.SelectedWallet is { } wallet) { dialog = new ReceiveSheet(wallet, model.Domains); reset = () => model.IsPresentingReceive = false; }
        else if (model.IsPresentingFaucet) { dialog = new FaucetSheet(model); reset = () => model.IsPresentingFaucet = false; }
        else if (model.IsPresentingStaking && model.SelectedWallet is not null)
        {
            var sheet = new StakingSheet(model);
            dialog = sheet;
            reset = () =>
            {
                model.IsPresentingStaking = false;
                if (sheet.DidOperate) _ = model.RefreshAfterOperation();
            };
        }
        else if (model.IsPresentingSend && model.SelectedWallet is { } sender)
        {
            var sheet = new SendSheet(model, sender);
            dialog = sheet;
            reset = () =>
            {
                model.IsPresentingSend = false;
                // Done (or closing after the operation went out) refreshes now and again shortly.
                if (sheet.DidSend) _ = model.RefreshAfterOperation();
            };
        }
        else if (model.IsPresentingBaking && model.SelectedWallet is not null)
        {
            var sheet = new BakingSheet(model);
            dialog = sheet;
            reset = () =>
            {
                model.IsPresentingBaking = false;
                if (sheet.DidOperate) _ = model.RefreshAfterOperation();
            };
        }
        else if (model.IsPresentingGovernance && model.SelectedWallet is not null) { dialog = new GovernanceSheet(model); reset = () => model.IsPresentingGovernance = false; }
        else if (model.IsPresentingConnectLedger) { dialog = new ConnectLedgerSheet(model); reset = () => model.IsPresentingConnectLedger = false; }
        else if (model.IsPresentingBuy && model.SelectedWallet is not null) { dialog = new BuySheet(model); reset = () => model.IsPresentingBuy = false; }
        else if (model.IsPresentingCreateMultisig) { dialog = new CreateMultisigSheet(model); reset = () => model.IsPresentingCreateMultisig = false; }
        else if (model.IsPresentingAddMultisig) { dialog = new AddMultisigSheet(model); reset = () => model.IsPresentingAddMultisig = false; }
        else if (model.IsPresentingSignMultisig)
        {
            dialog = new SignMultisigSheet(model);
            reset = () => { model.IsPresentingSignMultisig = false; model.SignMultisigPreset = null; };
        }
        else if (model.IsPresentingSubmitMultisig) { dialog = new SubmitMultisigSheet(model); reset = () => model.IsPresentingSubmitMultisig = false; }
        else if (model.IsPresentingNetworks){ dialog = new NetworksSheet(model); reset = () => model.IsPresentingNetworks = false; }
        else if (settingsRequested) { dialog = new SettingsView(model); reset = () => settingsRequested = false; }
        else if (updateRequested) { dialog = new UpdateSheet(); reset = () => updateRequested = false; }
        else
        {
            // Sheets not ported yet: drop the request rather than leave the flag set.
            model.IsPresentingSend = model.IsPresentingStaking = model.IsPresentingBaking = model.IsPresentingConnectLedger =
                model.IsPresentingGovernance = model.IsPresentingBuy = model.IsPresentingReceive = false;
            return;
        }
        dialogOpen = true;
        dialog.XamlRoot = XamlRoot;
        dialog.RequestedTheme = Services.AppearanceApplier.Current;
        try { await dialog.ShowAsync(); }
        finally
        {
            dialogOpen = false;
            reset();
        }
        // Another sheet may have been asked for while this one was up.
        await PresentSheets();
    }
}

/// <summary>Shown when there is nothing to display yet.</summary>
public sealed partial class NoWalletsView : UserControl
{
    private readonly WalletViewModel model;
    private readonly TextBlock importError = new() { Style = (Style)Application.Current.Resources["ErrorText"], TextAlignment = TextAlignment.Center, Visibility = Visibility.Collapsed };

    public NoWalletsView(WalletViewModel model)
    {
        this.model = model;
        var stack = new StackPanel { Spacing = 20, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 440 };
        stack.Children.Add(new Image { Source = new BitmapImage(new Uri("ms-appx:///Assets/Signet.png")), Width = 96, Height = 96 });
        stack.Children.Add(new TextBlock
        {
            Text = "Welcome to Signet, a Tezos Wallet for Windows.", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"],
            FontSize = 18, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center,
        });
        stack.Children.Add(new TextBlock { Text = "How would you like to start?", Style = (Style)Application.Current.Resources["SecondaryText"], HorizontalAlignment = HorizontalAlignment.Center });

        var options = new StackPanel { Spacing = 10 };
        options.Children.Add(StartOption("Create a new account", "Generate a fresh key on this PC, protected with a password.", "", true,
                                         () => model.IsPresentingCreateWallet = true));
        options.Children.Add(StartOption("Import an existing account", "From a secret key or a recovery phrase of 12 to 24 words.", "", false,
                                         () => model.IsPresentingImportAccount = true));
        options.Children.Add(StartOption("Connect a Ledger", "Use a key that stays on your hardware wallet.", "", false,
            () => model.IsPresentingConnectLedger = true));
        if (model.ImportableWalletCount > 0)
        {
            var count = model.ImportableWalletCount;
            options.Children.Add(StartOption("Import your octez-client wallet",
                $"Found {count} {(count == 1 ? "account" : "accounts")} in {TezosClientStore.OctezClientDirectory}. Copies the files into {model.WalletDirectory ?? TezosClientStore.DefaultDirectory}; octez-client keeps working.",
                "", false, ImportWallets));
        }
        stack.Children.Add(options);
        stack.Children.Add(importError);
        Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private void ImportWallets()
    {
        importError.Visibility = Visibility.Collapsed;
        try
        {
            model.ImportFromOctezClient();
        }
        catch (Exception e)
        {
            importError.Text = e.Message;
            importError.Visibility = Visibility.Visible;
        }
    }

    /// <summary>One of the ways to start on a cold boot: an icon, a title, a line of detail and a chevron.</summary>
    private static Button StartOption(string title, string detail, string glyph, bool prominent, Action? action)
    {
        var accent = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        var grid = new Grid { ColumnSpacing = 14 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new FontIcon { Glyph = glyph, FontSize = 22, Foreground = prominent ? accent : secondary });
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        text.Children.Add(new TextBlock { Text = detail, Foreground = secondary, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var chevron = new FontIcon { Glyph = "", FontSize = 11, Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"] };
        Grid.SetColumn(chevron, 2);
        grid.Children.Add(chevron);
        var button = new Button
        {
            Content = grid, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(14, 12, 14, 12), CornerRadius = new CornerRadius(10), IsEnabled = action is not null,
        };
        if (prominent) button.BorderBrush = accent;
        if (action is not null) button.Click += (_, _) => action();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"{title}. {detail}");
        return button;
    }
}

/// <summary>The launch warning. Shown once per launch until the user turns it off (Settings, on the Mac).</summary>
public static class Disclaimer
{
    public const string Key = "showsDisclaimer";
    public const string Title = "⚠️ Signet - Warning";
    public const string Message = "Signet is very new software and should be used with care. Make sure you protect your keys with a password or use a Ledger.\n\nYou can turn off this warning in the Settings.";

    /// <summary>Off for the <c>--preview</c> window, as upstream suppresses it under the test host.</summary>
    public static bool Suppressed { get; set; }

    /// <summary>OK carries on; Exit quits the app.</summary>
    public static async Task ShowIfNeeded(XamlRoot root)
    {
        if (Suppressed) return;
        // On unless turned off, like `@AppStorage(Disclaimer.key) var showsDisclaimer = true`.
        if (UserDefaults.Standard.Object(Key) is not null && !UserDefaults.Standard.Bool(Key)) return;
        var dialog = new ContentDialog
        {
            Title = Title, Content = new TextBlock { Text = Message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "OK", CloseButtonText = "Exit", DefaultButton = ContentDialogButton.Primary, XamlRoot = root,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) Application.Current.Exit();
    }
}
