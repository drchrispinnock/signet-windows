using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Signet.Models;
using Signet.Services;
using Signet.ViewModels;

namespace Signet.App.Views;

/// <summary>Pair with a dApp by pasting its Octez Connect code, and manage existing connections.</summary>
public sealed partial class ConnectDAppSheet : Sheet
{
    private readonly WalletViewModel model;
    private readonly DAppConnectionManager dapps;
    private readonly Ellipse statusLight = new() { Width = 8, Height = 8 };
    private readonly TextBlock statusText = new() { Style = (Style)Application.Current.Resources["CaptionSecondaryText"], VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel startFailure = new() { Spacing = 8 };
    // Masked like a password: whoever holds the code can pair their own wallet
    // with that dApp session, so it should not sit readable on screen.
    private readonly PasswordBox code = new() { PlaceholderText = "Paste the code from the dApp" };
    private readonly Button pairButton = new() { Content = "Pair", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
    private readonly ProgressRing pairing = new() { Width = 16, Height = 16, IsActive = true, Visibility = Visibility.Collapsed };
    private readonly StackPanel connected = new() { Spacing = 10 };
    private readonly StackPanel pairingState = new();
    private readonly TextBlock message = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private bool isPairing;

    public ConnectDAppSheet(WalletViewModel model) : base("Connect to a dApp", 520)
    {
        this.model = model;
        dapps = model.Dapps;

        var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        statusLight.VerticalAlignment = VerticalAlignment.Center;
        status.Children.Add(statusLight);
        status.Children.Add(statusText);
        Form.Children.Add(status);
        Form.Children.Add(startFailure);

        Form.Children.Add(new TextBlock { Text = "Pairing code" });
        Form.Children.Add(code);
        Form.Children.Add(Note("In the dApp choose Octez Connect (or Beacon), then “Pair wallet on another device” and copy the code. Signet connects over the Tezos relay; the dApp can be in any browser."));
        var pairRow = new Grid { ColumnSpacing = 8 };
        pairRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pairRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pairRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pairRow.Children.Add(pairing);
        Grid.SetColumn(pairButton, 2);
        pairRow.Children.Add(pairButton);
        Form.Children.Add(pairRow);

        Form.Children.Add(new TextBlock { Text = "Connected dApps", Style = (Style)Application.Current.Resources["SheetTitle"], Margin = new Thickness(0, 8, 0, 0) });
        Form.Children.Add(connected);
        Form.Children.Add(pairingState);
        Form.Children.Add(message);

        CloseButtonText = "Done";
        DefaultButton = ContentDialogButton.Close;
        pairButton.Click += (_, _) => Pair();
        code.PasswordChanged += (_, _) => Validate();
        dapps.PropertyChanged += Changed;
        model.PropertyChanged += ModelChanged;
        Closed += (_, _) =>
        {
            dapps.PropertyChanged -= Changed;
            model.PropertyChanged -= ModelChanged;
        };
        Opened += async (_, _) =>
        {
            TakePendingCode();
            await dapps.ReloadPermissions();
        };
        UpdateStatus();
        UpdateConnected();
        UpdatePairing();
        Validate();
    }

    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DAppConnectionManager.IsStarted) or nameof(DAppConnectionManager.LastError):
                UpdateStatus();
                break;
            case nameof(DAppConnectionManager.Permissions):
                UpdateConnected();
                break;
            case nameof(DAppConnectionManager.Pairing):
                UpdatePairing();
                break;
            case nameof(DAppConnectionManager.Current):
                // Only one sheet can be up at a time: step aside as soon as the dApp's first request
                // arrives, so the approval sheet can show without the user having to close this one.
                if (dapps.Current is not null) Hide();
                break;
        }
    }

    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WalletViewModel.PendingPairingCode)) TakePendingCode();
    }

    protected override void Validate() =>
        pairButton.IsEnabled = !isPairing && code.Password.Trim().Length > 0;

    private void UpdateStatus()
    {
        var failed = dapps.LastError is not null;
        statusLight.Fill = new SolidColorBrush(dapps.IsStarted ? Microsoft.UI.Colors.Green : failed ? Microsoft.UI.Colors.Red : Microsoft.UI.Colors.Gray);
        statusText.Text = dapps.IsStarted ? "Octez Connect running" : failed ? "Octez Connect failed" : "Starting…";
        ToolTipService.SetToolTip(statusText, dapps.LastError ?? "Connected to the Tezos relay");
        startFailure.Children.Clear();
        if (dapps.LastError is { } error && !dapps.IsStarted)
        {
            startFailure.Children.Add(new TextBlock { Text = error, Style = (Style)Application.Current.Resources["ErrorText"] });
            var retry = new Button { Content = "Try again" };
            retry.Click += async (_, _) => await dapps.Restart();
            startFailure.Children.Add(retry);
        }
    }

    private void UpdateConnected()
    {
        connected.Children.Clear();
        if (dapps.Permissions.Count == 0)
        {
            connected.Children.Add(new TextBlock { Text = "None yet.", Style = (Style)Application.Current.Resources["SecondaryText"] });
            return;
        }
        foreach (var permission in dapps.Permissions)
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new DAppIconView(permission.AppIcon, 28));
            var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = permission.AppName });
            text.Children.Add(new TextBlock
            {
                Text = $"{WalletName(permission.Address)} · {permission.NetworkType} · {string.Join(", ", permission.Scopes)}",
                Style = (Style)Application.Current.Resources["CaptionSecondaryText"], TextWrapping = TextWrapping.Wrap,
            });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            var disconnect = new Button { Content = "Disconnect", VerticalAlignment = VerticalAlignment.Center };
            disconnect.Click += async (_, _) => await dapps.Disconnect(permission);
            Grid.SetColumn(disconnect, 2);
            row.Children.Add(disconnect);
            connected.Children.Add(row);
        }
        var all = new Button { Content = "Disconnect all", Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"] };
        all.Click += async (_, _) => await dapps.DisconnectAll();
        connected.Children.Add(all);
    }

    private void UpdatePairing()
    {
        pairingState.Children.Clear();
        switch (dapps.Pairing)
        {
            case DAppConnectionManager.PairingState.Waiting waiting:
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                row.Children.Add(new ProgressRing { Width = 16, Height = 16, IsActive = true });
                row.Children.Add(new TextBlock { Text = $"Pairing with {waiting.Name}… waiting for the dApp to answer over the relay.", Style = (Style)Application.Current.Resources["SecondaryText"], TextWrapping = TextWrapping.Wrap });
                pairingState.Children.Add(row);
                break;
            case DAppConnectionManager.PairingState.Paired paired:
                pairingState.Children.Add(new TextBlock
                {
                    Text = $"Paired with {paired.Name}. It will now ask for permission; approve it in the next window.",
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.Green), TextWrapping = TextWrapping.Wrap,
                });
                break;
            case DAppConnectionManager.PairingState.Failed failed:
                pairingState.Children.Add(new TextBlock { Text = failed.Reason, Style = (Style)Application.Current.Resources["ErrorText"] });
                break;
        }
    }

    private string WalletName(string address) =>
        model.Wallets.FirstOrDefault(w => w.Address.Value == address)?.Alias ?? new Address(address).Shortened();

    /// <summary>A code that arrived through a signet:// link is pasted and paired automatically.</summary>
    private void TakePendingCode()
    {
        if (model.PendingPairingCode is not { } pending) return;
        model.PendingPairingCode = null;
        code.Password = pending;
        Pair();
    }

    private async void Pair()
    {
        isPairing = true;
        pairing.Visibility = Visibility.Visible;
        message.Visibility = Visibility.Collapsed;
        Validate();
        try
        {
            await dapps.Pair(code.Password.Trim());
            code.Password = "";
        }
        catch (Exception e)
        {
            message.Text = e.Message;
            message.Style = (Style)Application.Current.Resources["ErrorText"];
            message.Visibility = Visibility.Visible;
        }
        finally
        {
            isPairing = false;
            pairing.Visibility = Visibility.Collapsed;
            Validate();
        }
    }
}

/// <summary>
/// dApp icon or a generic placeholder. The icon's URL is the dApp's choice, so it is fetched like
/// upstream's artwork: public HTTPS only, through <c>PublicHTTPClient</c>, at most 1 MB.
/// </summary>
public sealed partial class DAppIconView : Grid
{
    private const int MaxBytes = 1024 * 1024;

    public DAppIconView(Uri? url, double size = 44)
    {
        Width = Height = size;
        CornerRadius = new CornerRadius(size * 0.22);
        Background = (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"];
        Children.Add(new FontIcon { Glyph = "", FontSize = size * 0.5, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
        if (url is not null && EndpointPolicy.IsPublicHTTPS(url)) _ = Load(url, size);
    }

    private async Task Load(Uri url, double size)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var response = await PublicHTTPClient.Shared.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > MaxBytes) return;
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token)) > 0)
            {
                if (buffer.Length + read > MaxBytes) return;
                buffer.Write(chunk, 0, read);
            }
            buffer.Position = 0;
            var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage { DecodePixelWidth = (int)(size * 2) };
            await bitmap.SetSourceAsync(buffer.AsRandomAccessStream());
            Children.Clear();
            Children.Add(new Image { Source = bitmap, Stretch = Stretch.Uniform });
        }
        catch (Exception)
        {
            // The placeholder stays.
        }
    }
}
