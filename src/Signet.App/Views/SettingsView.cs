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
/// Windows: a dialog rather than the Mac's Settings window. Not ported yet: the Updates section
/// (Velopack instead of Sparkle) and the Developer section (its switch is for dApps).
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

        CloseButtonText = "Done";
        DefaultButton = ContentDialogButton.Close;
        model.PropertyChanged += Changed;
        Closed += (_, _) => model.PropertyChanged -= Changed;
        Update();
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
