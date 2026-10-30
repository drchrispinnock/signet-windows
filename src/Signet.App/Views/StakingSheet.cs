using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Signet.Models;
using Signet.ViewModels;

namespace Signet.App.Views;

/// <summary>
/// Delegation and staking for the selected wallet: delegate, change or remove the delegate;
/// stake, unstake, finalize. Each action confirms with a fee estimate (and password if needed).
/// </summary>
public sealed partial class StakingSheet : Sheet
{
    private abstract record Mode
    {
        public sealed record Overview : Mode;
        public sealed record ChooseDelegate : Mode;
        public sealed record Amount(bool Stake) : Mode;
        public sealed record Confirm(StakingOperation Operation) : Mode;
        public sealed record Working(StakingOperation Operation) : Mode;
        public sealed record Done(string Hash, int Level, StakingOperation Operation) : Mode;
    }

    private readonly WalletViewModel model;
    private readonly Wallet? wallet;
    private Mode mode = new Mode.Overview();
    private IReadOnlyList<BakerCandidate> bakers = [];
    private TransferEstimate? estimate;
    private string? delegateName;
    /// <summary>Set once an operation went through, so closing the sheet refreshes the dashboard.</summary>
    public bool DidOperate { get; private set; }

    private readonly TextBox bakerFilter = new() { PlaceholderText = "Search bakers or paste an address" };
    private readonly StackPanel bakerList = new();
    private readonly TextBox amountText = new() { PlaceholderText = "0.0", MinWidth = 160 };
    private readonly PasswordBox passphrase = new();
    private readonly TextBlock fee = new();

    public StakingSheet(WalletViewModel model) : base("Staking", 520)
    {
        this.model = model;
        wallet = model.SelectedWallet;
        bakerFilter.TextChanged += (_, _) => RenderBakers();
        amountText.TextChanged += (_, _) => Validate();
        passphrase.PasswordChanged += (_, _) => Validate();
        PrimaryButtonClick += async (_, args) =>
        {
            if (mode is Mode.Done) return;
            args.Cancel = true;
            var deferral = args.GetDeferral();
            try
            {
                if (mode is Mode.Amount amount && ParsedAmount is { } value)
                    StartConfirm(amount.Stake ? new StakingOperation.Stake(value) : new StakingOperation.Unstake(value));
                else if (mode is Mode.Confirm confirm)
                    await Perform(confirm.Operation);
            }
            finally { deferral.Complete(); }
        };
        SecondaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            ShowError(null);
            SetMode(new Mode.Overview());
        };
        // An operation being signed and sent must not be abandoned with Esc.
        Closing += (_, args) => { if (mode is Mode.Working) args.Cancel = true; };
        Opened += async (_, _) =>
        {
            bakers = await model.Bakers();
            if (mode is Mode.ChooseDelegate) RenderBakers();
            await LoadDelegateName();
        };
        SetMode(new Mode.Overview());
    }

    private bool NeedsPassphrase => wallet?.KeyKind == KeyKind.Encrypted;
    private bool CanOperate => wallet?.KeyKind.CanSign() == true;
    private bool SignsOnLedger => wallet?.KeyKind == KeyKind.Ledger;
    private DelegateInfo? Info => model.DelegateInfo;
    private TezBalance? Balance => model.TezBalance;

    private string WatchOnlyNote => wallet?.KeyKind == KeyKind.Remote
        ? "Signet cannot sign for this account: it uses a remote signer."
        : "Signet holds only this address's public key, so it can show but not change its delegation.";

    private async Task LoadDelegateName()
    {
        delegateName = null;
        if (Info?.Delegate is not { } delegateAddress) return;
        var known = model.DisplayName(delegateAddress, bakers.FirstOrDefault(b => b.Address == delegateAddress)?.Alias);
        delegateName = known.IsOurs || known.Name != delegateAddress.Shortened() ? known.Name : await model.ProfileName(delegateAddress) ?? delegateAddress.Shortened();
        if (mode is Mode.Overview) SetMode(mode);
    }

    // ---- Modes -----------------------------------------------------------------------------------

    private void SetMode(Mode next)
    {
        mode = next;
        Title = next switch
        {
            Mode.Overview => "Staking",
            Mode.ChooseDelegate => "Choose a baker",
            Mode.Amount { Stake: true } => "Stake tez",
            Mode.Amount => "Unstake tez",
            Mode.Confirm c => c.Operation.Title,
            Mode.Working w => w.Operation.Title,
            Mode.Done d => $"{d.Operation.Title}: done",
            _ => "Staking",
        };
        // Detach the reused fields from wherever the last mode put them.
        foreach (var element in new FrameworkElement[] { bakerFilter, amountText, passphrase, fee })
            if (element.Parent is Panel panel) panel.Children.Remove(element);
        Form.Children.Clear();
        if (wallet is not null) Form.Children.Add(AccountHeading(wallet));
        switch (next)
        {
            case Mode.Overview: Overview(); break;
            case Mode.ChooseDelegate: BakerPicker(); break;
            case Mode.Amount amount: AmountForm(amount.Stake); break;
            case Mode.Confirm confirm: Confirmation(confirm.Operation); break;
            case Mode.Working working: WorkingRow(working.Operation); break;
            case Mode.Done done: DoneView(done.Hash, done.Level); break;
        }
        Buttons();
    }

    private void Overview()
    {
        if (!CanOperate) Form.Children.Add(Label(WatchOnlyNote, ""));

        Form.Children.Add(new SectionHeader("Delegation"));
        if (Info is not { } info)
        {
            Form.Children.Add(new ProgressRing { Width = 16, Height = 16, IsActive = true, HorizontalAlignment = HorizontalAlignment.Left });
        }
        else if (info.IsBaker)
        {
            Form.Children.Add(new TextBlock
            {
                Text = "👨‍🍳  " + (CanOperate ? "This account is a baker (self-delegated). Manage it under Baking in the menu." : "This address is a baker (self-delegated)."),
                TextWrapping = TextWrapping.Wrap,
            });
        }
        else if (info.Delegate is { } delegateAddress)
        {
            var who = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            who.Children.Add(new AccountAvatarView(delegateAddress, 22));
            who.Children.Add(new TextBlock { Text = delegateName ?? delegateAddress.Shortened(), VerticalAlignment = VerticalAlignment.Center });
            who.Children.Add(new TextBlock { Text = delegateAddress.Shortened(), Style = (Style)Application.Current.Resources["MonoText"], Foreground = Secondary, VerticalAlignment = VerticalAlignment.Center });
            Form.Children.Add(LabeledContent("Delegate", who));
            Form.Children.Add(LabeledContent("Accepts staking", new TextBlock { Text = info.DelegateAcceptsStaking is { } accepts ? (accepts ? "Yes" : "No") : "Unknown" }));
            Form.Children.Add(Row(
                MakeButton("Change delegate…", () => SetMode(new Mode.ChooseDelegate()), CanOperate),
                MakeButton("Remove delegation", () => StartConfirm(new StakingOperation.Delegate(null)), CanOperate)));
        }
        else
        {
            Form.Children.Add(Note("Not delegated. Delegating lets a baker earn rewards with your balance; the tez never leave your account."));
            Form.Children.Add(Row(MakeButton("Delegate…", () => SetMode(new Mode.ChooseDelegate()), CanOperate)));
        }

        Form.Children.Add(new SectionHeader("Staking"));
        if (Balance is { } balance)
        {
            Form.Children.Add(LabeledContent("Spendable", new TextBlock { Text = AssetBalance.Format(balance.Spendable, "tz") }));
            Form.Children.Add(LabeledContent("Staked", new TextBlock { Text = AssetBalance.Format(balance.Staked, "tz") }));
            if (balance.UnstakedFrozen > 0) Form.Children.Add(LabeledContent("Unstaking", new TextBlock { Text = AssetBalance.Format(balance.UnstakedFrozen, "tz") }));
            if (balance.UnstakedFinalizable > 0) Form.Children.Add(LabeledContent("Ready to finalize", new TextBlock { Text = AssetBalance.Format(balance.UnstakedFinalizable, "tz") }));
        }
        var canStake = Info?.CanStake == true;
        Form.Children.Add(Row(
            MakeButton("Stake…", () => { amountText.Text = ""; SetMode(new Mode.Amount(true)); }, CanOperate && canStake && (Balance?.Spendable ?? 0) > 0),
            MakeButton("Unstake…", () => { amountText.Text = ""; SetMode(new Mode.Amount(false)); }, CanOperate && (Balance?.Staked ?? 0) > 0),
            MakeButton("Finalize", () => StartConfirm(new StakingOperation.FinalizeUnstake()), CanOperate && (Balance?.UnstakedFinalizable ?? 0) > 0)));
        Form.Children.Add(Note(Info?.IsBaker == true
            ? "Staking your own tez raises your baking power and the stake others may add. Staked tez are frozen and can be slashed. Unstaking takes a few cycles, then needs finalizing."
            : canStake
                ? "Staked tez earn more than delegated tez but are frozen and can be slashed if your baker misbehaves. Unstaking takes a few cycles, then needs finalizing."
                : "Staking needs a delegate that accepts stakers."));
    }

    private void BakerPicker()
    {
        Form.Children.Add(bakerFilter);
        Form.Children.Add(new ScrollViewer { Content = bakerList, Height = 300 });
        RenderBakers();
        bakerFilter.Focus(FocusState.Programmatic);
    }

    private IEnumerable<BakerCandidate> FilteredBakers
    {
        get
        {
            var needle = bakerFilter.Text.Trim().ToLowerInvariant();
            return needle.Length == 0 ? bakers
                : bakers.Where(b => b.Alias?.ToLowerInvariant().Contains(needle) == true || b.Address.Value.ToLowerInvariant().StartsWith(needle, StringComparison.Ordinal));
        }
    }

    private void RenderBakers()
    {
        bakerList.Children.Clear();
        var typed = new Address(bakerFilter.Text.Trim());
        if (typed.IsValidAccount)
        {
            var use = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            use.Children.Add(new AccountAvatarView(typed, 24));
            use.Children.Add(new TextBlock { Text = $"Use {typed.Shortened()}", VerticalAlignment = VerticalAlignment.Center });
            bakerList.Children.Add(ListButton(use, () => StartConfirm(new StakingOperation.Delegate(typed))));
        }
        foreach (var baker in FilteredBakers)
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new AccountAvatarView(baker.Address, 28));
            var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = baker.Alias ?? baker.Address.Shortened(), TextTrimming = TextTrimming.CharacterEllipsis });
            var staking = baker.AcceptsStaking switch { true => "accepts staking", false => "no staking", null => "" };
            text.Children.Add(new TextBlock { Text = $"{baker.Delegators} delegators · {baker.Stakers} stakers · {staking}", FontSize = 12, Foreground = Secondary });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            var power = new TextBlock { Text = AssetBalance.Format(baker.StakingBalance, "tz"), FontSize = 12, Foreground = Secondary, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(power, 2);
            row.Children.Add(power);
            bakerList.Children.Add(ListButton(row, () => StartConfirm(new StakingOperation.Delegate(baker.Address))));
        }
        if (bakers.Count == 0) bakerList.Children.Add(Note("No baker list for this network; paste an address above."));
    }

    private void AmountForm(bool stake)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(amountText);
        row.Children.Add(new TextBlock { Text = "tz", Foreground = Secondary, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(MakeButton("Max", () =>
        {
            var max = stake ? Math.Max(0, (Balance?.Spendable ?? 0) - 0.01m) : Balance?.Staked ?? 0;
            amountText.Text = max.ToString(CultureInfo.InvariantCulture);
        }, true));
        Form.Children.Add(new TextBlock { Text = "Amount" });
        Form.Children.Add(row);
        Form.Children.Add(LabeledContent(stake ? "Spendable" : "Staked",
            new TextBlock { Text = AssetBalance.Format(stake ? Balance?.Spendable ?? 0 : Balance?.Staked ?? 0, "tz") }));
        Form.Children.Add(Note(stake
            ? "Staked tez stay in your account but are frozen and shared in your baker's risk."
            : "Unstaked tez become spendable after a few cycles; come back to finalize them."));
        amountText.Focus(FocusState.Programmatic);
    }

    private void Confirmation(StakingOperation operation)
    {
        Form.Children.Add(LabeledContent("Operation", new TextBlock { Text = operation.Title }));
        if (operation is StakingOperation.Delegate { To: { } to })
        {
            var who = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            who.Children.Add(new AccountAvatarView(to, 22));
            who.Children.Add(new TextBlock { Text = bakers.FirstOrDefault(b => b.Address == to)?.Alias ?? model.DisplayName(to, null).Name, VerticalAlignment = VerticalAlignment.Center });
            who.Children.Add(new TextBlock { Text = to.Shortened(), Style = (Style)Application.Current.Resources["MonoText"], Foreground = Secondary, VerticalAlignment = VerticalAlignment.Center });
            Form.Children.Add(LabeledContent("Baker", who));
        }
        if (operation is StakingOperation.Stake stake) Form.Children.Add(LabeledContent("Amount", new TextBlock { Text = AssetBalance.Format(stake.Amount, "tz") }));
        if (operation is StakingOperation.Unstake unstake) Form.Children.Add(LabeledContent("Amount", new TextBlock { Text = AssetBalance.Format(unstake.Amount, "tz") }));
        fee.Text = estimate is { } known ? AssetBalance.Format(known.Fee, "tz") : "Estimating…";
        Form.Children.Add(LabeledContent("Fee", fee));
        if (NeedsPassphrase && wallet is not null)
        {
            Form.Children.Add(new TextBlock { Text = $"Password for “{wallet.Alias}”" });
            Form.Children.Add(passphrase);
        }
        if (SignsOnLedger) Form.Children.Add(Note("Your Ledger will show this operation for approval."));
    }

    private void WorkingRow(StakingOperation operation)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new ProgressRing { Width = 16, Height = 16, IsActive = true });
        row.Children.Add(new TextBlock
        {
            Text = SignsOnLedger ? $"{operation.Title}: confirm on your Ledger…" : $"{operation.Title}… waiting for the next block",
            Foreground = Secondary,
        });
        Form.Children.Add(row);
    }

    private void DoneView(string hash, int level)
    {
        Form.Children.Add(Label($"Included in block {level.ToString("N0", CultureInfo.CurrentCulture)}", "", "SystemFillColorSuccessBrush"));
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = hash, Style = (Style)Application.Current.Resources["MonoText"], IsTextSelectionEnabled = true, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        if (model.Network.ExplorerURLForOperation(hash) is { } url)
        {
            var link = new HyperlinkButton { Content = "View in explorer", NavigateUri = url };
            Grid.SetColumn(link, 1);
            row.Children.Add(link);
        }
        Form.Children.Add(row);
    }

    // ---- Buttons ---------------------------------------------------------------------------------

    private void Buttons()
    {
        (PrimaryButtonText, SecondaryButtonText, CloseButtonText) = mode switch
        {
            Mode.Overview => ("", "", "Done"),
            Mode.ChooseDelegate => ("", "Back", ""),
            Mode.Amount => ("Continue", "Back", ""),
            Mode.Confirm => ("Confirm", "Back", ""),
            Mode.Working => ("", "", ""),
            _ => ("Done", "", ""),
        };
        DefaultButton = mode is Mode.Overview ? ContentDialogButton.Close : ContentDialogButton.Primary;
        // The default button's accent is only applied when the dialog opens; later steps set it themselves.
        PrimaryButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"];
        Validate();
    }

    protected override void Validate()
    {
        IsPrimaryButtonEnabled = mode switch
        {
            Mode.Amount => ParsedAmount is not null,
            Mode.Confirm => estimate is not null && !(NeedsPassphrase && passphrase.Password.Length == 0),
            _ => true,
        };
    }

    private decimal? ParsedAmount
    {
        get
        {
            if (!decimal.TryParse(amountText.Text.Trim().Replace(",", "."), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) || value <= 0) return null;
            if (mode is Mode.Amount { Stake: true } && value > (Balance?.Spendable ?? 0)) return null;
            if (mode is Mode.Amount { Stake: false } && value > (Balance?.Staked ?? 0)) return null;
            return value;
        }
    }

    private async void StartConfirm(StakingOperation operation)
    {
        ShowError(null);
        estimate = null;
        SetMode(new Mode.Confirm(operation));
        try
        {
            var result = await model.EstimateStaking(operation);
            if (mode is not Mode.Confirm current || current.Operation != operation) return;
            estimate = result;
            fee.Text = AssetBalance.Format(result.Fee, "tz");
        }
        catch (Exception e)
        {
            ShowError(e.Message);
            fee.Text = "—";
        }
        Validate();
    }

    private async Task Perform(StakingOperation operation)
    {
        ShowError(null);
        SetMode(new Mode.Working(operation));
        try
        {
            var result = await model.PerformStaking(operation, estimate?.Limits, NeedsPassphrase ? passphrase.Password : null);
            DidOperate = true;
            SetMode(new Mode.Done(result.Hash, result.Level, operation));
        }
        catch (Exception e)
        {
            SetMode(new Mode.Confirm(operation));
            ShowError(e.Message);
        }
    }

    // ---- Building blocks -------------------------------------------------------------------------

    private static Button ListButton(UIElement content, Action action)
    {
        var button = new Button
        {
            Content = content, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), Padding = new Thickness(4, 6, 4, 6),
        };
        button.Click += (_, _) => action();
        return button;
    }
}
