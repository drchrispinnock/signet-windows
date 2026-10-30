using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Signet.Models;
using Signet.ViewModels;

namespace Signet.App.Views;

/// <summary>
/// Gathers the other signers' signatures on a proposal and submits it from one of our accounts.
///
/// Windows: "Discard proposal" is the dialog's secondary button; it stays open on the next proposal.
/// </summary>
public sealed partial class SubmitMultisigSheet : Sheet
{
    private readonly WalletViewModel model;
    private readonly ComboBox proposalPicker = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel details = new() { Spacing = 8 };
    private readonly TextBox pasted = new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 60,
        FontFamily = new FontFamily("Cascadia Mono, Consolas"), IsSpellCheckEnabled = false,
    };
    private readonly Button addSignatures = new() { Content = "Add signatures", HorizontalAlignment = HorizontalAlignment.Right };
    private readonly ComboBox submitterPicker = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly PasswordBox passphrase = new();
    private readonly IReadOnlyList<Wallet> submitters;
    private IReadOnlyList<MultisigProposal> proposals = [];
    private Guid? proposalID;
    private TransferEstimate? estimate;
    private int estimateVersion;
    private bool isWorking;
    private bool done;

    public SubmitMultisigSheet(WalletViewModel model) : base("Submit multisig transaction", 580)
    {
        this.model = model;
        submitters = model.Wallets.Where(w => w.KeyKind.CanSign() && w.PublicKey is not null).ToList();
        proposals = model.CurrentMultisigProposals;
        if (proposals.Count == 0)
        {
            Empty();
            return;
        }

        Form.Children.Add(new TextBlock { Text = "Transaction" });
        Form.Children.Add(proposalPicker);
        Form.Children.Add(details);
        foreach (var w in submitters) submitterPicker.Items.Add($"{w.Alias}  {w.Address.Shortened()}");

        CloseButtonText = "Cancel";
        SecondaryButtonText = "Discard proposal";
        Primary("Submit", Submit);
        SecondaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            if (Proposal is not { } proposal || isWorking) return;
            try { model.RemoveMultisigProposal(proposal); }
            catch (Exception e) { ShowError(e.Message); }
            proposals = model.CurrentMultisigProposals;
            if (proposals.Count == 0) { Empty(); return; }
            FillPicker(proposals[0].Id);
        };
        proposalPicker.SelectionChanged += (_, _) =>
        {
            proposalID = proposalPicker.SelectedIndex >= 0 ? proposals[proposalPicker.SelectedIndex].Id : null;
            Changed();
        };
        submitterPicker.SelectionChanged += (_, _) => Changed();
        passphrase.PasswordChanged += (_, _) => Validate();
        pasted.TextChanged += (_, _) => addSignatures.IsEnabled = pasted.Text.Trim().Length > 0;
        addSignatures.Click += (_, _) => AddPasted();
        addSignatures.IsEnabled = false;
        Closing += (_, args) => { if (isWorking) args.Cancel = true; };
        submitterPicker.SelectedIndex = submitters.Count > 0 ? 0 : -1;
        FillPicker(proposals[0].Id);
    }

    private MultisigProposal? Proposal => proposals.FirstOrDefault(p => p.Id == proposalID);
    private Wallet? Submitter => submitterPicker.SelectedIndex >= 0 ? submitters[submitterPicker.SelectedIndex] : null;
    private bool NeedsPassphrase => Submitter?.KeyKind == KeyKind.Encrypted;
    private bool SignsOnLedger => Submitter?.KeyKind == KeyKind.Ledger;

    protected override void Validate()
    {
        IsPrimaryButtonEnabled = done || Proposal?.IsReady == true && Submitter is not null && estimate is not null && !isWorking
            && !(NeedsPassphrase && passphrase.Password.Length == 0);
        IsSecondaryButtonEnabled = !isWorking;
    }

    private void Empty()
    {
        Form.Children.Clear();
        Form.Children.Add(Note($"No pending multisig transaction on {model.Network.Name}. Propose one with Sign multisig transaction… first."));
        Primary("", () => Task.CompletedTask);
        PrimaryButtonText = "";
        SecondaryButtonText = "";
        CloseButtonText = "Close";
        DefaultButton = ContentDialogButton.Close;
    }

    private void FillPicker(Guid select)
    {
        proposalPicker.Items.Clear();
        foreach (var p in proposals) proposalPicker.Items.Add($"{p.Summary} from {p.ContractAlias} · counter {p.Counter}");
        proposalPicker.SelectedIndex = Math.Max(0, proposals.ToList().FindIndex(p => p.Id == select));
    }

    /// <summary>The proposal, its signatures or the submitter changed: redraw and estimate again.</summary>
    private void Changed()
    {
        DrawDetails();
        _ = RefreshEstimate();
    }

    private void DrawDetails()
    {
        details.Children.Clear();
        if (Proposal is not { } proposal) return;
        details.Children.Add(LabeledContent("Signatures", new TextBlock { Text = $"{proposal.Signatures.Count} of {proposal.Threshold} needed" }));
        foreach (var sig in proposal.Signatures)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (sig.PublicKey is { } pk && model.Wallets.FirstOrDefault(w => w.PublicKey == pk) is { } mine)
                row.Children.Add(Label(mine.Alias, "", "TextFillColorPrimaryBrush"));
            else
                row.Children.Add(new TextBlock { Text = "pasted", Style = (Style)Application.Current.Resources["SecondaryText"] });
            row.Children.Add(new TextBlock { Text = sig.Signature[..Math.Min(16, sig.Signature.Length)] + "…", Style = (Style)Application.Current.Resources["MonoText"], Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
            details.Children.Add(row);
        }
        details.Children.Add(new TextBlock { Text = "Paste the other signers' signatures (one per line or separated by spaces)", TextWrapping = TextWrapping.Wrap });
        details.Children.Add(pasted);
        details.Children.Add(addSignatures);
        details.Children.Add(new TextBlock { Text = "Submit from" });
        details.Children.Add(submitterPicker);
        if (proposal.IsReady)
            details.Children.Add(LabeledContent("Fee", estimate is { } e
                ? new TextBlock { Text = AssetBalance.Format(e.Fee, "tz") }
                : new ProgressRing { Width = 16, Height = 16, IsActive = true }));
        else
            details.Children.Add(Note($"{proposal.Missing} more signature{(proposal.Missing == 1 ? "" : "s")} needed before this can be submitted."));
        if (NeedsPassphrase && Submitter is { } submitter)
        {
            details.Children.Add(new TextBlock { Text = $"Password for “{submitter.Alias}”" });
            details.Children.Add(passphrase);
        }
        details.Children.Add(Note("The submitting account pays the fee; the multisig does the rest. Signatures are checked against the multisig's keys before anything is sent."));
        Validate();
    }

    private void AddPasted()
    {
        if (Proposal is not { } proposal) return;
        ShowError(null);
        foreach (var token in pasted.Text.Split([' ', '\t', '\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            try { model.AddMultisigSignature(token, proposal); }
            catch (Exception e) { ShowError(e.Message); }
        }
        pasted.Text = "";
        proposals = model.CurrentMultisigProposals;
        Changed();
    }

    private async Task RefreshEstimate()
    {
        var version = ++estimateVersion;
        estimate = null;
        Validate();
        if (Proposal is not { IsReady: true } proposal || Submitter is not { } submitter) return;
        ShowError(null);
        try
        {
            var result = await model.EstimateSubmitMultisig(proposal, submitter);
            if (version != estimateVersion) return;
            estimate = result;
        }
        catch (Exception e)
        {
            if (version != estimateVersion) return;
            ShowError(e.Message);
        }
        DrawDetails();
    }

    private async Task Submit()
    {
        if (Proposal is not { } proposal || Submitter is not { } submitter) return;
        isWorking = true;
        Validate();
        ShowProgress(SignsOnLedger ? "Confirm on your Ledger…" : "Submitting…");
        try
        {
            var hash = await model.SubmitMultisig(proposal, submitter, estimate?.Limits, NeedsPassphrase ? passphrase.Password : null);
            KeepOpen = true;
            Done(hash);
        }
        finally
        {
            isWorking = false;
            ShowProgress(null);
        }
    }

    private void Done(string hash)
    {
        done = true;
        Form.Children.Clear();
        Form.Children.Add(Success("Submitted and included in a block"));
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = hash, Style = (Style)Application.Current.Resources["MonoText"], IsTextSelectionEnabled = true, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        if (model.Network.ExplorerURLForOperation(hash) is { } url)
        {
            var link = new HyperlinkButton { Content = "View", NavigateUri = url };
            Grid.SetColumn(link, 1);
            row.Children.Add(link);
        }
        Form.Children.Add(row);
        SecondaryButtonText = "";
        CloseButtonText = "";
        Primary("Done", () => Task.CompletedTask);
    }
}
