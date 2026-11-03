using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Signet.Models;
using Signet.ViewModels;

namespace Signet.App.Views;

/// <summary>
/// Governance for an account we can sign for: the Tezos tab is on-chain protocol voting for a
/// baker (upvote proposals in a proposal period, cast a ballot in the exploration and promotion
/// votes); the Etherlink tab, with Layer 2 on, is Etherlink's kernel-upgrade governance, for a
/// baker or an account that is some baker's voting key (<c>EtherlinkGovernanceView.cs</c>).
/// </summary>
public sealed partial class GovernanceSheet : Sheet
{
    private enum Tab { Tezos, Etherlink }

    private abstract record Mode
    {
        public sealed record Overview : Mode;
        public sealed record Confirm(GovernanceOperation Operation) : Mode;
        public sealed record Working(GovernanceOperation Operation) : Mode;
        public sealed record Done(string Hash, int Level, GovernanceOperation Operation) : Mode;
        public sealed record EtherlinkConfirm(EtherlinkGovernance.Operation Operation) : Mode;
        public sealed record EtherlinkWorking(EtherlinkGovernance.Operation Operation) : Mode;
        public sealed record EtherlinkDone(string Hash, int Level, EtherlinkGovernance.Operation Operation) : Mode;
    }

    private Tab tab = Tab.Tezos;
    private bool ShowsTabs => model.CanGovernEtherlink;

    private readonly WalletViewModel model;
    private readonly Wallet? wallet;
    private Mode mode = new Mode.Overview();
    private GovernanceInfo? info;
    private string? loadError;
    private readonly HashSet<string> selected = [];
    private string newProposal = "";
    private string passphrase = "";
    private TextBlock? newProposalProblem;
    private Button? upvoteButton;

    public GovernanceSheet(WalletViewModel model) : base("Governance", 560)
    {
        this.model = model;
        wallet = model.SelectedWallet;
        PrimaryButtonClick += async (_, args) =>
        {
            if (mode is Mode.Done or Mode.EtherlinkDone) return;
            args.Cancel = true;
            var deferral = args.GetDeferral();
            try
            {
                if (mode is Mode.Confirm confirm) await Perform(confirm.Operation);
                else if (mode is Mode.EtherlinkConfirm etherlink) await PerformEtherlink(etherlink.Operation);
            }
            finally { deferral.Complete(); }
        };
        SecondaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            ShowError(null);
            SetMode(new Mode.Overview());
        };
        // A vote being signed and sent must not be abandoned with Esc.
        Closing += (_, args) => { if (mode is Mode.Working or Mode.EtherlinkWorking) args.Cancel = true; };
        // An account that cannot vote on Tezos (a voting key that is not a baker) opens on Etherlink.
        if (ShowsTabs && model.DelegateInfo?.IsBaker != true) tab = Tab.Etherlink;
        Opened += async (_, _) => await (tab == Tab.Tezos ? Load() : LoadEtherlink());
        SetMode(new Mode.Overview());
    }

    private bool NeedsPassphrase => wallet?.KeyKind == KeyKind.Encrypted;
    private bool SignsOnLedger => wallet?.KeyKind == KeyKind.Ledger;

    // ---- Modes -----------------------------------------------------------------------------------

    private void SetMode(Mode next)
    {
        mode = next;
        Title = next switch
        {
            Mode.Confirm c => c.Operation.Title,
            Mode.Working w => w.Operation.Title,
            Mode.Done d => $"{d.Operation.Title}: done",
            Mode.EtherlinkConfirm c => $"{c.Operation.Title} ({process.Title().ToLowerInvariant()})",
            Mode.EtherlinkWorking w => $"{w.Operation.Title} ({process.Title().ToLowerInvariant()})",
            Mode.EtherlinkDone d => $"{d.Operation.Title}: done",
            _ => "Governance",
        };
        Form.Children.Clear();
        upvoteButton = null;
        newProposalProblem = null;
        etherlinkFee = null;
        newHashProblem = null;
        newHashButtons = [];
        if (wallet is not null) Form.Children.Add(AccountHeading(wallet));
        if (next is Mode.Overview && ShowsTabs) Form.Children.Add(TabPicker());
        switch (next)
        {
            case Mode.Overview when tab == Tab.Etherlink: EtherlinkOverview(); break;
            case Mode.Overview: Overview(); break;
            case Mode.Confirm confirm: Confirmation(confirm.Operation); break;
            case Mode.Working working: AddWorkingRow(working.Operation.Title, SignsOnLedger); break;
            case Mode.Done done: AddIncludedInBlock(model.Network, done.Hash, done.Level); break;
            case Mode.EtherlinkConfirm confirm: EtherlinkConfirmation(confirm.Operation); break;
            case Mode.EtherlinkWorking working:
                AddWorkingRow(working.Operation.Title, SignsOnLedger);
                break;
            case Mode.EtherlinkDone done: AddIncludedInBlock(model.Network, done.Hash, done.Level); break;
        }
        (PrimaryButtonText, SecondaryButtonText, CloseButtonText) = mode switch
        {
            Mode.Overview => ("", "", "Done"),
            Mode.Confirm or Mode.EtherlinkConfirm => ("Confirm", "Back", ""),
            Mode.Working or Mode.EtherlinkWorking => ("", "", ""),
            _ => ("Done", "", ""),
        };
        DefaultButton = mode is Mode.Overview ? ContentDialogButton.Close : ContentDialogButton.Primary;
        PrimaryButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"];
        Validate();
    }

    /// <summary>The Tezos / Etherlink picker, shown with Layer 2 on for an account that can vote there.</summary>
    private UIElement TabPicker()
    {
        var bar = new SelectorBar();
        foreach (var each in new[] { Tab.Tezos, Tab.Etherlink })
            bar.Items.Add(new SelectorBarItem { Text = each.ToString(), Tag = each, IsSelected = each == tab });
        bar.SelectionChanged += async (sender, _) =>
        {
            if (sender.SelectedItem?.Tag is not Tab picked || picked == tab) return;
            tab = picked;
            ShowError(null);
            SetMode(new Mode.Overview());
            await (tab == Tab.Tezos ? Load() : LoadEtherlink());
        };
        return bar;
    }

    protected override void Validate()
    {
        IsPrimaryButtonEnabled = mode switch
        {
            Mode.Confirm => !(NeedsPassphrase && passphrase.Length == 0),
            Mode.EtherlinkConfirm => etherlinkEstimate is not null && !(NeedsPassphrase && passphrase.Length == 0),
            _ => true,
        };
        ValidateEtherlink();
        if (upvoteButton is not null)
            upvoteButton.IsEnabled = UpvoteHashes.Count > 0 && (info?.ProposalCount ?? 0) + UpvoteHashes.Count <= GovernanceInfo.MaximumUpvotesPerPeriod;
        if (newProposalProblem is not null)
        {
            var typed = newProposal.Trim();
            newProposalProblem.Text = typed.Length > 0 && !GovernanceOperation.IsProposalHash(typed) ? "That is not a protocol hash (51 characters starting with P)." : "";
            newProposalProblem.Visibility = newProposalProblem.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // ---- Overview --------------------------------------------------------------------------------

    private void Overview()
    {
        if (model.DelegateInfo?.IsBaker != true)
        {
            Form.Children.Add(Label("Tezos protocol voting is for bakers. This account is not a baker, so it can only vote on Etherlink.", ""));
            return;
        }
        if (loadError is not null)
        {
            Form.Children.Add(Label(loadError, "", "SystemFillColorCautionBrush"));
            return;
        }
        if (info is not { } i)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new ProgressRing { Width = 16, Height = 16, IsActive = true });
            row.Children.Add(new TextBlock { Text = "Reading the voting period…", Foreground = Secondary });
            Form.Children.Add(row);
            return;
        }
        PeriodSection(i);
        if (i.Kind is not { } kind) return;
        if (!i.CanVote)
            Form.Children.Add(Label("This baker is not in the voting listings for this period, so it cannot vote until the next period starts. Bakers need baking rights (enough stake) when the period opens.", ""));
        else if (kind.AcceptsUpvotes())
            ProposalSection(i);
        else if (kind.AcceptsBallots())
            BallotSection(i);
        else
            Form.Children.Add(Note(kind == VotingPeriodKind.Cooldown
                ? "Nothing to vote on: the cooldown period gives everyone time between the exploration and promotion votes."
                : "Nothing to vote on: the adoption period waits for the new protocol to activate."));
    }

    private void PeriodSection(GovernanceInfo i)
    {
        Form.Children.Add(new SectionHeader("Current period"));
        Form.Children.Add(LabeledContent("Period", new TextBlock
        {
            Text = i.Kind is { } kind ? kind.Title() + (i.Index is { } index ? $" (#{index})" : "") : "No governance period",
        }));
        if (i.Remaining is { } remaining)
            Form.Children.Add(LabeledContent("Remaining", new TextBlock { Text = $"{remaining.ToString("N0", CultureInfo.CurrentCulture)} blocks" }));
        if (i.VotingPower is { } power)
            Form.Children.Add(LabeledContent("Your voting power", new TextBlock { Text = $"{AssetBalance.Format(power / 1_000_000, "tz")} · {Percent(i.Share(power))}" }));
    }

    private void ProposalSection(GovernanceInfo i)
    {
        Form.Children.Add(new SectionHeader("Proposals"));
        if (i.Proposals.Count == 0) Form.Children.Add(Note("No proposals have been submitted yet in this period."));
        foreach (var proposal in i.Proposals)
        {
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock { Text = proposal.Hash, Style = (Style)Application.Current.Resources["MonoText"], TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = $"{Percent(i.Share(proposal.VotingPower))} of voting power", Style = (Style)Application.Current.Resources["CaptionSecondaryText"] });
            var box = new CheckBox { Content = text, IsChecked = selected.Contains(proposal.Hash) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, proposal.Hash);
            box.Checked += (_, _) => { selected.Add(proposal.Hash); Validate(); };
            box.Unchecked += (_, _) => { selected.Remove(proposal.Hash); Validate(); };
            Form.Children.Add(box);
        }
        Form.Children.Add(new TextBlock { Text = "Or a new proposal hash" });
        Form.Children.Add(BoundTextBox(() => newProposal, value => newProposal = value, "P…", monospaced: true, Validate));
        newProposalProblem = new TextBlock { Style = (Style)Application.Current.Resources["CautionText"], TextWrapping = TextWrapping.Wrap };
        Form.Children.Add(newProposalProblem);
        Form.Children.Add(Note($"A baker may upvote up to {GovernanceInfo.MaximumUpvotesPerPeriod} proposals per period; you have used {i.ProposalCount}. Upvoting cannot be undone."));
        upvoteButton = MakeButton("Upvote…", () => StartConfirm(new GovernanceOperation.Upvote(UpvoteHashes)));
        Form.Children.Add(Row(upvoteButton));
    }

    private IReadOnlyList<string> UpvoteHashes
    {
        get
        {
            var hashes = selected.Order(StringComparer.Ordinal).ToList();
            var typed = newProposal.Trim();
            if (GovernanceOperation.IsProposalHash(typed) && !hashes.Contains(typed)) hashes.Add(typed);
            return hashes;
        }
    }

    private void BallotSection(GovernanceInfo i)
    {
        Form.Children.Add(new SectionHeader(i.Kind == VotingPeriodKind.Exploration ? "Exploration vote" : "Promotion vote"));
        if (i.CurrentProposal is { } current)
            Form.Children.Add(LabeledContent("Proposal", new TextBlock { Text = current, Style = (Style)Application.Current.Resources["MonoText"], IsTextSelectionEnabled = true, TextTrimming = TextTrimming.CharacterEllipsis }));
        var b = i.BallotTally;
        Form.Children.Add(LabeledContent("So far", new TextBlock
        {
            Text = $"Yay {Percent(Share(b.Yay, b.Total))} · Nay {Percent(Share(b.Nay, b.Total))} · Pass {Percent(Share(b.Pass, b.Total))}",
        }));
        if (i.QuorumPerTenThousand is { } quorum)
            Form.Children.Add(LabeledContent("Turnout", new TextBlock { Text = $"{Percent(i.Share(b.Total))} of {Percent(quorum / 10_000.0)} quorum" }));
        if (i.MyBallot is { } mine)
        {
            Form.Children.Add(Success($"You voted {mine.Title()} in this period. A ballot cannot be changed."));
        }
        else if (i.CurrentProposal is { } proposal)
        {
            Form.Children.Add(Row(Enum.GetValues<BallotVote>()
                .Select(vote => MakeButton(vote.Title(), () => StartConfirm(new GovernanceOperation.Ballot(proposal, vote)))).ToArray()));
            Form.Children.Add(Note("Yay supports the proposal, Nay opposes it, Pass counts towards the quorum without taking a side."));
        }
    }

    private static double Share(decimal part, decimal whole) => whole > 0 ? (double)(part / whole) : 0;

    private static string Percent(double fraction) => (fraction * 100).ToString("0.##", CultureInfo.CurrentCulture) + "%";

    // ---- Confirm ---------------------------------------------------------------------------------

    private void Confirmation(GovernanceOperation operation)
    {
        Form.Children.Add(LabeledContent("Operation", new TextBlock { Text = operation.Title }));
        TextBlock Hash(string hash) => new() { Text = hash, Style = (Style)Application.Current.Resources["MonoText"], TextTrimming = TextTrimming.CharacterEllipsis };
        switch (operation)
        {
            case GovernanceOperation.Upvote upvote:
                foreach (var hash in upvote.Hashes) Form.Children.Add(LabeledContent("Proposal", Hash(hash)));
                break;
            case GovernanceOperation.Ballot ballot:
                Form.Children.Add(LabeledContent("Proposal", Hash(ballot.Proposal)));
                Form.Children.Add(LabeledContent("Ballot", new TextBlock { Text = ballot.Vote.Title() }));
                break;
        }
        Form.Children.Add(LabeledContent("Fee", new TextBlock { Text = "None (voting operations are free)" }));
        if (NeedsPassphrase && wallet is not null)
        {
            Form.Children.Add(new TextBlock { Text = $"Password for “{wallet.Alias}”" });
            Form.Children.Add(BoundPasswordBox(() => passphrase, value => passphrase = value, Validate));
        }
        if (SignsOnLedger) Form.Children.Add(Note("Your Ledger will show this operation for approval."));
    }

    // ---- Actions ---------------------------------------------------------------------------------

    private async Task Load()
    {
        if (model.DelegateInfo?.IsBaker != true) return;
        loadError = null;
        try { info = await model.GovernanceInfoForSelected(); }
        catch (Exception e) { loadError = $"Could not read the voting period: {e.Message}"; }
        if (mode is Mode.Overview && tab == Tab.Tezos) SetMode(mode);
    }

    private void StartConfirm(GovernanceOperation operation)
    {
        ShowError(null);
        SetMode(new Mode.Confirm(operation));
    }

    private async Task Perform(GovernanceOperation operation)
    {
        ShowError(null);
        SetMode(new Mode.Working(operation));
        try
        {
            var result = await model.PerformGovernance(operation, NeedsPassphrase ? passphrase : null);
            selected.Clear();
            newProposal = "";
            SetMode(new Mode.Done(result.Hash, result.Level, operation));
            await Load();
        }
        catch (Exception e)
        {
            SetMode(new Mode.Confirm(operation));
            ShowError(e.Message);
        }
    }
}
