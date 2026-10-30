using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Signet.App.Services;
using Signet.Models;
using Signet.ViewModels;
using Windows.System;

namespace Signet.App.Views;

/// <summary>
/// Left side of the header: alias (a dropdown that switches wallets), shortened address with a
/// copy button, then any Tezos Domains names.
/// </summary>
public sealed partial class WalletIdentityView : UserControl
{
    private WalletViewModel? model;

    public WalletIdentityView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => WrapTags();
    }

    /// <summary>Moves the tags under the name when name and tags do not fit side by side.</summary>
    private void WrapTags()
    {
        var host = WrappedTags.Children.Count > 0 ? WrappedTags : Tags;
        var tags = host.Children.ToList();
        host.Children.Clear();
        Picker.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var tagsWidth = tags.Sum(t => { t.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity)); return t.DesiredSize.Width + 6; });
        var available = ActualWidth - Avatar.ActualWidth - 12;
        var wrap = tags.Count > 0 && Picker.DesiredSize.Width + 8 + tagsWidth > available;
        var target = wrap ? WrappedTags : Tags;
        foreach (var tag in tags) target.Children.Add(tag);
        WrappedTags.Visibility = wrap ? Visibility.Visible : Visibility.Collapsed;
    }

    public WalletViewModel Model
    {
        get => model!;
        set
        {
            model = value;
            model.PropertyChanged += Changed;
            Update();
        }
    }

    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WalletViewModel.SelectedWallet) or nameof(WalletViewModel.Wallets) or nameof(WalletViewModel.Domains)
            or nameof(WalletViewModel.SessionWallets))
            Update();
    }

    private void Update()
    {
        if (model?.SelectedWallet is not { } wallet) return;
        Avatar.Address = wallet.Address;
        AliasText.Text = wallet.Alias;
        AutomationProperties.SetName(Picker, $"Account {wallet.Alias}. Switch account");
        AddressText.Text = wallet.Address.Shortened();
        ToolTipService.SetToolTip(AddressText, wallet.Address.Value);

        // The tags sit beside the menu, not in its label.
        Tags.Children.Clear();
        WrappedTags.Children.Clear();
        if (wallet.Address.IsContract) Tags.Children.Add(TagView("Contract", ""));
        else if (wallet.KeyKind == KeyKind.None) Tags.Children.Add(TagView("Watch only", "", "Signet has no key for this address; you can watch it and send to it."));
        if (wallet.KeyKind == KeyKind.Ledger)
            Tags.Children.Add(TagView("Ledger", "", $"This key lives on a Ledger{(wallet.LedgerKey is { } key ? $" ({key.FullPath})" : "")}. Operations are approved on the device."));
        if (wallet.IsSessionOnly) Tags.Children.Add(SessionTag());
        if (ActualWidth > 0) WrapTags();

        DomainList.Children.Clear();
        foreach (var domain in model.Domains)
            DomainList.Children.Add(new TextBlock { Text = domain, Style = (Style)Application.Current.Resources["SecondaryText"] });

        BuildPicker(wallet);
    }

    private void BuildPicker(Wallet current)
    {
        PickerMenu.Items.Clear();
        var mine = model!.Wallets.Where(w => w.KeyKind != KeyKind.None).ToList();
        var book = model.Wallets.Where(w => w.KeyKind == KeyKind.None && !w.Address.IsContract).ToList();
        var contracts = model.Wallets.Where(w => w.Address.IsContract).ToList();
        Section("My accounts", mine);
        Section("Address book", book);
        Section("Contracts", contracts);

        void Section(string title, List<Wallet> wallets)
        {
            if (wallets.Count == 0) return;
            if (PickerMenu.Items.Count > 0) PickerMenu.Items.Add(new MenuFlyoutSeparator());
            PickerMenu.Items.Add(new MenuFlyoutItem { Text = title, IsEnabled = false });
            foreach (var wallet in wallets)
            {
                var item = new ToggleMenuFlyoutItem { Text = $"{wallet.Alias}  {wallet.Address.Shortened()}", IsChecked = wallet.Id == current.Id };
                item.Click += (_, _) => model.Select(wallet);
                PickerMenu.Items.Add(item);
            }
        }
    }

    private static Border TagView(string text, string glyph, string? help = null)
    {
        var border = new Border { Style = (Style)Application.Current.Resources["TagBorder"] };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 10, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
        row.Children.Add(new TextBlock { Text = text, FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
        border.Child = row;
        if (help is not null) ToolTipService.SetToolTip(border, help);
        return border;
    }

    /// <summary>Orange, unlike the other tags: this key is lost at quit unless Remember key is used.</summary>
    private static Border SessionTag()
    {
        var orange = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
        var border = new Border
        {
            CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 1, 7, 2), VerticalAlignment = VerticalAlignment.Center,
            Background = (Brush)Application.Current.Resources["SystemFillColorCautionBackgroundBrush"], BorderBrush = orange, BorderThickness = new Thickness(1),
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        row.Children.Add(new FontIcon { Glyph = "", FontSize = 10, Foreground = orange });
        row.Children.Add(new TextBlock { Text = "Session only", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = orange });
        border.Child = row;
        ToolTipService.SetToolTip(border, "A paper key held in memory only. It is not in the wallet directory or any backup and is forgotten when Signet quits. Use Remember key… in the menu to keep it.");
        return border;
    }

    private async void CopyAddress(object sender, RoutedEventArgs e)
    {
        if (model?.SelectedWallet is not { } wallet) return;
        Pasteboard.SetString(wallet.Address.Value);
        CopyIcon.Glyph = "";
        await Task.Delay(1500);
        CopyIcon.Glyph = "";
    }
}

/// <summary>
/// The one list of account actions for the burger menu (upstream also puts it in the menu bar's
/// Operations menu; a Windows app has no menu bar, so the shortcuts live here). Items whose
/// screens are not ported yet are shown disabled so the menu matches the Mac's.
/// </summary>
public static class OperationsMenuItems
{
    private const string NotYet = "Not available on Windows yet";

    /// <param name="openSettings">Upstream opens the Settings scene with <c>SettingsLink</c>; here the home view shows the dialog.</param>
    public static void Fill(MenuFlyout menu, WalletViewModel model, Action openSettings)
    {
        menu.Items.Clear();
        var hasWallet = model.SelectedWallet is not null;
        menu.Items.Add(Item("Create account…", () => model.IsPresentingCreateWallet = true, VirtualKey.N, VirtualKeyModifiers.Control));
        menu.Items.Add(Item("Import account…", () => model.IsPresentingImportAccount = true, VirtualKey.I, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift));
        menu.Items.Add(Item("Connect Ledger…", null, help: NotYet));
        menu.Items.Add(Item("Rename account…", () => model.IsPresentingRenameWallet = true, enabled: hasWallet));
        menu.Items.Add(Item("Export secret key…", () => model.IsPresentingExportKey = true, enabled: model.CanExportSelectedKey));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Add address…", () => model.IsPresentingAddAddress = true, VirtualKey.N, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift));
        if (model.CanRememberSelectedKey) menu.Items.Add(Item("Remember key…", () => model.IsPresentingRememberKey = true));
        menu.Items.Add(Item("Forget account…", () => model.IsPresentingForget = true, enabled: hasWallet));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("dApps…", () => model.IsPresentingConnectDApp = true, VirtualKey.D, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Create multisig…", null, help: NotYet));
        menu.Items.Add(Item("Add multisig…", null, help: NotYet));
        menu.Items.Add(Item("Sign multisig transaction…", null, help: NotYet));
        menu.Items.Add(Item("Submit multisig transaction…", null, help: NotYet));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Baking…", null, help: NotYet));
        if (model.CanGovern) menu.Items.Add(Item("Governance…", null, help: NotYet));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Networks…", () => model.IsPresentingNetworks = true));
        // Upstream's "Shielded settings…" belongs with TzEL, which this port leaves out.
        var settings = Item("Settings…", openSettings);
        settings.KeyboardAcceleratorTextOverride = "Ctrl+,";
        menu.Items.Add(settings);
        menu.Items.Add(Item("Refresh", () => _ = model.Refresh(), VirtualKey.R, VirtualKeyModifiers.Control, hasWallet));
    }

    private static MenuFlyoutItem Item(string text, Action? action, VirtualKey key = VirtualKey.None, VirtualKeyModifiers modifiers = VirtualKeyModifiers.None,
                                       bool enabled = true, string? help = null)
    {
        var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled && action is not null };
        if (action is not null) item.Click += (_, _) => action();
        // The shortcut itself is installed on the window (InstallShortcuts); the item only shows it.
        if (key != VirtualKey.None)
            item.KeyboardAcceleratorTextOverride = (modifiers.HasFlag(VirtualKeyModifiers.Control) ? "Ctrl+" : "") +
                                                   (modifiers.HasFlag(VirtualKeyModifiers.Shift) ? "Shift+" : "") + key;
        if (help is not null) ToolTipService.SetToolTip(item, help);
        return item;
    }

    /// <summary>
    /// The burger menu's shortcuts, live while the window is open even when the menu is closed
    /// (the Mac's menu bar keeps them reachable the same way).
    /// </summary>
    public static void InstallShortcuts(UIElement root, WalletViewModel model, Action openSettings)
    {
        void Add(VirtualKey key, VirtualKeyModifiers modifiers, Action action, Func<bool>? enabled = null)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += (_, args) =>
            {
                args.Handled = true;
                if (enabled?.Invoke() ?? true) action();
            };
            root.KeyboardAccelerators.Add(accelerator);
        }
        Add(VirtualKey.N, VirtualKeyModifiers.Control, () => model.IsPresentingCreateWallet = true);
        Add(VirtualKey.I, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => model.IsPresentingImportAccount = true);
        Add(VirtualKey.N, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => model.IsPresentingAddAddress = true);
        Add(VirtualKey.D, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => model.IsPresentingConnectDApp = true);
        Add(VirtualKey.R, VirtualKeyModifiers.Control, () => _ = model.Refresh(), () => model.SelectedWallet is not null);
        Add((VirtualKey)188, VirtualKeyModifiers.Control, openSettings);  // Ctrl+, (VK_OEM_COMMA), as on the Mac
        root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
    }
}
