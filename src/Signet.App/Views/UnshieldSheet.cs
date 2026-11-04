using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Signet.Models;
using Signet.Services;
using Signet.ViewModels;

namespace Signet.App.Views;

/// <summary>
/// Unshield tez: moves tez from the account's shielded wallet on the TzEL rollup to an account
/// on Tezos. The rollup proves and applies it within minutes, but the tez only reach Tezos
/// through the rollup's outbox, which the account executes here once the commitment covering
/// it is cemented (about 14 days on Shadownet). The sheet lists every withdrawal under way with
/// where it stands and an Execute button when its time has come.
/// </summary>
public sealed partial class UnshieldSheet : Sheet
{
    private enum Step { Compose, Working, Done }

    private readonly WalletViewModel model;
    private string amountText = "";
    private string recipient;
    private UnshieldEstimate? estimate;
    private string? estimateError;
    private Step step = Step.Compose;
    private UnshieldEstimate? submitted;
    private UnshieldReceipt? receipt;
    private WithdrawalExecutionEstimate? execution;
    private string? executing;
    private string executionPassphrase = "";
    private CancellationTokenSource? estimating;
    private readonly StackPanel sides = new();
    private readonly StackPanel compose = new() { Spacing = 10 };
    private readonly StackPanel costs = new() { Spacing = 8 };
    private readonly StackPanel notices = new();
    private readonly StackPanel withdrawals = new() { Spacing = 8 };
    private readonly StackPanel result = new() { Spacing = 10 };
    private readonly TextBox recipientBox;

    public UnshieldSheet(WalletViewModel model) : base("Unshield tez", 600)
    {
        this.model = model;
        recipient = model.SelectedWallet?.Address.Value ?? "";
        if (model.SelectedWallet is { } wallet) Form.Children.Add(AccountHeading(wallet));
        Form.Children.Add(sides);
        compose.Children.Add(new TextBlock { Text = "Amount" });
        compose.Children.Add(FieldWithUnit(BoundTextBox(() => amountText, value => amountText = value, "0", changed: Reestimate), "tz"));
        compose.Children.Add(new TextBlock { Text = "To" });
        recipientBox = BoundTextBox(() => recipient, value => recipient = value, "tz1… or KT1…", monospaced: true, Reestimate);
        compose.Children.Add(recipientBox);
        if (model.SelectedWallet is { } me)
        {
            var self = MakeButton("This account", () => recipientBox.Text = me.Address.Value);
            compose.Children.Add(Row(self));
        }
        compose.Children.Add(costs);
        compose.Children.Add(Note("The notes are spent on the rollup within minutes; the tez reach the Tezos account only when the rollup's outbox message is executed, which is possible about 14 days later. The withdrawal stays listed here until then, with an Execute button when its time comes. The change comes back as a new note of yours."));
        compose.Children.Add(notices);
        compose.Children.Add(withdrawals);
        Form.Children.Add(compose);
        Form.Children.Add(result);

        Primary("Unshield", Unshield);
        CloseButtonText = "Cancel";
        model.PropertyChanged += ModelChanged;
        Closed += (_, _) => { model.PropertyChanged -= ModelChanged; estimating?.Cancel(); };
        Closing += (_, args) => { if (step == Step.Working || executing is not null) args.Cancel = true; };
        Render();
    }

    private Wallet? Wallet => submitted?.Context.Wallet ?? model.SelectedWallet;
    private Network Network => submitted?.Context.Network ?? model.Network;
    private bool TokenIsSet => model.ShieldedOperatorTokenIsSet(model.Network);
    private bool NeedsPassphrase => Wallet?.KeyKind == KeyKind.Encrypted;
    private bool SignsOnLedger => Wallet?.KeyKind == KeyKind.Ledger;
    private string TrimmedRecipient => recipient.Trim();

    private decimal? Amount
    {
        get
        {
            if (submitted is not null) return submitted.Amount;
            var text = amountText.Trim().Replace(",", ".");
            return decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : null;
        }
    }

    private bool CanConfirm =>
        estimate is { } e && Amount is { } amount && e.Amount == amount && e.Recipient == TrimmedRecipient && e.Context == model.BridgeContext &&
        model.CanShield && TokenIsSet && step == Step.Compose && executing is null;

    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WalletViewModel.ShieldProgress) or nameof(WalletViewModel.ShieldedWithdrawals) or nameof(WalletViewModel.Assets)
            or nameof(WalletViewModel.ShieldedWallet) or nameof(WalletViewModel.TezBalance))
            Render();
    }

    private void Render()
    {
        var shielded = model.Assets.FirstOrDefault(a => a.Kind is AssetBalance.AssetKind.Shielded);
        var available = model.ShieldedWallet is { } w ? w.AvailableMutez / Mutez.PerTez : (decimal?)null;
        sides.Children.Clear();
        if (step != Step.Done)
            sides.Children.Add(ShieldSheet.Sides("ShieldLogo", "Shielded", shielded is null ? null : shielded.Unavailable is null ? shielded.FormattedAmount : "—",
                                                 available is { } a ? $"{ShieldSheet.Tez(a)} available" : "on the TzEL rollup",
                                                 "TezosToken", "Tezos", model.TezBalance is { } t ? ShieldSheet.Tez(t.Spendable) : null, "spendable"));
        compose.Visibility = step == Step.Compose ? Visibility.Visible : Visibility.Collapsed;

        costs.Children.Clear();
        if (estimate is { } e)
        {
            costs.Children.Add(LabeledContent("Rollup and producer fees", new TextBlock { Text = ShieldSheet.Tez(e.Fees) }));
            costs.Children.Add(LabeledContent("Spent from notes", new TextBlock
            {
                Text = $"{ShieldSheet.Tez(e.InputMutez)} in {e.Inputs.Count} note{(e.Inputs.Count == 1 ? "" : "s")}, {ShieldSheet.Tez(e.Change)} back as change",
                TextWrapping = TextWrapping.Wrap, MaxWidth = 340, TextAlignment = TextAlignment.Right,
            }));
            costs.Children.Add(LabeledContent("Received on Tezos", new TextBlock { Text = ShieldSheet.Tez(e.Amount) }));
        }
        else if (Amount is not null && TrimmedRecipient.Length > 0)
        {
            costs.Children.Add(LabeledContent("Rollup and producer fees", estimateError is { } error ? Caution(error) : new ProgressRing { Width = 14, Height = 14, IsActive = true }));
        }

        notices.Children.Clear();
        if (!model.CanShield) notices.Children.Add(Caution("Unshielding needs Shielded Transactions on for this network and an account Signet can sign for."));
        else if (!TokenIsSet) notices.Children.Add(Caution("Unshielding needs the TzEL operator's access token. Enter it in Shielded settings."));

        RenderWithdrawals();
        RenderResult();
        ShowProgress(step == Step.Working ? model.ShieldProgress?.Text ?? $"Unshielding {ShieldSheet.Tez(Amount ?? 0)}…"
            : executing is not null ? SignsOnLedger && model.ShieldProgress is ShieldProgress.Executing ? "Confirm on your Ledger…" : model.ShieldProgress?.Text ?? "Executing…"
            : null);
        (PrimaryButtonText, CloseButtonText) = step switch
        {
            Step.Done => ("Done", ""),
            Step.Working => ("Unshield", ""),
            _ => ("Unshield", "Cancel"),
        };
        Validate();
    }

    /// <summary>Every unshield of this account that is not done on Tezos yet.</summary>
    private void RenderWithdrawals()
    {
        withdrawals.Children.Clear();
        var list = model.ShieldedWithdrawals;
        if (list.Count == 0) return;
        withdrawals.Children.Add(new TextBlock { Text = "Withdrawals", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        foreach (var status in list)
        {
            var stack = new StackPanel { Spacing = 4 };
            var line = new Grid { ColumnSpacing = 8 };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.Children.Add(new TextBlock { Text = ShieldSheet.Tez(status.Withdrawal.Amount), VerticalAlignment = VerticalAlignment.Center });
            var to = new TextBlock { Text = $"to {new Address(status.Withdrawal.Recipient).Shortened()}", Foreground = Secondary, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(to, 1);
            line.Children.Add(to);
            if (Trailing(status) is { } trailing)
            {
                Grid.SetColumn(trailing, 2);
                line.Children.Add(trailing);
            }
            stack.Children.Add(line);
            stack.Children.Add(new TextBlock { Text = status.Text, FontSize = 12, Foreground = Secondary, TextWrapping = TextWrapping.Wrap });
            if (status.State is ShieldedWithdrawalStatus.WithdrawalState.Executed executed && Network.ExplorerURLForOperation(executed.Hash) is { } url)
                stack.Children.Add(new HyperlinkButton { Content = "View in explorer", NavigateUri = url, Padding = new Thickness(0) });
            if (execution is { } pending && pending.Withdrawal.Id == status.Id && executing is null)
            {
                var confirm = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                confirm.Children.Add(new TextBlock { Text = $"Tezos fee {ShieldSheet.Tez(pending.Estimate.Fee)}", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
                Button go = null!;
                if (NeedsPassphrase && Wallet is { } wallet)
                {
                    var box = BoundPasswordBox(() => executionPassphrase, value => executionPassphrase = value, () => go.IsEnabled = executionPassphrase.Length > 0);
                    box.PlaceholderText = $"Password for “{wallet.Alias}”";
                    box.Width = 200;
                    confirm.Children.Add(box);
                }
                go = MakeButton("Confirm", () => _ = Execute(pending), !(NeedsPassphrase && executionPassphrase.Length == 0));
                go.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
                confirm.Children.Add(go);
                confirm.Children.Add(MakeButton("Cancel", () => { execution = null; Render(); }));
                stack.Children.Add(confirm);
            }
            withdrawals.Children.Add(stack);
        }
    }

    private FrameworkElement? Trailing(ShieldedWithdrawalStatus status)
    {
        if (executing == status.Id) return new ProgressRing { Width = 14, Height = 14, IsActive = true };
        switch (status.State)
        {
            case ShieldedWithdrawalStatus.WithdrawalState.Ready when execution?.Withdrawal.Id != status.Id:
                return MakeButton("Execute…", () => _ = PrepareExecution(status.Id), executing is null && model.CanShield);
            case ShieldedWithdrawalStatus.WithdrawalState.Submitted when status.Withdrawal.Created < DateTimeOffset.UtcNow.AddMinutes(-30):
                var forget = MakeButton("Forget", () => Forget(status.Id));
                ToolTipService.SetToolTip(forget, "Drops the record and frees its notes. Only for an unshield the operator never delivered; if it lands after all, the notes show as spent on the next refresh.");
                return forget;
            default:
                return null;
        }
    }

    private void RenderResult()
    {
        result.Children.Clear();
        if (receipt is not { } r) return;
        result.Children.Add(Success($"Unshielding {ShieldSheet.Tez(r.Amount)} to {new Address(r.Withdrawal.Recipient).Shortened()}"));
        result.Children.Add(Note($"The operator has the unshield (submission {r.Withdrawal.SubmissionID}). The rollup spends the notes once it applies it; {ShieldSheet.Tez(r.ChangeMutez)} comes back as change. The tez reach Tezos when the outbox message is executed, about 14 days from now: open Unshield tez again then and press Execute."));
        if (r.Withdrawal.OperationHash is { } hash) result.Children.Add(ShieldSheet.OperationLine("Delivered in", hash, Network));
    }

    protected override void Validate() => IsPrimaryButtonEnabled = step == Step.Done || CanConfirm;

    // ---- Actions ---------------------------------------------------------------------------------

    private async void Reestimate()
    {
        estimating?.Cancel();
        estimate = null;
        estimateError = null;
        Render();
        if (step != Step.Compose || Amount is not { } amount || TrimmedRecipient.Length == 0 || !model.CanShield) return;
        var cancel = estimating = new CancellationTokenSource();
        try { await Task.Delay(300, cancel.Token); }
        catch (OperationCanceledException) { return; }
        try
        {
            var made = await model.EstimateUnshield(amount, TrimmedRecipient);
            if (cancel.IsCancellationRequested) return;
            estimate = made;
        }
        catch (Exception e)
        {
            if (cancel.IsCancellationRequested) return;
            estimateError = e.Message;
        }
        Render();
    }

    private async Task Unshield()
    {
        if (step == Step.Done) return;   // "Done"
        KeepOpen = true;
        if (estimate is not { } approved || !CanConfirm) return;
        submitted = approved;
        step = Step.Working;
        Render();
        try
        {
            receipt = await model.UnshieldTez(approved);
            step = Step.Done;
        }
        catch (Exception)
        {
            submitted = null;
            step = Step.Compose;
            throw;
        }
        finally { Render(); }
    }

    private async Task PrepareExecution(string id)
    {
        ShowError(null);
        executing = id;
        Render();
        try { execution = await model.EstimateWithdrawalExecution(id); }
        catch (Exception e) { ShowError(e.Message); }
        finally
        {
            executing = null;
            Render();
        }
    }

    private async Task Execute(WithdrawalExecutionEstimate approved)
    {
        ShowError(null);
        executing = approved.Withdrawal.Id;
        Render();
        try
        {
            await model.ExecuteWithdrawal(approved, NeedsPassphrase ? executionPassphrase : null);
            execution = null;
        }
        catch (Exception e) { ShowError(e.Message); }
        finally
        {
            executing = null;
            executionPassphrase = "";
            Render();
        }
    }

    private void Forget(string id)
    {
        ShowError(null);
        try { model.ForgetWithdrawal(id); }
        catch (Exception e) { ShowError(e.Message); }
        Render();
    }
}
