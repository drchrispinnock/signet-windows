using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Signet.Models;
using Signet.Services;
using Signet.ViewModels;

namespace Signet.App.Views;

/// <summary>
/// Adds a key that lives on a Ledger: finds the device, checks the Tezos app is open, derives
/// the address for the chosen curve and account, and has the user approve it on the device.
/// </summary>
public sealed partial class ConnectLedgerSheet : Sheet
{
    private readonly WalletViewModel model;
    private readonly CancellationTokenSource polling = new();
    private readonly StackPanel deviceStatus = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBox alias = new() { PlaceholderText = "Ledger" };
    private readonly ComboBox curvePicker = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumberBox account = new() { Value = 0, Minimum = 0, Maximum = 100, SmallChange = 1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
    private readonly TextBlock path = new() { Style = (Style)Application.Current.Resources["MonoText"] };
    private readonly StackPanel addressArea = new() { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Right };
    private IReadOnlyList<LedgerDevice> devices = [];
    private LedgerAppInfo? appInfo;
    private string? appProblem;
    private Address? preview;
    private int previewVersion;
    private bool isAdding;

    public ConnectLedgerSheet(WalletViewModel model) : base("Connect Ledger", 520)
    {
        this.model = model;
        alias.Text = model.SuggestedAlias("Ledger");
        foreach (var curve in LedgerCurveExtensions.AllCases) curvePicker.Items.Add(curve.DisplayName());
        curvePicker.SelectedIndex = 0;

        Form.Children.Add(LabeledContent("Device", deviceStatus));
        Form.Children.Add(new TextBlock { Text = "Name" });
        Form.Children.Add(alias);
        Form.Children.Add(new TextBlock { Text = "Key type" });
        Form.Children.Add(curvePicker);
        Form.Children.Add(LabeledContent("Account", account));
        Form.Children.Add(LabeledContent("Path", path));
        Form.Children.Add(LabeledContent("Address", addressArea));
        Form.Children.Add(Note($"Press Add and the Ledger will show this address; approve it there to finish. The key stays on the device, which asks you to approve every operation. The entry is written to {WalletFolder(model)} as a ledger:// alias octez-client can use too."));

        CloseButtonText = "Cancel";
        Primary("Add", Add);
        alias.TextChanged += (_, _) => Validate();
        curvePicker.SelectionChanged += (_, _) => { DrawPath(); RefreshPreview(); };
        account.ValueChanged += (_, _) => { DrawPath(); RefreshPreview(); };
        // Approving on the device must not be abandoned with Esc.
        Closing += (_, args) => { if (isAdding) args.Cancel = true; };
        Opened += (_, _) => { alias.Focus(FocusState.Programmatic); _ = PollDevices(polling.Token); };
        Closed += (_, _) => polling.Cancel();
        DrawPath();
        DrawDevice();
        DrawAddress();
        Validate();
    }

    private LedgerDevice? Device => devices.FirstOrDefault();
    private bool Ready => Device is not null && appInfo?.IsWallet == true;
    private LedgerCurve Curve => LedgerCurveExtensions.AllCases[Math.Max(0, curvePicker.SelectedIndex)];
    private int Account => double.IsNaN(account.Value) ? 0 : (int)Math.Clamp(account.Value, 0, 100);
    private LedgerKey Key => new("", Curve, Account);
    private Wallet? Existing => preview is { } address ? model.Wallets.FirstOrDefault(w => w.Address == address) : null;

    protected override void Validate() =>
        IsPrimaryButtonEnabled = Ready && alias.Text.Trim().Length > 0 && !isAdding && Existing is null;

    private void DrawPath() => path.Text = Key.FullPath;

    private void DrawDevice()
    {
        deviceStatus.Children.Clear();
        var dot = new Ellipse { Width = 9, Height = 9, VerticalAlignment = VerticalAlignment.Center };
        deviceStatus.Children.Add(dot);
        if (Device is { } device)
        {
            dot.Fill = new SolidColorBrush(Ready ? Microsoft.UI.Colors.Green : Microsoft.UI.Colors.Orange);
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock { Text = device.Model, HorizontalAlignment = HorizontalAlignment.Right });
            var caption = new TextBlock { Style = (Style)Application.Current.Resources["CaptionSecondaryText"], HorizontalAlignment = HorizontalAlignment.Right, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Right };
            if (appInfo is { } info)
            {
                caption.Text = info.IsWallet ? $"Tezos Wallet {info.Version}" : $"Tezos Baking app {info.Version}: open the Tezos Wallet app instead";
                if (!info.IsWallet) caption.Foreground = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
            }
            else if (appProblem is { } problem)
            {
                caption.Text = problem;
                caption.Foreground = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
            }
            else
            {
                caption.Text = "Checking…";
            }
            text.Children.Add(caption);
            deviceStatus.Children.Add(text);
        }
        else
        {
            dot.Fill = new SolidColorBrush(Microsoft.UI.Colors.Red);
            deviceStatus.Children.Add(new TextBlock { Text = "No Ledger found. Plug it in, unlock it and open the Tezos app.", Foreground = Secondary, TextWrapping = TextWrapping.Wrap, MaxWidth = 300 });
        }
    }

    private void DrawAddress()
    {
        addressArea.Children.Clear();
        if (preview is { } address)
        {
            addressArea.Children.Add(new TextBlock { Text = address.Value, Style = (Style)Application.Current.Resources["MonoText"], IsTextSelectionEnabled = true, HorizontalAlignment = HorizontalAlignment.Right });
            if (Existing is { } existing)
                addressArea.Children.Add(Label($"Already in your list as “{existing.Alias}”", "", "SystemFillColorCautionBrush"));
        }
        else if (Ready)
        {
            addressArea.Children.Add(new ProgressRing { Width = 16, Height = 16, IsActive = true });
        }
        else
        {
            addressArea.Children.Add(new TextBlock { Text = "—", Foreground = Secondary });
        }
        Validate();
    }

    /// <summary>Watches for the device and the app while the sheet is up.</summary>
    private async Task PollDevices(CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            var found = await model.Ledger.Devices();
            if (cancel.IsCancellationRequested) return;
            var changed = !found.SequenceEqual(devices);
            devices = found;
            if (Device is { } device)
            {
                if ((changed || appInfo is null) && !isAdding)
                {
                    try
                    {
                        var info = await model.Ledger.AppInfo(device.Id);
                        var becameReady = info != appInfo;
                        appInfo = info;
                        appProblem = null;
                        if (becameReady) RefreshPreview();
                    }
                    catch (Exception e)
                    {
                        appInfo = null;
                        appProblem = e is ChainError ? e.Message : "Open the Tezos app on the Ledger.";
                        preview = null;
                        DrawAddress();
                    }
                }
            }
            else
            {
                appInfo = null;
                appProblem = null;
                preview = null;
                DrawAddress();
            }
            DrawDevice();
            Validate();
            try { await Task.Delay(TimeSpan.FromSeconds(1.5), cancel); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>The address for the chosen curve and account, read without a prompt on the device.</summary>
    private async void RefreshPreview()
    {
        var version = ++previewVersion;
        preview = null;
        DrawAddress();
        if (Device is not { } device || !Ready || isAdding) return;
        Address? result = null;
        try { result = (await model.Ledger.AddressFor(device.Id, Key, prompt: false)).Address; }
        catch (Exception) { }
        if (version != previewVersion) return;
        preview = result;
        DrawAddress();
    }

    private async Task Add()
    {
        if (Device is not { } device) return;
        isAdding = true;
        Validate();
        ShowProgress("Approve the address on your Ledger…");
        try
        {
            await model.ConnectLedger(alias.Text.Trim(), device.Id, Curve, Account);
        }
        finally
        {
            isAdding = false;
            ShowProgress(null);
        }
    }
}
