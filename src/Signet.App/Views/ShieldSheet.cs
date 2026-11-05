using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Signet.Models;
using Signet.Services;
using Signet.ViewModels;

namespace Signet.App.Views;

/// <summary>
/// Shield tez: moves tez from the selected account on Tezos into the account's shielded wallet
/// on the TzEL rollup. Two steps run back to back: a layer 1 deposit to the bridge ticketer
/// (an ordinary approved, signed transaction into a deposit pool only this wallet can claim),
/// then the shield itself (a note to ourselves, proved by the proving service and relayed by
/// the operator, which takes a minute or two). A deposit whose shield did not finish stays
/// listed as a pool to shield again.
/// </summary>
public sealed partial class ShieldSheet : Sheet
{
    private enum Step { Compose, Working, Done }

    private readonly WalletViewModel model;
    private string amountText = "";
    private string passphrase = "";
    private DepositPool? pool;
    private string? prepareError;
    private ShieldEstimate? estimate;
    private string? estimateError;
    private Step step = Step.Compose;
    private ShieldEstimate? submitted;
    private ShieldReceipt? receipt;
    private string? shieldingPool;
    private CancellationTokenSource? estimating;
    private readonly StackPanel sides = new();
    private readonly StackPanel compose = new() { Spacing = 10 };
    private readonly StackPanel costs = new() { Spacing = 8 };
    private readonly StackPanel notices = new() { Spacing = 8 };
    private readonly StackPanel pending = new() { Spacing = 6 };
    private readonly StackPanel result = new() { Spacing = 10 };

    public ShieldSheet(WalletViewModel model) : base("Shield tez", 560)
    {
        this.model = model;
        if (model.SelectedWallet is { } wallet) Form.Children.Add(AccountHeading(wallet));
        Form.Children.Add(sides);
        compose.Children.Add(new TextBlock { Text = "Amount" });
        var amount = BoundTextBox(() => amountText, value => amountText = value, "0", changed: Reestimate);
        compose.Children.Add(FieldWithUnit(amount, "tz"));
        compose.Children.Add(costs);
        compose.Children.Add(Note("The tez leave this account on Tezos into a deposit pool on the TzEL rollup that only this wallet can claim, and are then shielded into a note of yours. The rollup's fee is set at shield time; the shielded amount can differ slightly if it changes."));
        compose.Children.Add(Note("The shielded spending key is a separate random key kept unencrypted in the wallet directory and its backups, even when this account's own key has a password. The proof of each shield, unshield and transfer is made by the proving service, which sees that transaction's private details."));
        if (NeedsPassphrase && model.SelectedWallet is { } w)
        {
            compose.Children.Add(new TextBlock { Text = $"Password for “{w.Alias}”" });
            compose.Children.Add(BoundPasswordBox(() => passphrase, value => passphrase = value, Render));
        }
        compose.Children.Add(notices);
        compose.Children.Add(pending);
        Form.Children.Add(compose);
        Form.Children.Add(result);

        Primary("Shield", Shield);
        CloseButtonText = "Cancel";
        model.PropertyChanged += ModelChanged;
        Closed += (_, _) => { model.PropertyChanged -= ModelChanged; estimating?.Cancel(); };
        // A shield being deposited, proved and relayed must not be abandoned with Esc.
        Closing += (_, args) => { if (step == Step.Working || shieldingPool is not null) args.Cancel = true; };
        Opened += async (_, _) => await Prepare();
        Render();
    }

    private Wallet? Wallet => submitted?.Context.Wallet ?? model.SelectedWallet;
    private Network Network => submitted?.Context.Network ?? model.Network;
    private bool NeedsPassphrase => Wallet?.KeyKind == KeyKind.Encrypted;
    private bool SignsOnLedger => Wallet?.KeyKind == KeyKind.Ledger;
    private decimal? Spendable => model.TezBalance?.Spendable;
    private bool TokenIsSet => model.ShieldedOperatorTokenIsSet(model.Network);

    private decimal? Amount
    {
        get
        {
            if (submitted is not null) return submitted.Amount;
            var text = amountText.Trim().Replace(",", ".");
            return decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : null;
        }
    }

    private bool ExceedsBalance => Amount is { } amount && Spendable is { } spendable && amount + (estimate?.Estimate.Fee ?? 0) > spendable;
    /// <summary>The deposit must cover the rollup's and the producer's fees with something left over.</summary>
    private bool TooSmall => estimate is { } e && e.ShieldedAmount <= 0;

    private bool CanConfirm =>
        Amount is { } amount && estimate?.Amount == amount && estimate.Context == model.BridgeContext && pool is not null && !ExceedsBalance && !TooSmall &&
        model.CanShield && TokenIsSet && !(NeedsPassphrase && passphrase.Length == 0) && step == Step.Compose && shieldingPool is null;

    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WalletViewModel.ShieldProgress) or nameof(WalletViewModel.ShieldedPools) or nameof(WalletViewModel.Assets) or nameof(WalletViewModel.TezBalance))
            Render();
    }

    // ---- Rendering -------------------------------------------------------------------------------

    /// <summary>The two balances, Tezos and Shielded, with the arrow between them.</summary>
    internal static Grid Sides(string fromLogo, string fromTitle, string? fromBalance, string fromNote, string toLogo, string toTitle, string? toBalance, string toNote)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(Side(fromLogo, fromTitle, fromBalance, fromNote));
        var arrow = new FontIcon { Glyph = "", FontSize = 28, Foreground = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"], VerticalAlignment = VerticalAlignment.Center };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(arrow, $"Direction: {fromTitle} to {toTitle}");
        Grid.SetColumn(arrow, 1);
        grid.Children.Add(arrow);
        var to = Side(toLogo, toTitle, toBalance, toNote);
        Grid.SetColumn(to, 2);
        grid.Children.Add(to);
        return grid;
    }

    private static Border Side(string logo, string title, string? balance, string note)
    {
        var stack = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(Logo(logo, 56));
        stack.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(new TextBlock { Text = balance ?? "—", HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(new TextBlock { Text = note, FontSize = 12, Foreground = Secondary, HorizontalAlignment = HorizontalAlignment.Center });
        return new Border
        {
            Child = stack, Padding = new Thickness(12), CornerRadius = new CornerRadius(10),
            Background = (Brush)Application.Current.Resources["ControlAltFillColorTertiaryBrush"],
        };
    }

    /// <summary>The Tezos, Etherlink or Shield logo at <c>size</c>.</summary>
    internal static Image Logo(string name, double size) => name == "ShieldLogo"
        ? new Image { Source = new BitmapImage(new Uri("ms-appx:///Assets/ShieldLogo.png")), Width = size, Height = size }
        : ChainSideExtensions.SmallLogo($"Assets/{name}.svg", size);

    internal static string Tez(decimal amount) => AssetBalance.Format(amount, "tz");
    internal static string Tez(ulong mutez) => AssetBalance.Format(mutez / Mutez.PerTez, "tz");

    private void Render()
    {
        var shielded = model.Assets.FirstOrDefault(a => a.Kind is AssetBalance.AssetKind.Shielded);
        sides.Children.Clear();
        if (step != Step.Done)
            sides.Children.Add(Sides("TezosToken", "Tezos", Spendable is { } s ? Tez(s) : null, "spendable",
                                     "ShieldLogo", "Shielded", shielded is null ? null : shielded.Unavailable is null ? shielded.FormattedAmount : "—", "on the TzEL rollup"));
        compose.Visibility = step == Step.Compose ? Visibility.Visible : Visibility.Collapsed;

        costs.Children.Clear();
        if (ExceedsBalance) costs.Children.Add(Caution("More than the spendable balance, fee included."));
        if (estimate is { } e)
        {
            costs.Children.Add(LabeledContent("Tezos fee", new TextBlock { Text = Tez(e.Estimate.Fee) }));
            costs.Children.Add(LabeledContent("Rollup and producer fees", new TextBlock { Text = Tez(e.RollupFeeMutez + e.ProducerFeeMutez) }));
            costs.Children.Add(LabeledContent("Shielded", new TextBlock { Text = Tez(e.ShieldedAmount) }));
            if (TooSmall) costs.Children.Add(Caution("The deposit must be more than the rollup's and the producer's fees."));
        }
        else if (Amount is not null && pool is not null)
        {
            costs.Children.Add(LabeledContent("Tezos fee", estimateError is { } error
                ? Caution(error)
                : new ProgressRing { Width = 14, Height = 14, IsActive = true }));
        }

        notices.Children.Clear();
        if (prepareError is { } problem) notices.Children.Add(Caution(problem));
        else if (pool is null && model.CanShield) notices.Children.Add(ProgressLine(model.ShieldProgress?.Text ?? "Preparing…"));
        if (!model.CanShield) notices.Children.Add(Caution("Shielding needs Shielded Transactions on for this network and an account Signet can sign for."));
        else if (!TokenIsSet) notices.Children.Add(Caution("Shielding needs the TzEL operator's access token. Enter it in Shielded settings."));

        RenderPending();
        RenderResult();
        ShowProgress(step == Step.Working
            ? SignsOnLedger && model.ShieldProgress is ShieldProgress.Depositing ? "Confirm the deposit on your Ledger…" : model.ShieldProgress?.Text ?? $"Shielding {Tez(Amount ?? 0)}…"
            : null);
        (PrimaryButtonText, CloseButtonText) = step switch
        {
            Step.Done => ("Done", ""),
            Step.Working => ("Shield", ""),
            _ => ("Shield", "Cancel"),
        };
        Validate();
    }

    private static StackPanel ProgressLine(string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new ProgressRing { Width = 14, Height = 14, IsActive = true });
        row.Children.Add(new TextBlock { Text = text, Foreground = Secondary, TextWrapping = TextWrapping.Wrap });
        return row;
    }

    /// <summary>Deposits already on the rollup whose shield has not happened yet.</summary>
    private void RenderPending()
    {
        pending.Children.Clear();
        var pools = model.ShieldedPools.Where(p => p.Pool.OperationHash is not null && p.Pool.ShieldedCm is null).ToList();
        if (pools.Count == 0) return;
        pending.Children.Add(new TextBlock { Text = "Deposits waiting to be shielded", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        foreach (var status in pools)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = status.Balance is { } b ? Tez(b) : "not credited yet", VerticalAlignment = VerticalAlignment.Center });
            var id = new TextBlock { Text = status.Pool.PubkeyHash[..Math.Min(12, status.Pool.PubkeyHash.Length)] + "…", Style = (Style)Application.Current.Resources["MonoText"], Foreground = Secondary, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(id, 1);
            row.Children.Add(id);
            UIElement action = shieldingPool == status.Id
                ? ProgressLine(model.ShieldProgress?.Text ?? "Shielding…")
                : MakeButton("Shield", () => _ = ShieldPool(status.Id), shieldingPool is null && TokenIsSet && (status.BalanceMutez ?? 0) > 0);
            Grid.SetColumn((FrameworkElement)action, 2);
            row.Children.Add(action);
            pending.Children.Add(row);
        }
    }

    private void RenderResult()
    {
        result.Children.Clear();
        if (receipt is not { } r) return;
        result.Children.Add(Success($"Shielded {Tez(r.Shielded)}"));
        result.Children.Add(Note($"The operator has delivered the shield to the rollup (fees: {Tez(r.RollupFeeMutez)} rollup, {Tez(r.ProducerFeeMutez)} producer). The Shielded balance shows the note once the rollup has applied it, on the next refresh."));
        if (r.DepositHash is { } deposit) result.Children.Add(OperationLine("Deposit", deposit, Network));
        if (r.OperationHash is { } shield) result.Children.Add(OperationLine("Shield", shield, Network));
    }

    internal static Grid OperationLine(string title, string hash, Network network)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        line.Children.Add(new TextBlock { Text = hash, Style = (Style)Application.Current.Resources["MonoText"], IsTextSelectionEnabled = true, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 260, VerticalAlignment = VerticalAlignment.Center });
        if (network.ExplorerURLForOperation(hash) is { } url) line.Children.Add(new HyperlinkButton { Content = "View in explorer", NavigateUri = url });
        return LabeledContent(title, line);
    }

    protected override void Validate() => IsPrimaryButtonEnabled = step == Step.Done || CanConfirm;

    // ---- Actions ---------------------------------------------------------------------------------

    /// <summary>Checks the rollup and reserves the pool the deposit will go to.</summary>
    private async Task Prepare()
    {
        pool = null;
        prepareError = null;
        Render();
        if (step != Step.Compose || !model.CanShield) return;
        try { pool = await model.PrepareShield(); }
        catch (Exception e) { prepareError = e.Message; }
        Render();
        Reestimate();
    }

    private async void Reestimate()
    {
        estimating?.Cancel();
        estimate = null;
        estimateError = null;
        Render();
        if (step != Step.Compose || Amount is not { } amount || pool is not { } reserved || !model.CanShield) return;
        var cancel = estimating = new CancellationTokenSource();
        try { await Task.Delay(300, cancel.Token); }
        catch (OperationCanceledException) { return; }
        try
        {
            var result = await model.EstimateShield(amount, reserved);
            if (cancel.IsCancellationRequested) return;
            estimate = result;
        }
        catch (Exception e)
        {
            if (cancel.IsCancellationRequested) return;
            estimateError = e.Message;
        }
        Render();
    }

    private async Task Shield()
    {
        if (step == Step.Done) return;   // "Done"
        KeepOpen = true;
        if (estimate is not { } approved || !CanConfirm) return;
        submitted = approved;
        step = Step.Working;
        Render();
        try
        {
            receipt = await model.ShieldTez(approved, NeedsPassphrase ? passphrase : null);
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
            passphrase = "";
            Render();
        }
    }

    /// <summary>Shields a deposit that is already on the rollup.</summary>
    private async Task ShieldPool(string pubkeyHash)
    {
        ShowError(null);
        shieldingPool = pubkeyHash;
        Render();
        try
        {
            receipt = await model.ShieldPool(pubkeyHash);
            step = Step.Done;
        }
        catch (Exception e) { ShowError(e.Message); }
        finally
        {
            shieldingPool = null;
            Render();
        }
    }
}
