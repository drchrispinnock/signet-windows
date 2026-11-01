using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Signet.Models;
using Signet.ViewModels;

namespace Signet.App.Views;

/// <summary>
/// Send tez: compose (recipient + amount), confirm (you → them), then the result. With Layer 2
/// on, the drop-down at the top left (<c>ChainSidePicker</c>) sends on Etherlink instead: the same
/// account, through the Layer 2 Michelson node, to tz1, tz2 or tz3 accounts only. Both sides are
/// captured when the sheet opens, each network with the chain service it talks to (S16, S31).
/// </summary>
public sealed partial class SendSheet : Sheet
{
    private readonly WalletViewModel model;
    private readonly Wallet sender;
    private readonly IReadOnlyList<Wallet> wallets;
    private readonly SendSideContext layer1;
    private readonly SendSideContext? layer2;
    private ChainSide side = ChainSide.Tezos;
    private SendViewModel send;
    private SendSideContext Context => side == ChainSide.Etherlink ? layer2 ?? layer1 : layer1;
    private Network network => Context.Network;
    private readonly StackPanel heading = new() { Orientation = Orientation.Horizontal, Spacing = 12 };
    private readonly TextBlock headingText = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid from = new() { ColumnSpacing = 8 };
    /// <summary>Set once something was handed to the node, so closing the sheet refreshes the dashboard.</summary>
    public bool DidSend { get; private set; }

    // Compose
    private readonly StackPanel compose = new() { Spacing = 12 };
    private readonly TextBox recipientText = new() { FontFamily = new FontFamily("Cascadia Mono, Consolas"), IsSpellCheckEnabled = false };
    private readonly StackPanel recipientStatus = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly StackPanel suggestionList = new();
    private readonly TextBox amountText = new() { PlaceholderText = "0.0", MinWidth = 160 };
    private readonly Button max = new() { Content = "Max" };
    // Confirm
    private readonly StackPanel confirm = new() { Spacing = 12 };
    private readonly Grid parties = new();
    private readonly StackPanel details = new() { Spacing = 8 };
    private readonly PasswordBox passphrase = new();
    private readonly StackPanel passphraseRow = new() { Spacing = 6 };
    private readonly StackPanel sending = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly TextBlock sendingText = new() { Style = (Style)Application.Current.Resources["SecondaryText"] };
    // Result
    private readonly StackPanel result = new() { Spacing = 12 };

    public SendSheet(WalletViewModel model, Wallet sender) : base("Send tez", 520)
    {
        this.model = model;
        this.sender = sender;
        wallets = model.Wallets;
        (layer1, layer2) = SendSideContext.Capture(model);
        send = MakeSend();
        var picker = new ChainSidePicker(side, layer2 is not null && sender.Address.IsUsableOnEtherlink);
        picker.SideChanged += async picked =>
        {
            side = picked;
            send.PropertyChanged -= Changed;
            send = MakeSend();
            send.PropertyChanged += Changed;
            RenderFrom();
            Render();
            await send.RecoverPending();
        };
        heading.Children.Add(picker);
        heading.Children.Add(headingText);
        BuildCompose();
        BuildConfirm();
        Form.Children.Add(compose);
        Form.Children.Add(confirm);
        Form.Children.Add(result);

        PrimaryButtonClick += async (_, args) =>
        {
            // Every step but the last stays open; the model drives what comes next.
            if (send.CurrentStep is SendViewModel.Step.Sent or SendViewModel.Step.Unknown or SendViewModel.Step.Confirmed) return;
            args.Cancel = true;
            var deferral = args.GetDeferral();
            try
            {
                if (send.CurrentStep is SendViewModel.Step.Compose) await send.ProceedToConfirm();
                else if (send.CurrentStep is SendViewModel.Step.Confirm) await send.Send();
            }
            finally { deferral.Complete(); }
        };
        SecondaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            if (send.CurrentStep is SendViewModel.Step.Confirm) send.BackToCompose();
            else if (send.CurrentStep is SendViewModel.Step.Unknown) await send.Rebroadcast();
        };
        send.PropertyChanged += Changed;
        // Esc must not abandon an operation that is being signed and handed to the node.
        Closing += (_, args) => { if (send.CurrentStep is SendViewModel.Step.Sending) args.Cancel = true; };
        Closed += (_, _) => send.PropertyChanged -= Changed;
        RenderFrom();
        Opened += async (_, _) =>
        {
            recipientText.Focus(FocusState.Programmatic);
            await send.RecoverPending();
        };
        Render();
    }

    private void Changed(object? sender, PropertyChangedEventArgs e) => Render();

    /// <summary>A send for the side in use, on that side's chain service (never the other's).</summary>
    private SendViewModel MakeSend() =>
        new(sender, wallets, Context.Chain, Context.Spendable, (wallet, passphrase) => model.SigningKeyFor(wallet, passphrase), Context.ImplicitAccountsOnly);

    // ---- Step 1 ----------------------------------------------------------------------------------

    private void BuildCompose()
    {
        from.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        from.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        from.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        compose.Children.Add(from);

        compose.Children.Add(new TextBlock { Text = "To" });
        recipientText.TextChanged += (_, _) => send.RecipientText = recipientText.Text;
        compose.Children.Add(recipientText);
        compose.Children.Add(recipientStatus);
        compose.Children.Add(suggestionList);

        compose.Children.Add(new TextBlock { Text = "Amount" });
        var amountRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        amountText.TextChanged += (_, _) => send.AmountText = amountText.Text;
        amountText.KeyDown += async (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;
            e.Handled = true;
            if (send.CanProceed) await send.ProceedToConfirm();
        };
        max.Click += (_, _) => send.UseMaximum();
        ToolTipService.SetToolTip(max, "Everything except a small reserve for the fee");
        amountRow.Children.Add(amountText);
        amountRow.Children.Add(new TextBlock { Text = "tz", Foreground = Secondary, VerticalAlignment = VerticalAlignment.Center });
        amountRow.Children.Add(max);
        compose.Children.Add(amountRow);
    }

    /// <summary>The From line, the recipient prompt and Max: what differs between the two sides.</summary>
    private void RenderFrom()
    {
        from.Children.Clear();
        var who = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        who.Children.Add(new AccountAvatarView(send.Sender.Address, 24));
        who.Children.Add(new TextBlock { Text = send.Sender.Alias, VerticalAlignment = VerticalAlignment.Center });
        who.Children.Add(new TextBlock { Text = send.Sender.Address.Shortened(), Style = (Style)Application.Current.Resources["MonoText"], Foreground = Secondary, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(who, 1);
        from.Children.Add(new TextBlock { Text = "From", VerticalAlignment = VerticalAlignment.Center });
        from.Children.Add(who);
        if (send.Spendable is { } spendable)
        {
            var available = new TextBlock { Text = $"{AssetBalance.Format(spendable, "tz")} spendable", Foreground = Secondary, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(available, 2);
            from.Children.Add(available);
        }
        recipientText.PlaceholderText = send.ImplicitAccountsOnly
            ? "tz1, tz2 or tz3 address, name.tez, or an account from your list"
            : "Address, name.tez, or an account from your list";
        max.IsEnabled = send.Spendable is not null;
        headingText.Text = side == ChainSide.Etherlink ? "Send tez on Etherlink" : "Send tez";
    }

    private void RenderCompose()
    {
        recipientStatus.Children.Clear();
        if (send.Recipient is { } recipient)
        {
            recipientStatus.Children.Add(new AccountAvatarView(recipient.Address, 20));
            recipientStatus.Children.Add(new TextBlock { Text = recipient.DisplayName, VerticalAlignment = VerticalAlignment.Center });
            recipientStatus.Children.Add(VerificationTag(recipient));
            if (recipient.Wallet is null && recipient.Domains.FirstOrDefault() is { } domain && domain != recipient.DisplayName)
                recipientStatus.Children.Add(new TextBlock { Text = domain, Foreground = Secondary, VerticalAlignment = VerticalAlignment.Center });
        }
        else if (send.IsResolving)
        {
            recipientStatus.Children.Add(new ProgressRing { Width = 14, Height = 14, IsActive = true });
            recipientStatus.Children.Add(new TextBlock { Text = "Looking up name…", Foreground = Secondary });
        }
        else if (send.RecipientText.Length > 0 && send.Suggestions.Count == 0 && send.ErrorMessage is null)
        {
            recipientStatus.Children.Add(Label("Not a valid address or .tez name", ""));
        }
        if (amountText.Text != send.AmountText) amountText.Text = send.AmountText;
        if (recipientText.Text != send.RecipientText) recipientText.Text = send.RecipientText;

        suggestionList.Children.Clear();
        if (send.Recipient is null)
        {
            foreach (var wallet in send.Suggestions.Take(6))
            {
                var row = new Grid { ColumnSpacing = 8, Padding = new Thickness(0, 4, 0, 4) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var alias = new TextBlock { Text = wallet.Alias, VerticalAlignment = VerticalAlignment.Center };
                var address = new TextBlock { Text = wallet.Address.Shortened(), Style = (Style)Application.Current.Resources["MonoText"], Foreground = Secondary, VerticalAlignment = VerticalAlignment.Center };
                var kind = new TextBlock { Text = wallet.KeyKind == KeyKind.None ? "Address book" : "My account", FontSize = 12, Foreground = Tertiary, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(alias, 1);
                Grid.SetColumn(address, 2);
                Grid.SetColumn(kind, 3);
                row.Children.Add(new AccountAvatarView(wallet.Address, 20));
                row.Children.Add(alias);
                row.Children.Add(address);
                row.Children.Add(kind);
                var button = new Button
                {
                    Content = row, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), Padding = new Thickness(4, 0, 4, 0),
                };
                button.Click += (_, _) => send.Choose(wallet);
                suggestionList.Children.Add(button);
            }
        }
    }

    /// <summary>"Verified" when the recipient is one of ours; otherwise the name is hearsay from an indexer.</summary>
    private static Border VerificationTag(SendViewModel.RecipientInfo recipient)
    {
        var verified = recipient.IsVerified;
        var color = (Brush)Application.Current.Resources[verified ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush"];
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        row.Children.Add(new FontIcon { Glyph = verified ? "" : "", FontSize = 11, Foreground = color });
        row.Children.Add(new TextBlock
        {
            Text = verified ? (recipient.Wallet?.KeyKind == KeyKind.None ? "Address book" : "My account") : "Not verified",
            FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = color,
        });
        var tag = new Border
        {
            Child = row, CornerRadius = new CornerRadius(9), Padding = new Thickness(6, 2, 6, 2), VerticalAlignment = VerticalAlignment.Center,
            Background = (Brush)Application.Current.Resources[verified ? "SystemFillColorSuccessBackgroundBrush" : "SystemFillColorCautionBackgroundBrush"],
        };
        if (!verified)
            ToolTipService.SetToolTip(tag, "This name comes from the recipient's public profile (TzProfiles via TzKT), not from your own records. Check the address.");
        return tag;
    }

    // ---- Step 2 ----------------------------------------------------------------------------------

    private void BuildConfirm()
    {
        parties.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        parties.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        parties.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        confirm.Children.Add(parties);
        confirm.Children.Add(details);
        passphraseRow.Children.Add(new TextBlock { Text = $"Password for “{send.Sender.Alias}”" });
        passphraseRow.Children.Add(passphrase);
        passphrase.PasswordChanged += (_, _) => send.Passphrase = passphrase.Password;
        passphrase.KeyDown += async (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter || passphrase.Password.Length == 0) return;
            e.Handled = true;
            await send.Send();
        };
        confirm.Children.Add(passphraseRow);
        sending.Children.Add(new ProgressRing { Width = 16, Height = 16, IsActive = true });
        sending.Children.Add(sendingText);
        confirm.Children.Add(sending);
    }

    private void RenderConfirm()
    {
        parties.Children.Clear();
        details.Children.Clear();
        if (send.Recipient is not { } recipient || send.Amount is not { } amount) return;
        AddParty(0, send.Sender.Address, send.Sender.Alias, send.Sender.Address.Shortened(), null);
        var middle = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 28, 0, 0) };
        middle.Children.Add(new FontIcon { Glyph = "", FontSize = 26, Foreground = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"] });
        middle.Children.Add(new TextBlock { Text = AssetBalance.Format(amount, "tz"), FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        Grid.SetColumn(middle, 1);
        parties.Children.Add(middle);
        var subtitle = recipient.Domains.FirstOrDefault() is { } domain && domain != recipient.DisplayName ? domain : recipient.Address.Shortened();
        AddParty(2, recipient.Address, recipient.DisplayName, subtitle, VerificationTag(recipient));

        details.Children.Add(LabeledContent("To", new TextBlock { Text = recipient.Address.Value, Style = (Style)Application.Current.Resources["MonoText"], IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap }));
        if (send.Estimate is { } estimate)
        {
            details.Children.Add(LabeledContent("Amount", new TextBlock { Text = AssetBalance.Format(amount, "tz") }));
            details.Children.Add(LabeledContent("Fee", new TextBlock { Text = AssetBalance.Format(estimate.Fee, "tz") }));
            if (estimate.Burn > 0)
            {
                var allocation = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
                allocation.Children.Add(new TextBlock { Text = AssetBalance.Format(estimate.Burn, "tz"), HorizontalAlignment = HorizontalAlignment.Right });
                allocation.Children.Add(new TextBlock
                {
                    Text = "The recipient has never held tez; the chain charges once to create the account.",
                    FontSize = 12, Foreground = Secondary, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Right, MaxWidth = 300,
                });
                details.Children.Add(LabeledContent("Allocation", allocation));
            }
            var total = LabeledContent("Total", new TextBlock { Text = AssetBalance.Format(estimate.Total, "tz"), FontWeight = FontWeights.SemiBold });
            ((TextBlock)total.Children[0]).FontWeight = FontWeights.SemiBold;
            details.Children.Add(total);
        }
    }

    private void AddParty(int column, Address address, string name, string subtitle, UIElement? tag)
    {
        var stack = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(new AccountAvatarView(address, 72) { HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        stack.Children.Add(new TextBlock { Text = subtitle, Style = (Style)Application.Current.Resources["MonoText"], Foreground = Secondary, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        if (tag is FrameworkElement element)
        {
            element.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Children.Add(element);
        }
        Grid.SetColumn(stack, column);
        parties.Children.Add(stack);
    }

    // ---- Step 3 ----------------------------------------------------------------------------------

    private void RenderResult()
    {
        result.Children.Clear();
        switch (send.CurrentStep)
        {
            case SendViewModel.Step.Sent sent:
                result.Children.Add(ResultHeader("", "Sent", "Waiting for the next block…", spinning: true));
                result.Children.Add(OperationLink(sent.Hash));
                break;
            case SendViewModel.Step.Confirmed confirmed:
                result.Children.Add(ResultHeader("", "Confirmed", $"Included in block {confirmed.Level.ToString("N0", CultureInfo.CurrentCulture)}", spinning: false));
                result.Children.Add(OperationLink(confirmed.Hash));
                break;
            case SendViewModel.Step.Unknown unknown:
                result.Children.Add(ResultHeader("", "Not confirmed by the node", send.IsBusy ? "Watching the chain…" : "The node's answer was lost", spinning: send.IsBusy));
                result.Children.Add(OperationLink(unknown.Hash));
                result.Children.Add(Note("The operation was signed and handed to the node, but its answer did not arrive. Signet is watching the chain for it. Sending again sends the same signed operation, so it cannot be paid twice."));
                break;
        }
        if (send.Sent?.OriginatedAddress is { } contract)
            result.Children.Add(LabeledContent("Contract address", new TextBlock { Text = contract, Style = (Style)Application.Current.Resources["MonoText"], IsTextSelectionEnabled = true }));
    }

    private static StackPanel ResultHeader(string glyph, string title, string subtitle, bool spinning)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(new FontIcon
        {
            Glyph = glyph, FontSize = 34,
            Foreground = (Brush)Application.Current.Resources[spinning ? "AccentFillColorDefaultBrush" : "SystemFillColorSuccessBrush"],
        });
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] });
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (spinning) line.Children.Add(new ProgressRing { Width = 12, Height = 12, IsActive = true });
        line.Children.Add(new TextBlock { Text = subtitle, Style = (Style)Application.Current.Resources["SecondaryText"] });
        text.Children.Add(line);
        row.Children.Add(text);
        return row;
    }

    private Grid OperationLink(string hash)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = hash, Style = (Style)Application.Current.Resources["MonoText"], IsTextSelectionEnabled = true, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        if (network.ExplorerURLForOperation(hash) is { } url)
        {
            var link = new HyperlinkButton { Content = "View in explorer", NavigateUri = url };
            Grid.SetColumn(link, 1);
            grid.Children.Add(link);
        }
        return grid;
    }

    // ---- Steps -----------------------------------------------------------------------------------

    private void Render()
    {
        var step = send.CurrentStep;
        var isCompose = step is SendViewModel.Step.Compose;
        var isConfirm = step is SendViewModel.Step.Confirm or SendViewModel.Step.Sending;
        var isResult = !isCompose && !isConfirm;
        if (isResult) DidSend = true;

        compose.Visibility = isCompose ? Visibility.Visible : Visibility.Collapsed;
        confirm.Visibility = isConfirm ? Visibility.Visible : Visibility.Collapsed;
        result.Visibility = isResult ? Visibility.Visible : Visibility.Collapsed;
        Title = isCompose ? heading : isConfirm ? "Confirm" : null;

        if (isCompose) RenderCompose();
        if (isConfirm) RenderConfirm();
        if (isResult) RenderResult();

        passphraseRow.Visibility = isConfirm && send.NeedsPassphrase ? Visibility.Visible : Visibility.Collapsed;
        sending.Visibility = step is SendViewModel.Step.Sending ? Visibility.Visible : Visibility.Collapsed;
        sendingText.Text = send.SignsOnLedger ? "Confirm on your Ledger…" : "Signing and sending…";
        ShowError(send.ErrorMessage);
        ShowProgress(isCompose && send.IsBusy ? "Estimating the fee…" : null);

        // The buttons for this step.
        switch (step)
        {
            case SendViewModel.Step.Compose:
                PrimaryButtonText = "Continue";
                IsPrimaryButtonEnabled = send.CanProceed;
                SecondaryButtonText = "";
                CloseButtonText = "Cancel";
                break;
            case SendViewModel.Step.Confirm or SendViewModel.Step.Sending:
                PrimaryButtonText = "Confirm and Send";
                IsPrimaryButtonEnabled = step is SendViewModel.Step.Confirm && !send.IsBusy && !(send.NeedsPassphrase && send.Passphrase.Length == 0);
                SecondaryButtonText = "Back";
                IsSecondaryButtonEnabled = step is SendViewModel.Step.Confirm;
                CloseButtonText = "";
                break;
            default:
                PrimaryButtonText = "Done";
                IsPrimaryButtonEnabled = true;
                SecondaryButtonText = step is SendViewModel.Step.Unknown ? "Send the same operation again" : "";
                IsSecondaryButtonEnabled = !send.IsBusy;
                CloseButtonText = "";
                break;
        }
    }

    private static Brush Tertiary => (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
}
