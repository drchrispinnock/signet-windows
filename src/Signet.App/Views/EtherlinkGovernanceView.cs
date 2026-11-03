using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Signet.Models;
using Signet.ViewModels;

namespace Signet.App.Views;

/// <summary>
/// The Etherlink tab of Governance (upstream's <c>EtherlinkGovernanceView</c>): the current period
/// of the slow or fast kernel-upgrade process, the proposals to upvote (or a new kernel hash to
/// propose) in a Proposal period, and Yea / Nay / Pass on the candidate in a Promotion period.
/// Every call is a layer 1 transaction with a fee, so the confirm step shows the estimate and
/// sends with exactly it. On Windows it is part of the one Governance dialog.
/// </summary>
public sealed partial class GovernanceSheet
{
    private EtherlinkGovernance.Process process = EtherlinkGovernance.Process.Slow;
    private EtherlinkGovernance.State? etherlinkState;
    private string? etherlinkLoadError;
    private string newHash = "";
    private WalletViewModel.EtherlinkGovernanceEstimate? etherlinkEstimate;
    private string? etherlinkEstimateError;
    private int etherlinkGeneration;
    private Grid? etherlinkFee;
    private TextBlock? newHashProblem;
    private Button[] newHashButtons = [];

    private IReadOnlyList<EtherlinkGovernance.Process> Processes => model.Network.EtherlinkGovernanceProcesses;

    // ---- Overview --------------------------------------------------------------------------------

    private void EtherlinkOverview()
    {
        if (!Processes.Contains(process) && Processes.Count > 0) process = Processes[0];
        var picker = new SelectorBar();
        foreach (var each in Processes)
            picker.Items.Add(new SelectorBarItem { Text = each.Title(), Tag = each, IsSelected = each == process });
        picker.SelectionChanged += async (sender, _) =>
        {
            if (sender.SelectedItem?.Tag is not EtherlinkGovernance.Process picked || picked == process) return;
            process = picked;
            await LoadEtherlink();
        };
        Form.Children.Add(picker);

        if (etherlinkLoadError is { } error)
        {
            Form.Children.Add(Label(error, "", "SystemFillColorCautionBrush"));
            return;
        }
        if (etherlinkState is not { } state)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new ProgressRing { Width = 16, Height = 16, IsActive = true });
            row.Children.Add(new TextBlock { Text = "Reading the governance contract…", Foreground = Secondary });
            Form.Children.Add(row);
            return;
        }
        PeriodSection(state);
        if (!state.CanVote)
            Form.Children.Add(Label("This account has no voting power on Etherlink: it is neither a baker in this period's listings nor a voting key that a baker has delegated to and that has claimed its rights.", ""));
        else if (state.PeriodKind == EtherlinkGovernance.PeriodKind.Proposal)
            ProposalSection(state);
        else
            PromotionSection(state);
    }

    private void PeriodSection(EtherlinkGovernance.State state)
    {
        Form.Children.Add(new SectionHeader("Current period"));
        Form.Children.Add(LabeledContent("Period", new TextBlock { Text = $"{state.PeriodKind.Title()} (#{state.PeriodIndex})" }));
        Form.Children.Add(LabeledContent("Remaining", new TextBlock
        {
            Text = $"{state.RemainingBlocks.ToString("N0", CultureInfo.CurrentCulture)} blocks of {state.Config.PeriodLength.ToString("N0", CultureInfo.CurrentCulture)}",
        }));
        var power = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Right };
        power.Children.Add(new TextBlock { Text = $"{AssetBalance.Format(state.VotingPower / 1_000_000, "tz")} · {Percent(state.Share(state.VotingPower))}", HorizontalAlignment = HorizontalAlignment.Right });
        if (state.VotesFor.Count > 0)
            power.Children.Add(new TextBlock
            {
                Text = state.VotesFor.Count == 1 ? $"voting key of {model.DisplayName(state.VotesFor[0], null).Name}" : $"voting key of {state.VotesFor.Count} bakers",
                Style = (Style)Application.Current.Resources["CaptionSecondaryText"], HorizontalAlignment = HorizontalAlignment.Right,
            });
        Form.Children.Add(LabeledContent("Your voting power", power));
        var contract = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        contract.Children.Add(new TextBlock { Text = state.Contract.Value, Style = (Style)Application.Current.Resources["MonoText"], IsTextSelectionEnabled = true, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 260, VerticalAlignment = VerticalAlignment.Center });
        if (model.Network.ExplorerURLForContract(state.Contract.Value) is { } url)
            contract.Children.Add(new HyperlinkButton { Content = "Explorer", NavigateUri = url, Padding = new Thickness(4, 0, 4, 0) });
        Form.Children.Add(LabeledContent("Contract", contract));
        Form.Children.Add(new HyperlinkButton { Content = "Proposals are described at governance.etherlink.com", NavigateUri = EtherlinkGovernance.SiteURL, Padding = new Thickness(0) });
    }

    private void ProposalSection(EtherlinkGovernance.State state)
    {
        Form.Children.Add(new SectionHeader("Proposals"));
        if (!state.ProposalsKnown)
            Form.Children.Add(Label("The proposals cannot be listed without a TzKT API for this network; paste a kernel hash to upvote it.", ""));
        else if (state.Proposals.Count == 0)
            Form.Children.Add(Note("No kernels have been proposed yet in this period."));
        var outOfUpvotes = state.UpvotesUsed >= state.Config.UpvotingLimit;
        foreach (var proposal in state.Proposals)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock { Text = proposal.Hash, Style = (Style)Application.Current.Resources["MonoText"], IsTextSelectionEnabled = true, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock
            {
                Text = $"{Percent(state.Share(proposal.Upvotes))} of voting power, needs {Percent(state.Config.Fraction(state.Config.ProposalQuorum))} · proposed by {Proposers(proposal)}",
                Style = (Style)Application.Current.Resources["CaptionSecondaryText"], TextWrapping = TextWrapping.Wrap,
            });
            row.Children.Add(text);
            UIElement action = proposal.Upvoted
                ? Success("Upvoted")
                : MakeButton("Upvote…", () => StartEtherlinkConfirm(new EtherlinkGovernance.Operation.Upvote(proposal.Hash)), !outOfUpvotes);
            Grid.SetColumn((FrameworkElement)action, 1);
            ((FrameworkElement)action).VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(action);
            Form.Children.Add(row);
        }
        Form.Children.Add(new TextBlock { Text = "Propose or upvote a kernel hash" });
        Form.Children.Add(BoundTextBox(() => newHash, value => newHash = value, "0x…", monospaced: true, Validate));
        newHashProblem = new TextBlock { Text = "A kernel hash is 0x followed by 66 hexadecimal digits.", Style = (Style)Application.Current.Resources["CautionText"], TextWrapping = TextWrapping.Wrap };
        Form.Children.Add(newHashProblem);
        Form.Children.Add(Note($"You have used {state.UpvotesUsed} of {state.Config.UpvotingLimit} upvotes this period; proposing counts as one. Upvoting cannot be undone."));
        newHashButtons =
        [
            MakeButton("Upvote…", () => { if (TypedHash is { } hash) StartEtherlinkConfirm(new EtherlinkGovernance.Operation.Upvote(hash)); }),
            MakeButton("Propose…", () => { if (TypedHash is { } hash) StartEtherlinkConfirm(new EtherlinkGovernance.Operation.NewProposal(hash)); }),
        ];
        Form.Children.Add(Row(newHashButtons));
        ValidateEtherlink();
    }

    private string? TypedHash => EtherlinkGovernance.KernelHash(newHash);
    /// <summary>A listed kernel has its own Upvote button and cannot be proposed again.</summary>
    private bool TypedHashIsListed => TypedHash is { } hash && (etherlinkState?.Proposals.Any(p => p.Hash == hash) ?? false);

    /// <summary>The kernel hash field's problem line and buttons, kept current as it is typed.</summary>
    private void ValidateEtherlink()
    {
        if (newHashProblem is not null)
            newHashProblem.Visibility = newHash.Trim().Length > 0 && TypedHash is null ? Visibility.Visible : Visibility.Collapsed;
        var enabled = TypedHash is not null && !TypedHashIsListed && etherlinkState is { } s && s.UpvotesUsed < s.Config.UpvotingLimit;
        foreach (var button in newHashButtons) button.IsEnabled = enabled;
    }

    private string Proposers(EtherlinkGovernance.Proposal proposal)
    {
        if (proposal.Proposers.Count == 0) return "unknown";
        var name = model.DisplayName(proposal.Proposers[0], null).Name;
        return proposal.Proposers.Count > 1 ? $"{name} and {proposal.Proposers.Count - 1} more" : name;
    }

    private void PromotionSection(EtherlinkGovernance.State state)
    {
        Form.Children.Add(new SectionHeader("Promotion vote"));
        if (state.Candidate is { } candidate)
            Form.Children.Add(LabeledContent("Kernel", new TextBlock { Text = candidate, Style = (Style)Application.Current.Resources["MonoText"], IsTextSelectionEnabled = true, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 300 }));
        var t = state.Tallies;
        Form.Children.Add(LabeledContent("So far", new TextBlock
        {
            Text = $"Yea {Percent(Share(t.Yea, t.Total))} · Nay {Percent(Share(t.Nay, t.Total))} · Pass {Percent(Share(t.Pass, t.Total))}",
        }));
        Form.Children.Add(LabeledContent("Turnout", new TextBlock { Text = $"{Percent(state.Share(t.Total))} of {Percent(state.Config.Fraction(state.Config.PromotionQuorum))} quorum" }));
        Form.Children.Add(LabeledContent("Supermajority", new TextBlock { Text = $"{Percent(Share(t.Yea, t.Yea + t.Nay))} Yea of {Percent(state.Config.Fraction(state.Config.PromotionSupermajority))} needed" }));
        if (state.MyVote is { } mine)
        {
            Form.Children.Add(Success($"You voted {mine.Title()} in this period. A vote cannot be changed."));
        }
        else if (state.Candidate is not null)
        {
            Form.Children.Add(Row(EtherlinkGovernance.AllVotes
                .Select(vote => MakeButton(vote.Title(), () => StartEtherlinkConfirm(new EtherlinkGovernance.Operation.Vote(vote)))).ToArray()));
            Form.Children.Add(Note("Yea supports the kernel, Nay opposes it, Pass counts towards the quorum without taking a side."));
        }
    }

    // ---- Confirm ---------------------------------------------------------------------------------

    private void EtherlinkConfirmation(EtherlinkGovernance.Operation operation)
    {
        TextBlock Mono(string text) => new() { Text = text, Style = (Style)Application.Current.Resources["MonoText"], TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 300 };
        Form.Children.Add(LabeledContent("Operation", new TextBlock { Text = operation.Title }));
        if (operation.KernelHash is { } hash) Form.Children.Add(LabeledContent("Kernel", Mono(hash)));
        if (operation is EtherlinkGovernance.Operation.Vote vote && etherlinkState?.Candidate is { } candidate)
        {
            Form.Children.Add(LabeledContent("Kernel", Mono(candidate)));
            Form.Children.Add(LabeledContent("Vote", new TextBlock { Text = vote.Ballot.Title() }));
        }
        if (model.EtherlinkGovernanceContract(process) is { } contract) Form.Children.Add(LabeledContent("Contract", Mono(contract.Value)));
        etherlinkFee = LabeledContent("Fee", FeeContent());
        Form.Children.Add(etherlinkFee);
        if (NeedsPassphrase && wallet is not null)
        {
            Form.Children.Add(new TextBlock { Text = $"Password for “{wallet.Alias}”" });
            Form.Children.Add(BoundPasswordBox(() => passphrase, value => passphrase = value, Validate));
        }
        if (SignsOnLedger) Form.Children.Add(Note("Your Ledger will show this operation for approval."));
    }

    private UIElement FeeContent()
    {
        if (etherlinkEstimate is { } estimate) return new TextBlock { Text = AssetBalance.Format(estimate.Estimate.Fee, "tz") };
        if (etherlinkEstimateError is { } error)
            return new TextBlock { Text = error, Style = (Style)Application.Current.Resources["CautionText"], TextWrapping = TextWrapping.Wrap, MaxWidth = 300 };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new ProgressRing { Width = 14, Height = 14, IsActive = true });
        row.Children.Add(new TextBlock { Text = "Estimating…", Foreground = Secondary });
        return row;
    }

    // ---- Actions ---------------------------------------------------------------------------------

    private async Task LoadEtherlink()
    {
        var mine = ++etherlinkGeneration;
        if (!Processes.Contains(process) && Processes.Count > 0) process = Processes[0];
        etherlinkLoadError = null;
        etherlinkState = null;
        if (mode is Mode.Overview && tab == Tab.Etherlink) SetMode(mode);
        try
        {
            var state = await model.EtherlinkGovernanceState(process);
            if (mine != etherlinkGeneration) return;
            etherlinkState = state;
        }
        catch (Exception e)
        {
            if (mine != etherlinkGeneration) return;
            etherlinkLoadError = $"Could not read the governance contract: {e.Message}";
        }
        if (mode is Mode.Overview && tab == Tab.Etherlink) SetMode(mode);
    }

    private async void StartEtherlinkConfirm(EtherlinkGovernance.Operation operation)
    {
        ShowError(null);
        etherlinkEstimate = null;
        etherlinkEstimateError = null;
        var mine = ++etherlinkGeneration;
        SetMode(new Mode.EtherlinkConfirm(operation));
        try
        {
            var estimate = await model.EstimateEtherlinkGovernance(operation, process);
            if (mine != etherlinkGeneration) return;
            etherlinkEstimate = estimate;
        }
        catch (Exception e)
        {
            if (mine != etherlinkGeneration) return;
            etherlinkEstimateError = e.Message;
        }
        if (mode is Mode.EtherlinkConfirm current && current.Operation == operation && etherlinkFee is not null)
        {
            etherlinkFee.Children.RemoveAt(etherlinkFee.Children.Count - 1);
            var fee = FeeContent();
            Grid.SetColumn((FrameworkElement)fee, 1);
            ((FrameworkElement)fee).HorizontalAlignment = HorizontalAlignment.Right;
            etherlinkFee.Children.Add(fee);
        }
        Validate();
    }

    private async Task PerformEtherlink(EtherlinkGovernance.Operation operation)
    {
        ShowError(null);
        var approved = etherlinkEstimate;
        SetMode(new Mode.EtherlinkWorking(operation));
        try
        {
            var result = await model.PerformEtherlinkGovernance(operation, process, approved, NeedsPassphrase ? passphrase : null);
            newHash = "";
            SetMode(new Mode.EtherlinkDone(result.Hash, result.Level, operation));
            await LoadEtherlink();
        }
        catch (Exception e)
        {
            SetMode(new Mode.EtherlinkConfirm(operation));
            ShowError(e.Message);
        }
    }
}
