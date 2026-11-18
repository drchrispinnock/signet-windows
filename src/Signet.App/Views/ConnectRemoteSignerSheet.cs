using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Signet.Models;
using Signet.Services;
using Signet.ViewModels;

namespace Signet.App.Views;

/// <summary>
/// Connect Remote Signer: an account whose key a remote signer holds (octez-signer, Signatory,
/// Russignol: anything speaking octez-signer's protocol). The user picks how the signer listens
/// (HTTP, HTTPS or TCP, with octez-signer's default ports), where, and which of its keys; Signet
/// asks the signer for that key's public key, derives the address from it and adds the account
/// only when the two agree.
/// </summary>
public sealed partial class ConnectRemoteSignerSheet : Sheet
{
    private static readonly RemoteSignerLocator.TransportKind[] Transports =
        [RemoteSignerLocator.TransportKind.Http, RemoteSignerLocator.TransportKind.Https, RemoteSignerLocator.TransportKind.Tcp];

    private readonly WalletViewModel model;
    private readonly SelectorBar transportPicker = new();
    private readonly TextBox host = new() { Header = "Host", Text = "localhost", PlaceholderText = "localhost", IsSpellCheckEnabled = false };
    private readonly TextBox portBox = new() { Header = "Port", IsSpellCheckEnabled = false };
    private readonly StackPanel hostNote = new();
    private readonly TextBox addressBox = new() { Header = "Key", PlaceholderText = "tz1…, tz2…, tz3… or tz4…", IsSpellCheckEnabled = false, FontFamily = new FontFamily("Cascadia Mono, Consolas") };
    private readonly Button listButton = new() { Content = "List Keys", VerticalAlignment = VerticalAlignment.Bottom };
    private readonly DropDownButton keysButton = new() { Content = "Keys", VerticalAlignment = VerticalAlignment.Bottom, Visibility = Visibility.Collapsed };
    private readonly TextBlock listNote = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly StackPanel addressNote = new();
    private readonly StackPanel checkRow = new() { Spacing = 8 };
    private readonly TextBox alias = new() { Header = "Name", PlaceholderText = "e.g. Baker" };
    private readonly TextBlock aliasNote = new() { Text = "An account or address is already called that.", Visibility = Visibility.Collapsed };
    private readonly StackPanel tcpNote = new();

    private RemoteSignerLocator.TransportKind transport = RemoteSignerLocator.TransportKind.Tcp;
    private IReadOnlyList<Address>? listedKeys;
    private bool isListing;
    private WalletViewModel.RemoteSignerCheck? check;
    private string? checkError;
    private bool isChecking;
    private CancellationTokenSource? checking;

    public ConnectRemoteSignerSheet(WalletViewModel model) : base("Connect Remote Signer", 560)
    {
        this.model = model;
        portBox.Text = transport.DefaultPort()!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        portBox.PlaceholderText = portBox.Text;
        aliasNote.Style = (Style)Application.Current.Resources["ErrorText"];
        listNote.Foreground = Secondary;

        foreach (var each in Transports)
            transportPicker.Items.Add(new SelectorBarItem { Text = each.Title(), Tag = each, IsSelected = each == transport });
        transportPicker.SelectionChanged += (sender, _) =>
        {
            if (sender.SelectedItem?.Tag is not RemoteSignerLocator.TransportKind picked || picked == transport) return;
            var old = transport;
            transport = picked;
            // Keep a port the user typed; follow the defaults otherwise.
            if (portBox.Text.Length == 0 || portBox.Text == old.DefaultPort()?.ToString(System.Globalization.CultureInfo.InvariantCulture))
                portBox.Text = picked.DefaultPort()?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
            portBox.PlaceholderText = picked.DefaultPort()?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
            ConnectionChanged();
        };
        host.TextChanged += (_, _) => ConnectionChanged();
        portBox.TextChanged += (_, _) => ConnectionChanged();
        addressBox.TextChanged += (_, _) => KeyChanged();
        alias.TextChanged += (_, _) => Validate();
        listButton.Click += async (_, _) => await ListKeys();

        Form.Children.Add(new TextBlock { Text = "Connection", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        Form.Children.Add(transportPicker);
        var hostRow = new Grid { ColumnSpacing = 8 };
        hostRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hostRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        Grid.SetColumn(portBox, 1);
        hostRow.Children.Add(host);
        hostRow.Children.Add(portBox);
        Form.Children.Add(hostRow);
        Form.Children.Add(hostNote);
        Form.Children.Add(new TextBlock
        {
            Text = $"The usual ports are {RemoteSignerLocator.TransportKind.Http.DefaultPort()} for HTTP, {RemoteSignerLocator.TransportKind.Https.DefaultPort()} for HTTPS and {RemoteSignerLocator.TransportKind.Tcp.DefaultPort()} for TCP (octez-signer's defaults). Use whatever port your signer is set to.",
            FontSize = 12, Foreground = Secondary, TextWrapping = TextWrapping.Wrap,
        });

        var keyRow = new Grid { ColumnSpacing = 8 };
        keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        keyRow.Children.Add(addressBox);
        var keyButtons = new Grid();
        keyButtons.Children.Add(listButton);
        keyButtons.Children.Add(keysButton);
        Grid.SetColumn(keyButtons, 1);
        keyRow.Children.Add(keyButtons);
        Form.Children.Add(keyRow);
        Form.Children.Add(listNote);
        Form.Children.Add(addressNote);
        Form.Children.Add(checkRow);
        Form.Children.Add(alias);
        Form.Children.Add(aliasNote);
        Form.Children.Add(tcpNote);
        Form.Children.Add(Note("Signet keeps only the address and public key. Every operation is sent to the signer to sign, and its signature is checked against the account's key before anything is sent to the network. octez-client can use the account too."));

        CloseButtonText = "Cancel";
        Primary("Add Account", () => Signer is { } signer ? model.ConnectRemoteSigner(alias.Text, signer) : Task.CompletedTask);
        Opened += (_, _) => host.Focus(FocusState.Programmatic);
        Closed += (_, _) => checking?.Cancel();
        ConnectionChanged();
    }

    private string TrimmedHost => host.Text.Trim();
    private int? Port => int.TryParse(portBox.Text.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var p) && p is >= 1 and <= 65535 ? p : null;
    private Address KeyAddress => new(addressBox.Text.Trim());

    /// <summary>Why the host cannot be used this way, if it cannot.</summary>
    private string? HostProblem
    {
        get
        {
            if (TrimmedHost.Length == 0) return null;
            if (TrimmedHost.Contains('/') || TrimmedHost.Contains('@') || TrimmedHost.Contains(' ')) return "Enter only the host name or IP address.";
            if (transport == RemoteSignerLocator.TransportKind.Http && !EndpointPolicy.IsLocalOrPrivateHost(TrimmedHost))
                return "Plain HTTP is only for a signer on this PC or your local network. Use HTTPS or TCP for one elsewhere.";
            return null;
        }
    }

    /// <summary>The signer as entered, for any key; <c>null</c> until host and port make sense.</summary>
    private RemoteSignerLocator? Endpoint =>
        TrimmedHost.Length > 0 && HostProblem is null && Port is { } port ? new RemoteSignerLocator(transport, TrimmedHost, port, KeyAddress) : null;

    private RemoteSignerLocator? Signer => KeyAddress.IsImplicitAccount ? Endpoint : null;
    private Wallet? Existing => model.Wallets.FirstOrDefault(w => w.Address == KeyAddress);
    private bool AliasTaken => model.Wallets.Any(w => w.Alias == alias.Text.Trim());

    protected override void Validate()
    {
        aliasNote.Visibility = AliasTaken ? Visibility.Visible : Visibility.Collapsed;
        IsPrimaryButtonEnabled = Signer is { } signer && check is { } c && c.Signer == signer && Existing is null
                                 && alias.Text.Trim().Length > 0 && !AliasTaken;
    }

    private void ConnectionChanged()
    {
        hostNote.Children.Clear();
        if (HostProblem is { } problem) hostNote.Children.Add(Label(problem, "", "SystemFillColorCautionBrush"));
        else if (Port is null && portBox.Text.Length > 0) hostNote.Children.Add(Label("The port is a number from 1 to 65535.", "", "SystemFillColorCautionBrush"));
        listedKeys = null;
        listNote.Visibility = Visibility.Collapsed;
        tcpNote.Children.Clear();
        if (transport == RemoteSignerLocator.TransportKind.Tcp && Endpoint is { } endpoint && !EndpointPolicy.IsLocalOrPrivateHost(endpoint.Host))
            tcpNote.Children.Add(Label("TCP is not encrypted. What Signet sends to sign is public anyway and every signature is checked against the key, but a signer reachable from the internet without authentication will sign for anyone who asks.", ""));
        KeyChanged();
    }

    private void KeyChanged()
    {
        addressNote.Children.Clear();
        if (addressBox.Text.Length > 0 && !KeyAddress.IsImplicitAccount)
            addressNote.Children.Add(Label("That is not a tz1 to tz5 address.", "", "SystemFillColorCautionBrush"));
        RenderKeyButtons();
        _ = CheckSigner();
    }

    private void RenderKeyButtons()
    {
        if (listedKeys is { Count: > 0 } keys)
        {
            var flyout = new MenuFlyout();
            foreach (var key in keys)
            {
                var item = new MenuFlyoutItem { Text = key.Value };
                item.Click += (_, _) => addressBox.Text = key.Value;
                flyout.Items.Add(item);
            }
            keysButton.Flyout = flyout;
            keysButton.Visibility = Visibility.Visible;
            listButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            keysButton.Visibility = Visibility.Collapsed;
            listButton.Visibility = Visibility.Visible;
            listButton.IsEnabled = Endpoint is not null && !isListing;
        }
    }

    private void RenderCheck()
    {
        checkRow.Children.Clear();
        if (Existing is { } existing && KeyAddress.IsImplicitAccount)
        {
            checkRow.Children.Add(Label($"Already in your list as “{existing.Alias}”.", ""));
        }
        else if (isChecking)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new ProgressRing { IsActive = true, Width = 16, Height = 16 });
            row.Children.Add(new TextBlock { Text = "Asking the signer…", Foreground = Secondary });
            checkRow.Children.Add(row);
        }
        else if (check is { } c && c.Signer == Signer)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(new AccountAvatarView(c.Signer.Address, 32));
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock { Text = c.Signer.Address.Value, Style = (Style)Application.Current.Resources["MonoText"], IsTextSelectionEnabled = true });
            text.Children.Add(Label($"The signer holds this key ({c.Signer.Address.Scheme?.DisplayName() ?? ""}).", "", "SystemFillColorSuccessBrush"));
            row.Children.Add(text);
            checkRow.Children.Add(row);
            if (!c.AcceptsUnauthenticated)
                checkRow.Children.Add(Label("This signer requires authenticated requests, which Signet cannot make yet, so it will refuse to sign. Turn authentication off in the signer (octez-signer: --require-authentication) and keep it reachable only by you, for example through a tunnel.", "", "SystemFillColorCautionBrush"));
        }
        else if (checkError is { } error)
        {
            checkRow.Children.Add(Label(error, "", "SystemFillColorCriticalBrush"));
        }
        Validate();
    }

    private async Task CheckSigner()
    {
        checking?.Cancel();
        var cancel = checking = new CancellationTokenSource();
        check = null;
        checkError = null;
        isChecking = false;
        RenderCheck();
        if (Signer is not { } signer || Existing is not null) return;
        // Wait for typing to settle before asking.
        try { await Task.Delay(500, cancel.Token); }
        catch (TaskCanceledException) { return; }
        isChecking = true;
        RenderCheck();
        try
        {
            var result = await model.CheckRemoteSigner(signer);
            if (cancel.IsCancellationRequested) return;
            check = result;
            if (alias.Text.Length == 0) alias.Text = $"Remote {signer.Address.Shortened(6, 4)}";
        }
        catch (Exception e)
        {
            if (cancel.IsCancellationRequested) return;
            checkError = e.Message;
        }
        finally
        {
            if (!cancel.IsCancellationRequested)
            {
                isChecking = false;
                RenderCheck();
            }
        }
    }

    private async Task ListKeys()
    {
        if (Endpoint is not { } endpoint) return;
        isListing = true;
        listNote.Visibility = Visibility.Collapsed;
        RenderKeyButtons();
        try
        {
            var keys = await model.RemoteSignerKeys(endpoint);
            listedKeys = keys;
            if (keys.Count == 0) ShowListNote("The signer lists no keys.");
            else if (keys.Count == 1 && addressBox.Text.Length == 0) addressBox.Text = keys[0].Value;
        }
        catch (Exception e) when (e is RemoteSignerError.NotFound or RemoteSignerError.Closed)
        {
            ShowListNote("This signer does not list its keys. Enter the key's address; the signer's own configuration or key list shows it.");
        }
        catch (Exception e)
        {
            ShowListNote(e.Message);
        }
        finally
        {
            isListing = false;
            RenderKeyButtons();
        }
    }

    private void ShowListNote(string text)
    {
        listNote.Text = text;
        listNote.Visibility = Visibility.Visible;
    }
}
