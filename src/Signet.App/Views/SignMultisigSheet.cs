using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Signet.Models;
using Signet.ViewModels;

namespace Signet.App.Views;

/// <summary>
/// Proposes an action from a multisig (a transfer, or a change of delegate) and signs it with one
/// of our keys. The signature is shown for handing to whoever submits; the proposal is kept so more
/// signatures can be gathered.
/// </summary>
public sealed partial class SignMultisigSheet : Sheet
{
    private enum Kind { Transfer, Delegate, Undelegate }
    private static readonly (Kind Kind, string Title)[] Kinds = [(Kind.Transfer, "Transfer"), (Kind.Delegate, "Set delegate"), (Kind.Undelegate, "Remove delegate")];

    private readonly WalletViewModel model;
    private readonly IReadOnlyList<MultisigContract> contracts;
    private readonly ComboBox contractPicker = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel stateLine = new() { HorizontalAlignment = HorizontalAlignment.Right };
    private readonly StackPanel transactionArea = new() { Spacing = 8 };
    private readonly ComboBox existingPicker = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel actionArea = new() { Spacing = 8 };
    private readonly StackPanel kindPicker = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly StackPanel kindFields = new() { Spacing = 8 };
    private readonly TextBox amountText = new() { PlaceholderText = "0.0" };
    private readonly TextBox destination = new() { PlaceholderText = "tz1… or KT1…", FontFamily = new FontFamily("Cascadia Mono, Consolas"), IsSpellCheckEnabled = false };
    private readonly TextBox baker = new() { PlaceholderText = "tz1…", FontFamily = new FontFamily("Cascadia Mono, Consolas"), IsSpellCheckEnabled = false };
    private readonly StackPanel bakerKnown = new();
    private readonly ContentControl destinationMenu = new();
    private readonly ContentControl bakerMenu = new();
    private readonly ComboBox signerPicker = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel passphraseArea = new() { Spacing = 8 };
    private readonly PasswordBox passphrase = new();
    private IReadOnlyList<BakerCandidate> bakers = [];
    private MultisigContract? contract;
    private MultisigInfo? info;
    private string? infoError;
    private IReadOnlyList<MultisigProposal> pending = [];
    private MultisigProposal? existing;
    private IReadOnlyList<Wallet> signers = [];
    private Kind kind = Kind.Transfer;
    private int infoVersion;
    private bool isWorking;
    private bool done;

    public SignMultisigSheet(WalletViewModel model) : base("Sign multisig transaction", 560)
    {
        this.model = model;
        contracts = model.MultisigContracts;
        if (contracts.Count == 0)
        {
            Form.Children.Add(Note("No multisig yet. Use Create multisig… or Add multisig… first."));
            CloseButtonText = "Close";
            DefaultButton = ContentDialogButton.Close;
            return;
        }

        foreach (var c in contracts) contractPicker.Items.Add($"{c.Alias}  {c.Address.Shortened()}");
        Form.Children.Add(new TextBlock { Text = "Multisig" });
        Form.Children.Add(contractPicker);
        Form.Children.Add(LabeledContent("State", stateLine));
        Form.Children.Add(transactionArea);
        transactionArea.Children.Add(new TextBlock { Text = "Transaction" });
        transactionArea.Children.Add(existingPicker);

        foreach (var (k, title) in Kinds)
        {
            var radio = new RadioButton { Content = title, GroupName = "multisig-kind", Tag = k, MinWidth = 0, Margin = new Thickness(0, 0, 12, 0) };
            radio.Checked += (_, _) => { kind = (Kind)radio.Tag; DrawKindFields(); Validate(); };
            kindPicker.Children.Add(radio);
        }
        actionArea.Children.Add(kindPicker);
        actionArea.Children.Add(kindFields);
        BuildKindFields();
        Form.Children.Add(actionArea);

        Form.Children.Add(new TextBlock { Text = "Sign with" });
        Form.Children.Add(signerPicker);
        Form.Children.Add(passphraseArea);
        Form.Children.Add(Note("Nothing is sent yet. Your signature goes to whoever submits the transaction, together with the other signers' signatures."));

        CloseButtonText = "Cancel";
        Primary("Sign", Sign);
        contractPicker.SelectionChanged += async (_, _) =>
        {
            contract = contractPicker.SelectedIndex >= 0 ? contracts[contractPicker.SelectedIndex] : null;
            await LoadInfo();
        };
        existingPicker.SelectionChanged += (_, _) =>
        {
            existing = existingPicker.SelectedIndex > 0 ? pending[existingPicker.SelectedIndex - 1] : null;
            actionArea.Visibility = existing is null ? Visibility.Visible : Visibility.Collapsed;
            Validate();
        };
        signerPicker.SelectionChanged += (_, _) => { DrawPassphrase(); Validate(); };
        passphrase.PasswordChanged += (_, _) => Validate();
        amountText.TextChanged += (_, _) => Validate();
        destination.TextChanged += (_, _) => Validate();
        baker.TextChanged += (_, _) => { DrawBakerKnown(); Validate(); };
        Closing += (_, args) => { if (isWorking) args.Cancel = true; };
        Opened += async (_, _) =>
        {
            bakers = await model.Bakers();
            if (kind == Kind.Delegate) DrawKindFields();
        };

        // Where to start: the delegate row's contract, the selected contract, or the first one.
        if (model.SignMultisigPreset is { } preset)
        {
            contract = preset.Contract;
            kind = preset.Delegating ? Kind.Delegate : Kind.Transfer;
        }
        else if (model.SelectedWallet is { Address.IsContract: true } selected)
        {
            contract = contracts.FirstOrDefault(c => c.Address == selected.Address);
        }
        contract ??= contracts[0];
        ((RadioButton)kindPicker.Children[(int)kind]).IsChecked = true;
        var index = contracts.ToList().FindIndex(c => c.Address == contract.Address);
        contractPicker.SelectedIndex = Math.Max(0, index);
    }

    private decimal? Amount => ParseTez(amountText.Text) is > 0 and var value ? value : null;
    private Address DestinationAddress => new(destination.Text.Trim());
    private Address BakerAddress => new(baker.Text.Trim());
    private Wallet? Signer => signerPicker.SelectedIndex >= 0 && signerPicker.SelectedIndex < signers.Count ? signers[signerPicker.SelectedIndex] : null;
    private bool NeedsPassphrase => Signer?.KeyKind == KeyKind.Encrypted;
    private bool SignsOnLedger => Signer?.KeyKind == KeyKind.Ledger;

    /// <summary>What the form describes, when it is complete.</summary>
    private MultisigAction? Action => kind switch
    {
        Kind.Transfer when Amount is { } amount && DestinationAddress.IsValidAccount => new MultisigAction.Transfer(Mutez.FromTez(amount), DestinationAddress.Value),
        Kind.Delegate when BakerAddress.IsValidAccount && !BakerAddress.IsContract => new MultisigAction.SetDelegate(BakerAddress.Value),
        Kind.Undelegate => new MultisigAction.SetDelegate(null),
        _ => null,
    };

    protected override void Validate() =>
        IsPrimaryButtonEnabled = done || contract is not null && info?.IsGenericMultisig == true && Signer is not null && !isWorking
            && !(NeedsPassphrase && passphrase.Password.Length == 0) && (existing is not null || Action is not null);

    private async Task LoadInfo()
    {
        var version = ++infoVersion;
        info = null;
        infoError = null;
        existing = null;
        DrawState();
        DrawPending();
        DrawSigners();
        if (contract is not { } c) return;
        try
        {
            var result = await model.MultisigInfo(c);
            if (version != infoVersion) return;
            info = result;
        }
        catch (Exception e)
        {
            if (version != infoVersion) return;
            infoError = e.Message;
        }
        DrawState();
        DrawSigners();
        DrawKindFields();
        Validate();
    }

    private void DrawState()
    {
        stateLine.Children.Clear();
        if (info is { } i)
        {
            stateLine.Children.Add(i.IsGenericMultisig
                ? new TextBlock { Text = $"{i.Threshold} of {i.Keys.Count} signatures · {AssetBalance.Format(i.Balance, "tz")} · counter {i.Counter}", TextWrapping = TextWrapping.Wrap }
                : Label("Not an octez-client multisig", "", "SystemFillColorCautionBrush"));
        }
        else if (infoError is not null)
        {
            stateLine.Children.Add(new TextBlock { Text = infoError, Style = (Style)Application.Current.Resources["SecondaryText"], TextWrapping = TextWrapping.Wrap });
        }
        else
        {
            stateLine.Children.Add(new ProgressRing { Width = 16, Height = 16, IsActive = true });
        }
    }

    private void DrawPending()
    {
        pending = model.CurrentMultisigProposals.Where(p => p.ContractAddress == contract?.Address.Value).ToList();
        existingPicker.Items.Clear();
        existingPicker.Items.Add("New");
        foreach (var p in pending) existingPicker.Items.Add($"{p.Summary} · {p.Signatures.Count}/{p.Threshold} signed");
        existingPicker.SelectedIndex = 0;
        transactionArea.Visibility = pending.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        actionArea.Visibility = Visibility.Visible;
    }

    private void DrawSigners()
    {
        var previous = Signer;
        signers = info?.Signers(model.Wallets) ?? [];
        signerPicker.Items.Clear();
        if (signers.Count == 0) signerPicker.PlaceholderText = "None of your accounts is a signer";
        foreach (var w in signers) signerPicker.Items.Add($"{w.Alias}  {w.Address.Shortened()}");
        var keep = previous is null ? -1 : signers.ToList().FindIndex(w => w.Id == previous.Id);
        signerPicker.SelectedIndex = keep >= 0 ? keep : signers.Count > 0 ? 0 : -1;
        DrawPassphrase();
    }

    private void DrawPassphrase()
    {
        passphraseArea.Children.Clear();
        if (NeedsPassphrase && Signer is { } signer)
        {
            passphraseArea.Children.Add(new TextBlock { Text = $"Password for “{signer.Alias}”" });
            passphraseArea.Children.Add(passphrase);
        }
    }

    /// <summary>
    /// The three action forms are built once and only shown or hidden: moving a TextBox from one
    /// parent to another is what crashed this sheet (0x800F1000 from <c>UIElementCollection.Add</c>,
    /// because WinUI had not let go of the old parent). Only the Choose… menus are rebuilt, as their
    /// lists change with the contract and the bakers.
    /// </summary>
    private void BuildKindFields()
    {
        var transfer = new StackPanel { Spacing = 8 };
        transfer.Children.Add(new TextBlock { Text = "Amount (tez)" });
        transfer.Children.Add(amountText);
        transfer.Children.Add(new TextBlock { Text = "To" });
        transfer.Children.Add(FieldWithMenu(destination, destinationMenu));
        var @delegate = new StackPanel { Spacing = 8 };
        @delegate.Children.Add(new TextBlock { Text = "Baker" });
        @delegate.Children.Add(FieldWithMenu(baker, bakerMenu));
        @delegate.Children.Add(bakerKnown);
        kindFields.Children.Add(transfer);
        kindFields.Children.Add(@delegate);
        kindFields.Children.Add(Note("The multisig's tez will no longer be delegated."));
    }

    private void DrawKindFields()
    {
        for (var i = 0; i < kindFields.Children.Count; i++)
            kindFields.Children[i].Visibility = i == (int)kind ? Visibility.Visible : Visibility.Collapsed;
        var candidates = model.Wallets.Where(w => w.Address != contract?.Address).ToList();
        destinationMenu.Content = ChooseMenu("Choose…",
            ("My accounts", candidates.Where(w => w.KeyKind != KeyKind.None).Select(w => Pick(w, destination))),
            ("Address book", candidates.Where(w => w.KeyKind == KeyKind.None).Select(w => Pick(w, destination))));
        bakerMenu.Content = ChooseMenu("Choose…",
            ("My accounts", model.Wallets.Where(w => w.KeyKind != KeyKind.None && !w.Address.IsContract).Select(w => Pick(w, baker))),
            ("Bakers", bakers.Take(40).Select(b => ($"{b.Alias ?? b.Address.Shortened()}  {b.Address.Shortened()}", (Action)(() => baker.Text = b.Address.Value), true))));
        DrawBakerKnown();

        static (string, Action, bool) Pick(Wallet w, TextBox field) => ($"{w.Alias}  {w.Address.Shortened()}", () => field.Text = w.Address.Value, true);
    }

    private static Grid FieldWithMenu(TextBox field, ContentControl menu)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(field);
        Grid.SetColumn(menu, 1);
        row.Children.Add(menu);
        return row;
    }

    private void DrawBakerKnown()
    {
        bakerKnown.Children.Clear();
        if (!BakerAddress.IsValidAccount || bakers.FirstOrDefault(b => b.Address == BakerAddress) is not { } known) return;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new AccountAvatarView(known.Address, 22));
        row.Children.Add(new TextBlock { Text = known.Alias ?? known.Address.Shortened(), VerticalAlignment = VerticalAlignment.Center });
        bakerKnown.Children.Add(LabeledContent("Baker", row));
    }

    private async Task Sign()
    {
        if (contract is not { } c || Signer is not { } signer) return;
        isWorking = true;
        ShowProgress(SignsOnLedger ? "Sign on your Ledger…" : "Signing…");
        try
        {
            MultisigProposal proposal;
            if (existing is not null) proposal = existing;
            else if (Action is { } action) proposal = await model.ProposeMultisig(action, c);
            else return;
            var signature = await model.SignMultisigProposal(proposal, signer, NeedsPassphrase ? passphrase.Password : null);
            var updated = model.MultisigProposals.FirstOrDefault(p => p.Id == proposal.Id) ?? proposal;
            KeepOpen = true;
            Done(updated, signature);
        }
        finally
        {
            isWorking = false;
            ShowProgress(null);
        }
    }

    private void Done(MultisigProposal proposal, MultisigSignature signature)
    {
        done = true;
        Form.Children.Clear();
        Form.Children.Add(Success($"Signed: {proposal.Summary} from “{proposal.ContractAlias}”"));
        Form.Children.Add(Note($"{proposal.Signatures.Count} of {proposal.Threshold} signatures gathered (counter {proposal.Counter})."));
        Form.Children.Add(new TextBlock { Text = "Your signature" });
        Form.Children.Add(CopyableLine(signature.Signature, "signature"));
        Form.Children.Add(Note(proposal.IsReady
            ? "Enough signatures: use Submit multisig transaction… to send it."
            : "Give this signature to whoever will submit, or collect the others' and submit it yourself from Submit multisig transaction…."));
        CloseButtonText = "";
        Primary("Done", () => Task.CompletedTask);
    }
}
