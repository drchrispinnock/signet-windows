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
/// Transfers or burns the NFTs chosen in the grid (0.9.13): the tokens with how many editions
/// of each, the recipient (an address, a <c>.tez</c> name or one of our accounts; the burn address
/// for a burn), the fee and storage burn from the estimate, then the result. One FA2
/// <c>transfer</c> call per contract goes out in one operation group with the approved limits
/// (S14), bound to the account, network and directory the estimate was made in (S31).
/// </summary>
public sealed partial class TransferNFTSheet : Sheet
{
    private enum Step { Compose, Working, Done }

    private readonly WalletViewModel model;
    private readonly NFTOperation operation;
    /// <summary>Resolves the recipient as the Send sheet does (names, suggestions, Etherlink's tz1–tz3 rule).</summary>
    private readonly SendViewModel recipient;
    /// <summary>Editions of each token to move; a token held once has no choice.</summary>
    private readonly Dictionary<string, int> counts;
    private WalletViewModel.TokenTransferApproval? estimate;
    private string? estimateError;
    private string passphrase = "";
    private bool understoodBurn;
    private Step step = Step.Compose;
    private WalletViewModel.TokenTransferApproval? submitted;
    private (string Hash, int Level)? done;
    private CancellationTokenSource? estimating;

    private readonly StackPanel tokens = new();
    private readonly StackPanel compose = new() { Spacing = 10 };
    private readonly TextBox recipientText = new() { FontFamily = new FontFamily("Cascadia Mono, Consolas"), IsSpellCheckEnabled = false };
    private readonly StackPanel recipientStatus = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly StackPanel suggestionList = new();
    private readonly StackPanel costs = new() { Spacing = 8 };
    private readonly StackPanel result = new() { Spacing = 10 };

    public TransferNFTSheet(WalletViewModel model, NFTOperation operation) : base("", 560)
    {
        this.model = model;
        this.operation = operation;
        var sender = model.SelectedWallet ?? new Wallet("", new Address(""));
        var chain = operation.Layer == 2 ? model.Layer2Chain ?? model.ChainService : model.ChainService;
        recipient = new SendViewModel(sender, model.Wallets, chain, null, (_, _) => null, implicitAccountsOnly: operation.Layer == 2);
        counts = operation.Nfts.ToDictionary(n => n.Id, _ => 1);
        Title = TitleText;

        if (Wallet is { } wallet)
        {
            var heading = AccountHeading(wallet);
            Form.Children.Add(heading);
        }
        BuildTokens();
        Form.Children.Add(new Border
        {
            Child = new ScrollViewer { Content = tokens, MaxHeight = Math.Min(operation.Nfts.Count, 4) * 60 + 8 },
            CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 0, 6, 0),
            Background = (Brush)Application.Current.Resources["ControlAltFillColorTertiaryBrush"],
        });
        BuildCompose();
        Form.Children.Add(compose);
        Form.Children.Add(result);

        Primary(IsBurn ? "Burn" : "Transfer", Send);
        CloseButtonText = "Cancel";
        if (IsBurn) PrimaryButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"];
        recipient.PropertyChanged += RecipientChanged;
        // A transfer being signed and sent must not be abandoned with Esc.
        Closing += (_, args) => { if (step == Step.Working) args.Cancel = true; };
        Closed += (_, _) => { recipient.PropertyChanged -= RecipientChanged; estimating?.Cancel(); };
        Opened += (_, _) => { if (!IsBurn) recipientText.Focus(FocusState.Programmatic); };
        model.PropertyChanged += ContextChanged;
        Closed += (_, _) => model.PropertyChanged -= ContextChanged;
        Render();
        Reestimate();
    }

    private bool IsBurn => operation.Kind == NFTOperation.OperationKind.Burn;
    private Wallet? Wallet => submitted?.Context.Wallet ?? model.SelectedWallet;
    private Network TokenNetwork => model.TokenNetwork(operation.Layer);
    private bool NeedsPassphrase => Wallet?.KeyKind == KeyKind.Encrypted;
    private bool SignsOnLedger => Wallet?.KeyKind == KeyKind.Ledger;
    private string TitleText =>
        (IsBurn ? "Burn " : "Transfer ") + (operation.Nfts.Count == 1 ? "NFT" : $"{operation.Nfts.Count} NFTs") + (operation.Layer == 2 ? " on Etherlink" : "");

    /// <summary>The request as composed, once it is complete and sound.</summary>
    private TokenTransferRequest? Request
    {
        get
        {
            if (submitted is not null) return submitted.Request;
            Address? to = IsBurn ? TokenTransferRequest.BurnAddress : recipient.Recipient?.Address;
            if (to is not { } address) return null;
            if (!operation.Nfts.All(n => Count(n) >= 1 && Count(n) <= n.Balance)) return null;
            var request = new TokenTransferRequest(address, operation.Nfts.Select(n => new TokenTransfer(n, Count(n))).ToList());
            return request.IsValid ? request : null;
        }
    }

    private int Count(NFT nft) => counts.GetValueOrDefault(nft.Id, 1);
    private int Editions => operation.Nfts.Sum(Count);

    private bool CanConfirm =>
        Request is { } request && estimate is { } approved && approved.Request == request && approved.Context == model.BridgeContext &&
        step == Step.Compose && model.CanTransferTokens(operation.Layer) && !(NeedsPassphrase && passphrase.Length == 0) && (!IsBurn || understoodBurn);

    // ---- Pieces ----------------------------------------------------------------------------------

    /// <summary>The tokens going, with a count for those held in several editions.</summary>
    private void BuildTokens()
    {
        foreach (var nft in operation.Nfts)
        {
            var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 6, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(NFTGridView.NFTTile(nft, 44, showName: false).Tile);
            var names = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            names.Children.Add(new TextBlock { Text = nft.Name, TextTrimming = TextTrimming.CharacterEllipsis });
            names.Children.Add(new TextBlock { Text = $"{new Address(nft.Contract).Shortened()} #{nft.TokenId}", Style = (Style)Application.Current.Resources["MonoText"], FontSize = 12, Foreground = Secondary });
            Grid.SetColumn(names, 1);
            row.Children.Add(names);
            if (nft.Balance > 1)
            {
                var held = (int)Math.Min(nft.Balance, int.MaxValue);
                var stepper = new NumberBox
                {
                    Value = Count(nft), Minimum = 1, Maximum = held, SmallChange = 1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
                    Header = $"of {held}", VerticalAlignment = VerticalAlignment.Center, MinWidth = 120,
                };
                ToolTipService.SetToolTip(stepper, $"How many of the {held} editions held to {(IsBurn ? "burn" : "transfer")}");
                stepper.ValueChanged += (_, args) =>
                {
                    if (double.IsNaN(args.NewValue)) { stepper.Value = Count(nft); return; }
                    counts[nft.Id] = (int)Math.Clamp(Math.Round(args.NewValue), 1, held);
                    Reestimate();
                };
                Grid.SetColumn(stepper, 2);
                row.Children.Add(stepper);
            }
            tokens.Children.Add(row);
        }
    }

    private void BuildCompose()
    {
        if (IsBurn)
        {
            var warning = new StackPanel { Spacing = 6 };
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            head.Children.Add(new FontIcon { Glyph = "", Foreground = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"] });
            head.Children.Add(new TextBlock { Text = "Burning is permanent.", FontWeight = FontWeights.SemiBold, Foreground = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"] });
            warning.Children.Add(head);
            warning.Children.Add(Note($"The tokens go to {TokenTransferRequest.BurnAddress.Shortened()}, an address nobody holds the key to, and can never be recovered or sold."));
            var understood = new CheckBox { Content = $"I understand that {(Editions == 1 ? "this token" : $"these {Editions} tokens")} will be gone for good" };
            understood.Checked += (_, _) => { understoodBurn = true; Render(); };
            understood.Unchecked += (_, _) => { understoodBurn = false; Render(); };
            warning.Children.Add(understood);
            compose.Children.Add(warning);
        }
        else
        {
            compose.Children.Add(new TextBlock { Text = "To" });
            recipientText.PlaceholderText = recipient.ImplicitAccountsOnly
                ? "tz1, tz2 or tz3 address, name.tez, or an account from your list"
                : "Address, name.tez, or an account from your list";
            recipientText.TextChanged += (_, _) => recipient.RecipientText = recipientText.Text;
            compose.Children.Add(recipientText);
            compose.Children.Add(recipientStatus);
            compose.Children.Add(suggestionList);
        }
        compose.Children.Add(costs);
        if (NeedsPassphrase && Wallet is { } wallet)
        {
            compose.Children.Add(new TextBlock { Text = $"Password for “{wallet.Alias}”" });
            compose.Children.Add(BoundPasswordBox(() => passphrase, value => passphrase = value, Render));
        }
        if (!model.CanTransferTokens(operation.Layer))
            compose.Children.Add(Caution(operation.Layer == 2
                ? "Transfers on Etherlink need Layer 2 on and a tz1, tz2 or tz3 account Signet can sign for."
                : "Signet cannot sign for this account."));
    }

    private void RecipientChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SendViewModel.Recipient)) Reestimate();
        Render();
    }

    private void ContextChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A change of account, network, node or wallet directory drops the old estimate and asks again (S31).
        if (e.PropertyName is nameof(WalletViewModel.SelectedWalletID) or nameof(WalletViewModel.Network) or nameof(WalletViewModel.WalletDirectory))
            Reestimate();
    }

    private void RenderRecipient()
    {
        recipientStatus.Children.Clear();
        if (recipient.Recipient is { } resolved)
        {
            recipientStatus.Children.Add(new AccountAvatarView(resolved.Address, 20));
            recipientStatus.Children.Add(new TextBlock { Text = resolved.DisplayName, VerticalAlignment = VerticalAlignment.Center });
            recipientStatus.Children.Add(new TextBlock
            {
                Text = resolved.IsVerified ? (resolved.Wallet?.KeyKind == KeyKind.None ? "Address book" : "My account") : "Not verified",
                FontSize = 11, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources[resolved.IsVerified ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush"],
            });
            if (resolved.Wallet is null && resolved.Domains.FirstOrDefault() is { } domain && domain != resolved.DisplayName)
                recipientStatus.Children.Add(new TextBlock { Text = domain, Foreground = Secondary, VerticalAlignment = VerticalAlignment.Center });
        }
        else if (recipient.IsResolving)
        {
            recipientStatus.Children.Add(new ProgressRing { Width = 14, Height = 14, IsActive = true });
            recipientStatus.Children.Add(new TextBlock { Text = "Looking up name…", Foreground = Secondary });
        }
        else if (recipient.ErrorMessage is { } error)
        {
            recipientStatus.Children.Add(new TextBlock { Text = error, Style = (Style)Application.Current.Resources["ErrorText"] });
        }
        else if (recipient.RecipientText.Length > 0 && recipient.Suggestions.Count == 0)
        {
            recipientStatus.Children.Add(Label("Not a valid address or .tez name", ""));
        }
        if (recipientText.Text != recipient.RecipientText) recipientText.Text = recipient.RecipientText;

        suggestionList.Children.Clear();
        if (recipient.Recipient is not null) return;
        foreach (var candidate in recipient.Suggestions.Take(6))
        {
            var row = new Grid { ColumnSpacing = 8, Padding = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var alias = new TextBlock { Text = candidate.Alias, VerticalAlignment = VerticalAlignment.Center };
            var address = new TextBlock { Text = candidate.Address.Shortened(), Style = (Style)Application.Current.Resources["MonoText"], Foreground = Secondary, VerticalAlignment = VerticalAlignment.Center };
            var kind = new TextBlock { Text = candidate.KeyKind == KeyKind.None ? "Address book" : "My account", FontSize = 12, Foreground = Tertiary, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(alias, 1);
            Grid.SetColumn(address, 2);
            Grid.SetColumn(kind, 3);
            row.Children.Add(new AccountAvatarView(candidate.Address, 20));
            row.Children.Add(alias);
            row.Children.Add(address);
            row.Children.Add(kind);
            var button = new Button
            {
                Content = row, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), Padding = new Thickness(4, 0, 4, 0),
            };
            button.Click += (_, _) => recipient.Choose(candidate);
            suggestionList.Children.Add(button);
        }
    }

    private void RenderCosts()
    {
        costs.Children.Clear();
        if (estimate?.Estimate is { } e)
        {
            costs.Children.Add(LabeledContent("Fee", new TextBlock { Text = AssetBalance.Format(e.Fee, "tz") }));
            if (e.Burn > 0)
            {
                var storage = LabeledContent("Storage", new TextBlock { Text = AssetBalance.Format(e.Burn, "tz") });
                ToolTipService.SetToolTip(storage, "The contract's ledger grows for a recipient that has not held the token before; the chain charges for the space.");
                costs.Children.Add(storage);
            }
            costs.Children.Add(LabeledContent("Total", new TextBlock { Text = AssetBalance.Format(e.Total, "tz"), FontWeight = FontWeights.SemiBold }));
        }
        else if (Request is not null)
        {
            UIElement value = estimateError is { } error
                ? new TextBlock { Text = error, Style = (Style)Application.Current.Resources["CautionText"], TextWrapping = TextWrapping.Wrap, MaxWidth = 340, TextAlignment = TextAlignment.Right }
                : new ProgressRing { Width = 14, Height = 14, IsActive = true };
            costs.Children.Add(LabeledContent("Fee", value));
        }
    }

    private void RenderResult()
    {
        result.Children.Clear();
        if (done is not { } outcome) return;
        var what = Editions == 1 ? "1 token" : $"{Editions} tokens";
        result.Children.Add(Success(IsBurn ? $"Burnt {what}" : $"Transferred {what} to {submitted?.Request.To.Shortened() ?? "the recipient"}"));
        result.Children.Add(Note($"Included in block {outcome.Level.ToString("N0", CultureInfo.CurrentCulture)}. The NFTs tab updates on the next refresh."));
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        line.Children.Add(new TextBlock { Text = outcome.Hash, Style = (Style)Application.Current.Resources["MonoText"], IsTextSelectionEnabled = true, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 340, VerticalAlignment = VerticalAlignment.Center });
        if (TokenNetwork.ExplorerURLForOperation(outcome.Hash) is { } url) line.Children.Add(new HyperlinkButton { Content = "View in explorer", NavigateUri = url });
        result.Children.Add(line);
    }

    private void Render()
    {
        compose.Visibility = step == Step.Compose ? Visibility.Visible : Visibility.Collapsed;
        if (!IsBurn) RenderRecipient();
        RenderCosts();
        RenderResult();
        ShowProgress(step == Step.Working ? SignsOnLedger ? $"Confirm the {(IsBurn ? "burn" : "transfer")} on your Ledger…" : IsBurn ? "Burning…" : "Transferring…" : null);
        switch (step)
        {
            case Step.Compose:
                PrimaryButtonText = IsBurn ? "Burn" : "Transfer";
                CloseButtonText = "Cancel";
                break;
            case Step.Working:
                PrimaryButtonText = IsBurn ? "Burn" : "Transfer";
                CloseButtonText = "";
                break;
            case Step.Done:
                PrimaryButtonText = "Done";
                CloseButtonText = "";
                break;
        }
        Validate();
    }

    protected override void Validate() => IsPrimaryButtonEnabled = step == Step.Done || CanConfirm;

    private static Brush Tertiary => (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];

    // ---- Actions ---------------------------------------------------------------------------------

    /// <summary>
    /// Everything the estimate depends on: the request itself, and a change of account, network,
    /// node or wallet directory drops the old estimate and asks again (S31).
    /// </summary>
    private async void Reestimate()
    {
        estimating?.Cancel();
        estimate = null;
        estimateError = null;
        Render();
        if (step != Step.Compose || Request is not { } request || !model.CanTransferTokens(operation.Layer)) return;
        var cancel = estimating = new CancellationTokenSource();
        try { await Task.Delay(300, cancel.Token); }
        catch (OperationCanceledException) { return; }
        try
        {
            var approved = await model.EstimateTokenTransfer(request, operation.Layer);
            if (cancel.IsCancellationRequested) return;
            estimate = approved;
        }
        catch (Exception e)
        {
            if (cancel.IsCancellationRequested) return;
            estimateError = e.Message;
        }
        Render();
    }

    private async Task Send()
    {
        if (step == Step.Done) return;   // "Done"
        if (Request is not { } request || estimate is not { } approved) { KeepOpen = true; return; }
        submitted = approved;
        step = Step.Working;
        Render();
        try
        {
            done = await model.TransferTokens(request, operation.Layer, approved, NeedsPassphrase ? passphrase : null);
            step = Step.Done;
        }
        catch (Exception)
        {
            submitted = null;
            step = Step.Compose;
            throw;
        }
        finally
        {
            Render();
        }
        KeepOpen = true;
    }
}
