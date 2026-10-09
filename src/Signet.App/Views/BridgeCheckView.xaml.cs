using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Signet.Models;
using Signet.Services;

namespace Signet.App.Views;

/// <summary>Windows-only, temporary: a live check of the shared Taquito bridge. See the XAML.</summary>
public sealed partial class BridgeCheckView : UserControl
{
    private readonly TaquitoBridge bridge = TaquitoBridge.Shared;
    private readonly DispatcherTimer logTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    public BridgeCheckView()
    {
        InitializeComponent();
        NetworkPicker.ItemsSource = Network.All;
        NetworkPicker.SelectedItem = Network.All[0];
        logTimer.Tick += (_, _) => RefreshLog();
        logTimer.Start();
        Loaded += async (_, _) =>
        {
            AddressChanged(AddressBox, null!);
            ShowWallet();
            try
            {
                var version = await bridge.Call("version");
                SubtitleText.Text = $"Port in progress: Taquito bridge {version.StringValue} running in V8";
            }
            catch (TaquitoBridge.BridgeError e) { SubtitleText.Text = e.Message; }
            await CheckNode();
            await LoadBalance();
        };
    }

    private Network SelectedNetwork => NetworkPicker.SelectedItem as Network ?? Network.Mainnet;

    private async void NetworkChanged(object sender, SelectionChangedEventArgs e)
    {
        NodeText.Text = SelectedNetwork.NodeDisplayText;
        if (IsLoaded)
        {
            await CheckNode();
            await LoadBalance();
        }
    }

    private async void CheckNodeClicked(object sender, RoutedEventArgs e) => await CheckNode();
    private async void BalanceClicked(object sender, RoutedEventArgs e) => await LoadBalance();

    private async Task CheckNode()
    {
        SetStatus(Colors.Gray, "Checking…");
        HeadText.Text = "";
        var clock = Stopwatch.StartNew();
        try
        {
            var head = await bridge.Call("getHead", SelectedNetwork.RpcURL.AbsoluteString());
            SetStatus(Colors.LimeGreen, $"Connected in {clock.ElapsedMilliseconds} ms");
            HeadText.Text = $"level  {head["level"]?.DoubleValue:0}\nblock  {head["hash"]?.StringValue}\nchain  {head["chainId"]?.StringValue}";
        }
        catch (TaquitoBridge.BridgeError ex)
        {
            SetStatus(Colors.IndianRed, "No connection");
            HeadText.Text = ex.Message;
        }
    }

    private void SetStatus(Windows.UI.Color color, string text)
    {
        StatusDot.Fill = new SolidColorBrush(color);
        StatusText.Text = text;
    }

    private void AddressChanged(object sender, TextChangedEventArgs e)
    {
        var address = new Address(AddressBox.Text.Trim());
        AddressInfo.Text = address.Value.Length == 0 ? ""
            : !address.IsValidAccount ? "Not a valid Tezos address"
            : address.Scheme is { } scheme ? $"{scheme.RawValue()} · {scheme.DisplayName()} · {address.Shortened()}"
            : $"Contract · {address.Shortened()}";
    }

    private async Task LoadBalance()
    {
        var address = new Address(AddressBox.Text.Trim());
        BreakdownList.Children.Clear();
        if (!address.IsValidAccount)
        {
            TotalText.Text = "";
            return;
        }
        TotalText.Text = "Loading…";
        try
        {
            var r = await bridge.Call("getTezBalances", SelectedNetwork.RpcURL.AbsoluteString(), address.Value);
            if (r["exists"]?.BoolValue == false)
            {
                TotalText.Text = "Key not found on chain";
                return;
            }
            var balance = TezBalance.FromMutez(r["spendable"]?.StringValue, r["staked"]?.StringValue,
                                               r["unstakedFrozen"]?.StringValue, r["unstakedFinalizable"]?.StringValue);
            if (balance is null)
            {
                TotalText.Text = "Unexpected reply from the bridge";
                return;
            }
            TotalText.Text = AssetBalance.Format(balance.Total, "tz");
            foreach (var detail in balance.Breakdown)
            {
                var row = new Grid { ColumnSpacing = 12 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.Children.Add(new TextBlock { Text = detail.Label, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
                var amount = new TextBlock { Text = AssetBalance.Format(detail.Amount, "tz") };
                Grid.SetColumn(amount, 1);
                row.Children.Add(amount);
                BreakdownList.Children.Add(row);
            }
        }
        catch (TaquitoBridge.BridgeError ex)
        {
            TotalText.Text = ex.Message;
        }
    }

    private static Brush Secondary => (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

    private static Grid Row(string left, string right, double leftWidth = 140)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(leftWidth) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(new TextBlock { Text = left, Foreground = Secondary, TextTrimming = TextTrimming.CharacterEllipsis });
        var value = new TextBlock { Text = right, FontFamily = new FontFamily("Consolas"), IsTextSelectionEnabled = true };
        Grid.SetColumn(value, 1);
        row.Children.Add(value);
        return row;
    }

    /// <summary>Lists the accounts in the wallet directory (read-only).</summary>
    private void ShowWallet()
    {
        var directory = new WalletDirectorySettings().Current;
        WalletDirText.Text = directory;
        AccountList.Children.Clear();
        try
        {
            var wallets = new TezosClientStore(directory).Load();
            if (wallets.Count == 0)
            {
                var octez = new TezosClientStore(TezosClientStore.OctezClientDirectory);
                AccountList.Children.Add(new TextBlock
                {
                    Text = octez.HasWallets
                        ? $"No accounts yet. {octez.Load().Count} found in {TezosClientStore.OctezClientDirectory} to import."
                        : "No accounts yet.",
                    TextWrapping = TextWrapping.Wrap,
                });
            }
            foreach (var wallet in wallets)
            {
                var kind = wallet.KeyKind switch
                {
                    KeyKind.Unencrypted => "key on disk",
                    KeyKind.Encrypted => "encrypted key",
                    KeyKind.Ledger => "Ledger",
                    KeyKind.Remote => "remote signer",
                    KeyKind.None => "watch only",
                    _ => "unknown",
                };
                AccountList.Children.Add(Row(wallet.Alias, $"{wallet.Address.Shortened()}  ({kind})"));
            }
        }
        catch (TezosClientStore.StoreError e)
        {
            AccountList.Children.Add(new TextBlock { Text = e.Message, TextWrapping = TextWrapping.Wrap });
        }
    }

    private async void GenerateClicked(object sender, RoutedEventArgs e)
    {
        GenerateButton.IsEnabled = false;
        KeyList.Children.Clear();
        var generator = new KeyGenerator();
        foreach (var scheme in AddressSchemeExtensions.AllCases.Where(s => s.IsSupported()))
        {
            try
            {
                var clock = Stopwatch.StartNew();
                var material = await generator.Generate(scheme);
                // Only public data is shown; the secret goes out of scope here.
                KeyList.Children.Add(Row($"{scheme.RawValue()} {scheme.DisplayName()}",
                                         $"{new Address(material.Address).Shortened()}  {clock.ElapsedMilliseconds} ms", 210));
            }
            catch (Exception ex) when (ex is KeyGenerator.KeyError or TaquitoBridge.BridgeError)
            {
                KeyList.Children.Add(Row(scheme.RawValue(), ex.Message, 210));
            }
        }
        GenerateButton.IsEnabled = true;
    }

    private void RefreshLog()
    {
        var lines = bridge.ConsoleLines;
        var text = string.Join("\n", lines.TakeLast(60));
        if (text == LogText.Text) return;
        LogText.Text = text;
        LogScroller.ChangeView(null, LogScroller.ScrollableHeight + 1000, null, disableAnimation: true);
    }
}
