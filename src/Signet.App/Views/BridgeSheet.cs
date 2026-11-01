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
/// Moves tez between Tezos layer 1 and the selected account on Etherlink (0.9.1): Tezos on the
/// left, Etherlink on the right, the arrow between them sets the direction. Only layer 1 →
/// Etherlink can be carried out today: Etherlink does not yet offer withdrawals from
/// Michelson-interface (tz) accounts, so that direction, with its Fast / Slow choice, is shown
/// but cannot be confirmed.
/// </summary>
public sealed partial class BridgeSheet : Sheet
{
    private enum Direction { ToEtherlink, ToTezos }
    private enum Speed { Fast, Slow }

    private abstract record Step
    {
        public sealed record Compose : Step;
        public sealed record Working : Step;
        public sealed record Done(string Hash, int Level) : Step;
    }

    private readonly WalletViewModel model;
    private Direction direction = Direction.ToEtherlink;
    private Speed speed = Speed.Fast;
    private Step step = new Step.Compose();
    /// <summary>The estimate and the context it was made in (S31); refreshed whenever that context moves.</summary>
    private WalletViewModel.BridgeEstimate? estimate;
    private string? estimateError;
    private WalletViewModel.BridgeEstimate? submitted;
    private CancellationTokenSource? estimating;

    // Built once and kept in place; only their contents change.
    private readonly StackPanel heading = new();
    private readonly StackPanel compose = new() { Spacing = 12 };
    private readonly Grid sides = new() { ColumnSpacing = 12 };
    private readonly Button arrow = new() { Padding = new Thickness(6), CornerRadius = new CornerRadius(20), VerticalAlignment = VerticalAlignment.Center };
    private readonly FontIcon arrowIcon = new() { FontSize = 24 };
    private readonly TextBlock tezosBalance = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock etherlinkBalance = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBox amountText = new() { PlaceholderText = "0", MinWidth = 140, TextAlignment = TextAlignment.Right };
    private readonly TextBlock exceeds = new() { Text = "More than the spendable balance, fee included.", Style = (Style)Application.Current.Resources["CautionText"] };
    private readonly StackPanel directionPanel = new() { Spacing = 8 };
    private readonly StackPanel passphraseRow = new() { Spacing = 6 };
    private readonly PasswordBox passphrase = new();
    private readonly TextBlock cannotBridge = new()
    {
        Text = "Bridging needs Layer 2 on for this network and a tz1, tz2 or tz3 account Signet can sign for.",
        Style = (Style)Application.Current.Resources["CautionText"],
    };
    private readonly StackPanel working = new() { Spacing = 8 };
    private readonly StackPanel done = new() { Spacing = 10 };

    public BridgeSheet(WalletViewModel model) : base("Bridge", 560)
    {
        this.model = model;
        BuildCompose();
        Form.Children.Add(heading);
        Form.Children.Add(compose);
        Form.Children.Add(working);
        Form.Children.Add(done);

        amountText.TextChanged += (_, _) => ScheduleEstimate();
        passphrase.PasswordChanged += (_, _) => Validate();
        arrow.Click += (_, _) =>
        {
            direction = direction == Direction.ToEtherlink ? Direction.ToTezos : Direction.ToEtherlink;
            ScheduleEstimate();
        };
        PrimaryButtonClick += async (_, args) =>
        {
            if (step is Step.Done) return;
            args.Cancel = true;
            var deferral = args.GetDeferral();
            try { await Bridge(); }
            finally { deferral.Complete(); }
        };
        // A deposit being signed and sent must not be abandoned with Esc.
        Closing += (_, args) => { if (step is Step.Working) args.Cancel = true; };
        model.PropertyChanged += ModelChanged;
        Closed += (_, _) =>
        {
            model.PropertyChanged -= ModelChanged;
            estimating?.Cancel();
        };
        Render();
    }

    private Wallet? Wallet => submitted?.Context.Wallet ?? model.SelectedWallet;
    private Network Network => submitted?.Context.Network ?? model.Network;
    private bool NeedsPassphrase => Wallet?.KeyKind == KeyKind.Encrypted;
    private bool SignsOnLedger => Wallet?.KeyKind == KeyKind.Ledger;
    private decimal? Spendable => model.TezBalance?.Spendable;
    private AssetBalance? Etherlink => model.Assets.FirstOrDefault(a => a.Kind is AssetBalance.AssetKind.Etherlink);

    private decimal? Amount => submitted?.Amount ?? (ParseTez(amountText.Text) is { } value && value > 0 ? value : null);

    private bool ExceedsBalance => Amount is { } amount && Spendable is { } spendable && amount + (estimate?.Estimate.Fee ?? 0) > spendable;

    private bool CanConfirm =>
        direction == Direction.ToEtherlink && Amount is { } amount && estimate?.Amount == amount
        && estimate.Context == model.BridgeContext && !ExceedsBalance && model.CanBridge
        && !(NeedsPassphrase && passphrase.Password.Length == 0) && step is Step.Compose;

    /// <summary>
    /// Everything the estimate depends on: a change of account, network, node, Layer 2 settings
    /// or wallet directory drops the old estimate and asks again (S31).
    /// </summary>
    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(WalletViewModel.Network) or nameof(WalletViewModel.SelectedWallet) or nameof(WalletViewModel.WalletDirectory):
                if (step is Step.Compose) ScheduleEstimate();
                else Render();
                break;
            case nameof(WalletViewModel.TezBalance) or nameof(WalletViewModel.Assets):
                Render();
                break;
        }
    }

    // ---- Pieces ----------------------------------------------------------------------------------

    private void BuildCompose()
    {
        // Tezos on the left, Etherlink on the right; the arrow between them is the direction.
        sides.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sides.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        sides.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sides.Children.Add(Side(ChainSide.Tezos, tezosBalance, "spendable"));
        arrowIcon.Foreground = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        arrow.Content = arrowIcon;
        Grid.SetColumn(arrow, 1);
        sides.Children.Add(arrow);
        var right = Side(ChainSide.Etherlink, etherlinkBalance, "on the rollup");
        Grid.SetColumn(right, 2);
        sides.Children.Add(right);
        compose.Children.Add(sides);

        var amountRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        amountRow.Children.Add(amountText);
        amountRow.Children.Add(new TextBlock { Text = "tz", Foreground = Secondary, VerticalAlignment = VerticalAlignment.Center });
        compose.Children.Add(LabeledContent("Amount", amountRow));
        compose.Children.Add(exceeds);
        compose.Children.Add(directionPanel);
        passphraseRow.Children.Add(new TextBlock { Text = $"Password for “{Wallet?.Alias}”" });
        passphraseRow.Children.Add(passphrase);
        compose.Children.Add(passphraseRow);
        compose.Children.Add(cannotBridge);
    }

    private static Border Side(ChainSide side, TextBlock balance, string note)
    {
        var stack = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
        var logo = ChainSideExtensions.SmallLogo(side.Logo(), 56);
        logo.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(logo);
        stack.Children.Add(new TextBlock { Text = side.RawValue(), FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(balance);
        stack.Children.Add(new TextBlock { Text = note, FontSize = 12, Foreground = Secondary, HorizontalAlignment = HorizontalAlignment.Center });
        return new Border
        {
            Child = stack, Padding = new Thickness(12), CornerRadius = new CornerRadius(10),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
        };
    }

    private void Render()
    {
        heading.Children.Clear();
        if (Wallet is { } wallet) heading.Children.Add(AccountHeading(wallet));

        compose.Visibility = step is Step.Done ? Visibility.Collapsed : Visibility.Visible;
        tezosBalance.Text = Spendable is { } spendable ? AssetBalance.Format(spendable, "tz") : "—";
        etherlinkBalance.Text = Etherlink is { } row ? row.Unavailable is null ? row.FormattedAmount : "—" : "—";
        arrowIcon.Glyph = direction == Direction.ToEtherlink ? "" : "";
        var help = direction == Direction.ToEtherlink ? "Tezos to Etherlink. Click to reverse." : "Etherlink to Tezos. Click to reverse.";
        ToolTipService.SetToolTip(arrow, help);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(arrow, direction == Direction.ToEtherlink ? "Direction: Tezos to Etherlink" : "Direction: Etherlink to Tezos");
        arrow.IsEnabled = amountText.IsEnabled = step is Step.Compose;
        exceeds.Visibility = ExceedsBalance ? Visibility.Visible : Visibility.Collapsed;
        RenderDirection();
        passphraseRow.Visibility = direction == Direction.ToEtherlink && NeedsPassphrase && step is not Step.Done ? Visibility.Visible : Visibility.Collapsed;
        passphrase.IsEnabled = step is Step.Compose;
        cannotBridge.Visibility = model.CanBridge || step is not Step.Compose ? Visibility.Collapsed : Visibility.Visible;

        working.Children.Clear();
        working.Visibility = step is Step.Working ? Visibility.Visible : Visibility.Collapsed;
        if (step is Step.Working)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            line.Children.Add(new ProgressRing { Width = 16, Height = 16, IsActive = true });
            line.Children.Add(new TextBlock { Text = $"Bridging {AssetBalance.Format(Amount ?? 0, "tz")} to Etherlink…", Foreground = Secondary });
            working.Children.Add(line);
            if (SignsOnLedger) working.Children.Add(Label("Confirm the deposit on your Ledger…", "", "AccentTextFillColorPrimaryBrush"));
        }

        done.Children.Clear();
        done.Visibility = step is Step.Done ? Visibility.Visible : Visibility.Collapsed;
        if (step is Step.Done finished) RenderDone(finished);

        switch (step)
        {
            case Step.Compose:
                PrimaryButtonText = "Bridge";
                CloseButtonText = "Cancel";
                break;
            case Step.Working:
                PrimaryButtonText = "Bridge";
                CloseButtonText = "";
                break;
            case Step.Done:
                PrimaryButtonText = "Done";
                CloseButtonText = "";
                break;
        }
        Validate();
    }

    private void RenderDirection()
    {
        directionPanel.Children.Clear();
        if (direction == Direction.ToEtherlink)
        {
            if (estimate?.Estimate is { } result)
            {
                directionPanel.Children.Add(LabeledContent("Fee", new TextBlock { Text = AssetBalance.Format(result.Fee, "tz") }));
                directionPanel.Children.Add(LabeledContent("Total", new TextBlock { Text = AssetBalance.Format(result.Total, "tz") }));
            }
            else if (Amount is not null && step is Step.Compose)
            {
                directionPanel.Children.Add(LabeledContent("Fee", estimateError is { } error
                    ? new TextBlock { Text = error, Style = (Style)Application.Current.Resources["CautionText"], TextAlignment = TextAlignment.Right, MaxWidth = 380 }
                    : new ProgressRing { Width = 16, Height = 16, IsActive = true }));
            }
            directionPanel.Children.Add(Note("The tez leave this account on Tezos through Etherlink's bridge and arrive on Etherlink under the same address within about a minute."));
        }
        else
        {
            var picker = new RadioButtons { Header = "Speed", MaxColumns = 2, SelectedIndex = (int)speed };
            picker.Items.Add("Fast");
            picker.Items.Add("Slow");
            picker.SelectionChanged += (_, _) =>
            {
                if (picker.SelectedIndex < 0 || (Speed)picker.SelectedIndex == speed) return;
                speed = (Speed)picker.SelectedIndex;
                RenderDirection();
            };
            directionPanel.Children.Add(picker);
            directionPanel.Children.Add(Note(speed == Speed.Fast
                ? "Fast: a liquidity provider pays out on Tezos within about a minute for a small fee."
                : "Slow: the tez arrive on Tezos after the rollup's 15-day withdrawal period, with no fee."));
            directionPanel.Children.Add(Label("Etherlink does not yet offer withdrawals from accounts on its Michelson interface (tz addresses). This direction will work once it does.",
                                              "", "SystemFillColorCautionBrush"));
        }
    }

    private void RenderDone(Step.Done finished)
    {
        done.Children.Add(Success($"Sent {AssetBalance.Format(Amount ?? 0, "tz")} to the bridge"));
        done.Children.Add(Note($"Included in block {finished.Level.ToString("N0", CultureInfo.CurrentCulture)}. Etherlink credits {Wallet?.Alias ?? "the account"} within about a minute; the Etherlink balance updates on the next refresh."));
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = finished.Hash, Style = (Style)Application.Current.Resources["MonoText"], IsTextSelectionEnabled = true, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        if (Network.ExplorerURLForOperation(finished.Hash) is { } url)
        {
            var link = new HyperlinkButton { Content = "View in explorer", NavigateUri = url };
            Grid.SetColumn(link, 1);
            row.Children.Add(link);
        }
        done.Children.Add(row);
    }

    protected override void Validate() => IsPrimaryButtonEnabled = step is Step.Done || CanConfirm;

    // ---- Actions ---------------------------------------------------------------------------------

    private async void ScheduleEstimate()
    {
        estimating?.Cancel();
        var cancel = estimating = new CancellationTokenSource();
        estimate = null;
        estimateError = null;
        Render();
        if (step is not Step.Compose || direction != Direction.ToEtherlink || Amount is not { } amount || !model.CanBridge) return;
        try { await Task.Delay(300, cancel.Token); }
        catch (TaskCanceledException) { return; }
        try
        {
            var result = await model.EstimateBridgeDeposit(amount);
            if (!cancel.IsCancellationRequested) estimate = result;
        }
        catch (Exception e)
        {
            if (!cancel.IsCancellationRequested) estimateError = e.Message;
        }
        if (!cancel.IsCancellationRequested) Render();
    }

    private async Task Bridge()
    {
        if (Amount is not { } amount || estimate is not { } approved || direction != Direction.ToEtherlink) return;
        ShowError(null);
        submitted = approved;
        step = new Step.Working();
        Render();
        try
        {
            var (hash, level) = await model.BridgeToEtherlink(amount, approved, NeedsPassphrase ? passphrase.Password : null);
            step = new Step.Done(hash, level);
        }
        catch (Exception e)
        {
            ShowError(e.Message);
            submitted = null;
            step = new Step.Compose();
        }
        Render();
    }
}
