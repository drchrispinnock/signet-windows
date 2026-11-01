using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Signet.App.Services;
using Signet.Models;
using Signet.Services;
using Signet.ViewModels;

namespace Signet.App.Views;

/// <summary>
/// Shows the selected wallet's address as a QR code and text so someone can send tez to it.
/// With Layer 2 on, the logo at the top left has a drop-down beside it (<c>ChainSidePicker</c>) that
/// switches to the account's address on Etherlink's EVM side (<c>EtherlinkAlias</c>). That address
/// is only shown once it exists on chain (<c>EtherlinkAliasStatus.Linked</c>): until the account has
/// made its first cross-interface call, tez sent to it would sit at an address nobody controls,
/// so the sheet offers to link the account first (a no-op gateway call paid on Etherlink).
/// </summary>
public sealed partial class ReceiveSheet : Sheet
{
    private readonly Wallet wallet;
    private readonly IReadOnlyList<string> domains;
    /// <summary>The model that checks and links the alias; <c>null</c> shows the Tezos side only.</summary>
    private readonly WalletViewModel? model;
    private ChainSide side = ChainSide.Tezos;
    private AliasState alias = new AliasState.Checking();
    private string passphrase = "";
    private TransferEstimate? estimate;
    /// <summary>Bumped on every side switch, so a slow alias check for an earlier pick is dropped.</summary>
    private int generation;

    private abstract record AliasState
    {
        public sealed record Checking : AliasState;
        public sealed record Linked(string Evm) : AliasState;
        public sealed record Unlinked(decimal? Fee, string? Problem) : AliasState;
        public sealed record Linking : AliasState;
        public sealed record Failed(string Message) : AliasState;
    }

    /// <param name="showsEtherlink">Offer the Etherlink address too: Layer 2 is on and the key is usable on the rollup.</param>
    public ReceiveSheet(Wallet wallet, IReadOnlyList<string> domains, bool showsEtherlink = false, WalletViewModel? model = null) : base("Receive", 372)
    {
        this.wallet = wallet;
        this.domains = domains;
        this.model = model;
        var picker = new ChainSidePicker(side, showsEtherlink && model is not null);
        picker.SideChanged += async picked =>
        {
            side = picked;
            var mine = ++generation;
            Render();
            // A link in flight reports back by itself.
            if (side == ChainSide.Etherlink && alias is not AliasState.Linking) await CheckAlias(mine);
        };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        title.Children.Add(picker);
        title.Children.Add(new TextBlock { Text = "Receive", VerticalAlignment = VerticalAlignment.Center });
        Title = title;

        CloseButtonText = "Done";
        DefaultButton = ContentDialogButton.Close;
        PrimaryButtonClick += async (_, args) =>
        {
            // Copying keeps the sheet open, as on the Mac.
            args.Cancel = true;
            if (Address is not { } address) return;
            Pasteboard.SetString(address);
            PrimaryButtonText = "Copied";
            await Task.Delay(1500);
            if (Address is not null) PrimaryButtonText = "Copy address";
        };
        // The link is being signed and sent: stay until it is in a block.
        Closing += (_, args) => { if (alias is AliasState.Linking) args.Cancel = true; };
        Render();
    }

    private string? Address => side == ChainSide.Tezos ? wallet.Address.Value : alias is AliasState.Linked linked ? linked.Evm : null;

    private bool NeedsPassphrase => wallet.KeyKind == KeyKind.Encrypted;

    private void Render()
    {
        Form.Children.Clear();
        var names = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
        names.Children.Add(new TextBlock { Text = wallet.Alias, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        if (side == ChainSide.Tezos)
            foreach (var domain in domains) names.Children.Add(new TextBlock { Text = domain, Foreground = Secondary, HorizontalAlignment = HorizontalAlignment.Center });
        else
            names.Children.Add(new TextBlock { Text = "on Etherlink", Foreground = Secondary, HorizontalAlignment = HorizontalAlignment.Center });
        Form.Children.Add(names);

        if (Address is { } address) AddAddressViews(address);
        else AddLinkViews();

        PrimaryButtonText = Address is null ? "" : "Copy address";
        CloseButtonText = alias is AliasState.Linking ? "" : "Done";
    }

    private void AddAddressViews(string address)
    {
        var image = new Image { Width = 220, Height = 220 };
        var bitmap = new BitmapImage();
        using (var stream = new MemoryStream(QRCode.Png(address, 220)))
            bitmap.SetSource(stream.AsRandomAccessStream());
        image.Source = bitmap;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(image, $"QR code for {address}");
        Form.Children.Add(new Border
        {
            Child = image, Padding = new Thickness(12), CornerRadius = new CornerRadius(12), HorizontalAlignment = HorizontalAlignment.Center,
            Background = new SolidColorBrush(Microsoft.UI.Colors.White),
            BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"], BorderThickness = new Thickness(1),
        });
        Form.Children.Add(new TextBlock
        {
            Text = address, Style = (Style)Application.Current.Resources["MonoText"], IsTextSelectionEnabled = true,
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center,
        });
        Form.Children.Add(Centered(side == ChainSide.Tezos
            ? $"Only send tez and Tezos tokens to this address{(domains.Count == 0 ? "." : $", or to {domains[0]}.")}"
            : "This account's address on Etherlink's EVM side. Tez sent to it on Etherlink are forwarded to this account; its own address on Etherlink's Michelson side is the Tezos one."));
    }

    /// <summary>The Etherlink side before the alias exists: what is going on, and the way to link.</summary>
    private void AddLinkViews()
    {
        var stack = new StackPanel { Spacing = 12, MinHeight = 244, HorizontalAlignment = HorizontalAlignment.Center };
        switch (alias)
        {
            case AliasState.Checking:
                stack.Children.Add(new ProgressRing { Width = 20, Height = 20, IsActive = true });
                stack.Children.Add(Centered("Checking whether this account is known on Etherlink…"));
                break;
            case AliasState.Linking:
                stack.Children.Add(new ProgressRing { Width = 20, Height = 20, IsActive = true });
                stack.Children.Add(Centered(wallet.KeyKind == KeyKind.Ledger ? "Confirm the link on your Ledger…" : "Linking to Etherlink…"));
                break;
            case AliasState.Failed failed:
                stack.Children.Add(new TextBlock { Text = failed.Message, Style = (Style)Application.Current.Resources["ErrorText"], TextAlignment = TextAlignment.Center });
                var retry = MakeButton("Try again", async () => await CheckAlias(generation));
                retry.HorizontalAlignment = HorizontalAlignment.Center;
                stack.Children.Add(retry);
                break;
            case AliasState.Unlinked unlinked:
                stack.Children.Add(Centered("This account has no address on Etherlink's EVM side yet. Etherlink creates one the first time the account calls across from its Tezos side; tez sent there before that would be stuck at an address nobody controls."));
                if (wallet.KeyKind.CanSign())
                {
                    stack.Children.Add(Centered("Linking sends a small no-op call from this account on Etherlink. It needs a little tez there to pay the fee (use Bridge… first)."));
                    var link = new Button
                    {
                        Content = unlinked.Fee is { } fee ? $"Link to Etherlink (fee {AssetBalance.Format(fee, "tz")})" : "Link to Etherlink",
                        Style = (Style)Application.Current.Resources["AccentButtonStyle"], HorizontalAlignment = HorizontalAlignment.Center,
                        IsEnabled = unlinked.Fee is not null && !(NeedsPassphrase && passphrase.Length == 0),
                    };
                    if (NeedsPassphrase)
                    {
                        var box = BoundPasswordBox(() => passphrase, value => passphrase = value,
                                                   () => link.IsEnabled = unlinked.Fee is not null && passphrase.Length > 0);
                        box.PlaceholderText = $"Password for {wallet.Alias}";
                        box.MaxWidth = 260;
                        stack.Children.Add(box);
                    }
                    if (unlinked.Problem is { } problem) stack.Children.Add(new TextBlock { Text = problem, Style = (Style)Application.Current.Resources["CautionText"], TextAlignment = TextAlignment.Center });
                    link.Click += async (_, _) => await Link();
                    stack.Children.Add(link);
                }
                else
                {
                    stack.Children.Add(Centered("Link it from a wallet that holds this account's key."));
                }
                break;
        }
        Form.Children.Add(stack);
    }

    private static TextBlock Centered(string text) => new()
    {
        Text = text, Foreground = Secondary, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
        HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 360,
    };

    private void Publish(int mine, AliasState state)
    {
        if (mine != generation) return;
        alias = state;
        Render();
    }

    private async Task CheckAlias(int mine)
    {
        if (model is null) return;
        Publish(mine, new AliasState.Checking());
        try
        {
            if (await model.EtherlinkAliasStatusForSelected() is not { } status)
            {
                Publish(mine, new AliasState.Failed("Layer 2 is off for this network."));
                return;
            }
            if (status is EtherlinkAliasStatus.Linked linked)
            {
                Publish(mine, new AliasState.Linked(linked.EvmAddress));
                return;
            }
            if (!wallet.KeyKind.CanSign())
            {
                Publish(mine, new AliasState.Unlinked(null, null));
                return;
            }
            try
            {
                var fee = await model.EstimateEtherlinkLink();
                if (mine == generation) estimate = fee;
                Publish(mine, new AliasState.Unlinked(fee.Fee, null));
            }
            catch (Exception e)
            {
                Publish(mine, new AliasState.Unlinked(null, $"The link cannot be estimated: {e.Message}"));
            }
        }
        catch (Exception e)
        {
            Publish(mine, new AliasState.Failed(e.Message));
        }
    }

    private async Task Link()
    {
        if (model is null) return;
        var mine = generation;
        var fee = estimate?.Fee;
        Publish(mine, new AliasState.Linking());
        try
        {
            await model.LinkToEtherlink(estimate?.Limits, NeedsPassphrase ? passphrase : null);
            passphrase = "";
            // Ask again for whichever pick is current: the side may have been switched meanwhile.
            alias = new AliasState.Checking();
            if (side == ChainSide.Etherlink) await CheckAlias(generation);
            else Render();
        }
        catch (Exception e)
        {
            alias = new AliasState.Unlinked(fee, e.Message);
            Render();
        }
    }
}
