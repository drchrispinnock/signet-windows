using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;
using Signet.App.Services;
using Signet.Services;
using Signet.ViewModels;

namespace Signet.App.Views;

/// <summary>
/// App settings: appearance, where the wallet files live and how they are backed up. Nodes are
/// edited in <c>NetworksSheet</c>.
///
/// Windows: a dialog rather than the Mac's Settings window, with Velopack behind the Updates
/// section instead of Sparkle. Not ported yet: the Developer section (its switch is for dApps).
/// </summary>
public sealed partial class SettingsView : Sheet
{
    private readonly WalletViewModel model;
    private readonly StackPanel appearance = new() { Orientation = Orientation.Horizontal };
    private readonly ToggleSwitch showsDisclaimer = new() { Header = "Show the disclaimer at launch" };
    private readonly TextBlock walletPath = PathText();
    private readonly Button walletDefault = new() { Content = "Use Default" };
    private readonly TextBlock backupPath = PathText();
    private readonly Button backupDefault = new() { Content = "Use Default" };
    private readonly NumberBox generations = new()
    {
        Header = "Generations to keep", Minimum = BackupSettings.MinGenerations, Maximum = BackupSettings.MaxGenerations,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 140,
    };
    private readonly TextBlock lastBackup = new() { Style = (Style)Application.Current.Resources["SecondaryText"], VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

    public SettingsView(WalletViewModel model) : base("Settings", 520)
    {
        this.model = model;
        RequestedTheme = AppearanceApplier.Current;

        // Appearance: one row, OS · Light · Dark, as the Mac's segmented picker.
        var stored = AppearanceExtensions.Stored();
        foreach (var option in AppearanceExtensions.AllCases)
        {
            var radio = new RadioButton { Content = option.Title(), GroupName = "appearance", IsChecked = option == stored, MinWidth = 0, Margin = new Thickness(0, 0, 16, 0) };
            radio.Checked += (_, _) =>
            {
                AppearanceApplier.Set(option, XamlRoot);
                RequestedTheme = AppearanceApplier.Theme(option);
            };
            appearance.Children.Add(radio);
        }
        showsDisclaimer.IsOn = UserDefaults.Standard.Object(Disclaimer.Key) is null || UserDefaults.Standard.Bool(Disclaimer.Key);
        showsDisclaimer.Toggled += (_, _) => UserDefaults.Standard.Set(Disclaimer.Key, showsDisclaimer.IsOn);
        Form.Children.Add(new TextBlock { Text = "Appearance" });
        Form.Children.Add(appearance);
        Form.Children.Add(showsDisclaimer);

        // Wallet directory
        if (model.WalletDirectory is not null)
        {
            Form.Children.Add(new SectionHeader("Wallet directory"));
            Form.Children.Add(walletPath);
            Form.Children.Add(Buttons(
                MakeButton("Show in Explorer", () => Reveal(model.WalletDirectory)),
                walletDefault,
                MakeButton("Choose…", async () => { if (await ChooseFolder() is { } path) model.ChangeWalletDirectory(path); })));
            walletDefault.Click += (_, _) => { if (model.DefaultWalletDirectory is { } fallback) model.ChangeWalletDirectory(fallback); };
            Form.Children.Add(Note("Wallet files are kept here in octez-client's format (public_key_hashs, public_keys, secret_keys). Choosing another directory switches to the wallets in it; nothing is moved or copied."));
        }

        // Backups
        if (model.BackupDirectory is not null)
        {
            Form.Children.Add(new SectionHeader("Backup folder"));
            Form.Children.Add(backupPath);
            Form.Children.Add(Buttons(
                MakeButton("Show in Explorer", () => Reveal(model.BackupDirectory)),
                backupDefault,
                MakeButton("Choose…", async () => { if (await ChooseFolder() is { } path) model.SetBackupDirectory(path); })));
            backupDefault.Click += (_, _) => { if (model.DefaultBackupDirectory is { } fallback) model.SetBackupDirectory(fallback); };
            generations.Value = model.BackupGenerations;
            generations.ValueChanged += (_, args) =>
            {
                if (!double.IsNaN(args.NewValue) && (int)args.NewValue != model.BackupGenerations) model.SetBackupGenerations((int)args.NewValue);
            };
            Form.Children.Add(generations);
            var last = new Grid { ColumnSpacing = 12 };
            last.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            last.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var backUpNow = MakeButton("Back Up Now", async () => await model.BackUp(force: true));
            Grid.SetColumn(backUpNow, 1);
            last.Children.Add(lastBackup);
            last.Children.Add(backUpNow);
            Form.Children.Add(last);
            Form.Children.Add(Note("The wallet files are copied here on every launch and after any key is created or renamed. Automatic copies are skipped when nothing has changed; Back Up Now always writes one. Only the newest generations are kept."));
        }

        UpdatesSection();

        CloseButtonText = "Done";
        DefaultButton = ContentDialogButton.Close;
        model.PropertyChanged += Changed;
        updater.PropertyChanged += UpdaterChanged;
        Closed += (_, _) =>
        {
            model.PropertyChanged -= Changed;
            updater.PropertyChanged -= UpdaterChanged;
        };
        Update();
    }

    // ---- Updates (upstream's updatesSection, Velopack for Sparkle) ----------------------------------

    private readonly UpdaterService updater = UpdaterService.Shared;
    private readonly ToggleSwitch automaticChecks = new() { Header = "Check for updates automatically" };
    private readonly ToggleSwitch automaticDownloads = new() { Header = "Download and install them automatically" };
    private readonly TextBlock lastChecked = new() { Style = (Style)Application.Current.Resources["SecondaryText"], VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly Button checkNow = new() { Content = "Check Now" };
    private readonly StackPanel updateStatus = new() { Spacing = 8 };

    private void UpdatesSection()
    {
        Form.Children.Add(new SectionHeader("Updates"));
        if (!updater.IsAvailable)
        {
            Form.Children.Add(Note($"This is Signet {updater.CurrentVersion}. Updates work in the installed copy of Signet (the Setup or Portable download from GitHub); this copy was not installed that way."));
            return;
        }
        automaticChecks.IsOn = updater.AutomaticallyChecksForUpdates;
        automaticDownloads.IsOn = updater.AutomaticallyDownloadsUpdates;
        automaticChecks.Toggled += (_, _) => { updater.AutomaticallyChecksForUpdates = automaticChecks.IsOn; UpdateUpdates(); };
        automaticDownloads.Toggled += (_, _) => updater.AutomaticallyDownloadsUpdates = automaticDownloads.IsOn;
        checkNow.Click += async (_, _) => await updater.CheckForUpdates();
        Form.Children.Add(automaticChecks);
        Form.Children.Add(automaticDownloads);
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(checkNow, 1);
        row.Children.Add(lastChecked);
        row.Children.Add(checkNow);
        Form.Children.Add(row);
        Form.Children.Add(updateStatus);
        Form.Children.Add(Note($"This is Signet {updater.CurrentVersion}. New releases are fetched from GitHub over HTTPS and installed when you restart Signet. Only Signet itself is replaced; your accounts stay in the wallet folder."));
        UpdateUpdates();
    }

    private void UpdaterChanged(object? sender, PropertyChangedEventArgs e) => DispatcherQueue.TryEnqueue(UpdateUpdates);

    private void UpdateUpdates()
    {
        if (!updater.IsAvailable) return;
        automaticDownloads.IsEnabled = automaticChecks.IsOn;
        lastChecked.Text = "Last checked: " + (updater.LastUpdateCheckDate?.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture) ?? "Never");
        checkNow.IsEnabled = updater.CanCheckForUpdates;
        updateStatus.Children.Clear();
        switch (updater.State)
        {
            case UpdaterService.UpdateState.Checking:
                updateStatus.Children.Add(Note("Checking…"));
                break;
            case UpdaterService.UpdateState.UpToDate:
                updateStatus.Children.Add(Note($"Signet {updater.CurrentVersion} is the latest version."));
                break;
            case UpdaterService.UpdateState.Available available:
                updateStatus.Children.Add(Note($"Signet {available.Version} is available."));
                updateStatus.Children.Add(Buttons(MakeButton("Download Update", async () => await updater.Download())));
                break;
            case UpdaterService.UpdateState.Downloading downloading:
                updateStatus.Children.Add(Note($"Downloading Signet {downloading.Version}… {downloading.Percent}%"));
                break;
            case UpdaterService.UpdateState.Ready ready:
                updateStatus.Children.Add(Note($"Signet {ready.Version} is ready. It is installed when Signet restarts."));
                updateStatus.Children.Add(Buttons(MakeButton("Restart to Update", updater.RestartToUpdate)));
                break;
            case UpdaterService.UpdateState.Failed failed:
                updateStatus.Children.Add(new TextBlock { Text = failed.Message, Style = (Style)Application.Current.Resources["ErrorText"], TextWrapping = TextWrapping.Wrap });
                break;
        }
    }

    private void Changed(object? sender, PropertyChangedEventArgs e) => Update();

    private void Update()
    {
        walletPath.Text = model.WalletDirectory ?? "";
        walletDefault.IsEnabled = !model.IsUsingDefaultWalletDirectory;
        backupPath.Text = model.BackupDirectory ?? "";
        backupDefault.IsEnabled = !model.IsUsingDefaultBackupDirectory;
        lastBackup.Text = $"Last backup: {LastBackupDescription}";
    }

    private string LastBackupDescription
    {
        get
        {
            if (model.LastBackupError is { } error) return $"Failed: {error}";
            if (model.LastBackup is not { } last) return "Not yet";
            var when = last.Date.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture);
            return last.Outcome switch
            {
                WalletBackup.Outcome.Created => $"Saved {when}",
                WalletBackup.Outcome.Unchanged => $"Up to date ({when})",
                _ => "No wallet files yet",
            };
        }
    }

    private static TextBlock PathText() => new()
    {
        Style = (Style)Application.Current.Resources["MonoText"], IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap,
    };

    private static Button MakeButton(string text, Action action)
    {
        var button = new Button { Content = text };
        button.Click += (_, _) => action();
        return button;
    }

    private static StackPanel Buttons(params Button[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var button in buttons) row.Children.Add(button);
        return row;
    }

    /// <summary>Opens the folder in File Explorer (upstream: Show in Finder), creating it first if need be.</summary>
    private static void Reveal(string? path)
    {
        if (path is null) return;
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private async Task<string?> ChooseFolder()
    {
        var picker = new FolderPicker(XamlRoot.ContentIslandEnvironment.AppWindowId) { CommitButtonText = "Use This Folder" };
        var result = await picker.PickSingleFolderAsync();
        return result?.Path;
    }
}
