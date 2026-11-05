using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Signet.Models;
using Signet.Services;
using Signet.ViewModels;

namespace Signet.App.Views;

/// <summary>
/// Send shielded: a private transfer from the account's shielded wallet to someone else's
/// shielded address, with an optional memo only they can read. The recipient is a payment
/// address, a JSON record another wallet printed (Signet's "Shielded address…" or the tzel
/// CLI's <c>receive</c>), pasted here or picked from the address book; the fingerprint shown is
/// what the two of you compare. The rollup applies the transfer within minutes; the recipient
/// sees the note on their next scan.
/// </summary>
public sealed partial class SendShieldedSheet : Sheet
{
    private enum Step { Compose, Working, Done }
    private const string Pasted = "pasted";

    private readonly WalletViewModel model;
    private string amountText = "";
    private string memo = "";
    private IReadOnlyList<ShieldedContact> contacts = [];
    private string chosen = Pasted;
    private string pastedJSON = "";
    private ShieldedContact? parsed;
    private string? parseError;
    private string saveName = "";
    private SendShieldedEstimate? estimate;
    private string? estimateError;
    private Step step = Step.Compose;
    private SendShieldedEstimate? submitted;
    private SendShieldedReceipt? receipt;
    private CancellationTokenSource? parsing;
    private CancellationTokenSource? estimating;
    /// <summary>The unacknowledged transfer being handed to the operator again, while it is.</summary>
    private string? resubmitting;

    private readonly ComboBox picker = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox pastedBox = new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 90, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12,
        IsSpellCheckEnabled = false, PlaceholderText = "Paste the recipient's shielded address (the JSON their wallet printed)",
    };
    private readonly StackPanel recipientInfo = new() { Spacing = 6 };
    private readonly StackPanel compose = new() { Spacing = 10 };
    private readonly StackPanel costs = new() { Spacing = 8 };
    private readonly StackPanel notices = new();
    private readonly StackPanel sent = new() { Spacing = 6 };
    private readonly StackPanel result = new() { Spacing = 10 };
    private readonly TextBlock balanceLine = new() { Foreground = Secondary };

    public SendShieldedSheet(WalletViewModel model) : base("Send shielded", 620)
    {
        this.model = model;
        if (model.SelectedWallet is { } wallet) Form.Children.Add(AccountHeading(wallet));
        Form.Children.Add(balanceLine);
        compose.Children.Add(new TextBlock { Text = "To" });
        compose.Children.Add(picker);
        compose.Children.Add(pastedBox);
        compose.Children.Add(recipientInfo);
        compose.Children.Add(new TextBlock { Text = "Amount" });
        compose.Children.Add(FieldWithUnit(BoundTextBox(() => amountText, value => amountText = value, "0", changed: Reestimate), "tz"));
        compose.Children.Add(new TextBlock { Text = "Memo" });
        compose.Children.Add(BoundTextBox(() => memo, value => memo = value, "Optional, only the recipient can read it", changed: Reestimate));
        compose.Children.Add(costs);
        compose.Children.Add(Note("Nothing on the rollup names the recipient: only their viewing keys find the note. Compare the fingerprint with them before sending; a note sent to the wrong address cannot be recovered. The change comes back as a new note of yours."));
        compose.Children.Add(notices);
        compose.Children.Add(sent);
        Form.Children.Add(compose);
        Form.Children.Add(result);

        try { contacts = model.ShieldedContacts(); } catch (Exception) { contacts = []; }
        if (contacts.FirstOrDefault() is { } first) chosen = first.Fingerprint;
        FillPicker();
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedItem is ComboBoxItem { Tag: string tag } && tag != chosen) { chosen = tag; Render(); Reestimate(); }
        };
        pastedBox.TextChanged += (_, _) => { pastedJSON = pastedBox.Text; ParsePasted(); };

        Primary("Send", Send);
        CloseButtonText = "Cancel";
        model.PropertyChanged += ModelChanged;
        Closed += (_, _) => { model.PropertyChanged -= ModelChanged; parsing?.Cancel(); estimating?.Cancel(); };
        Closing += (_, args) => { if (step == Step.Working) args.Cancel = true; };
        Render();
    }

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

    /// <summary>The recipient in use: a contact, or what was pasted once it parses.</summary>
    private ShieldedContact? Recipient => chosen == Pasted ? parsed : contacts.FirstOrDefault(c => c.Fingerprint == chosen);

    private bool CanConfirm =>
        estimate is { } e && Amount is { } amount && Recipient is { } recipient && e.Amount == amount && e.Recipient.Fingerprint == recipient.Fingerprint &&
        e.Memo == memo && e.Context == model.BridgeContext && model.CanShield && TokenIsSet && step == Step.Compose;

    public static string Short(string fingerprint) =>
        fingerprint.Length > 20 ? $"{fingerprint[..12]}…{fingerprint[^6..]}" : fingerprint;

    private void FillPicker()
    {
        picker.Items.Clear();
        foreach (var contact in contacts) picker.Items.Add(new ComboBoxItem { Content = $"{contact.Name}  {Short(contact.Fingerprint)}", Tag = contact.Fingerprint });
        picker.Items.Add(new ComboBoxItem { Content = "A pasted shielded address…", Tag = Pasted });
        picker.SelectedIndex = Math.Max(0, picker.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => (string)i.Tag == chosen));
    }

    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WalletViewModel.ShieldProgress) or nameof(WalletViewModel.ShieldedTransfers) or nameof(WalletViewModel.Assets) or nameof(WalletViewModel.ShieldedWallet))
            Render();
    }

    private void Render()
    {
        var shielded = model.Assets.FirstOrDefault(a => a.Kind is AssetBalance.AssetKind.Shielded);
        balanceLine.Text = shielded is { Unavailable: null }
            ? $"{shielded.FormattedAmount} shielded" + (model.ShieldedWallet is { } w ? $", {ShieldSheet.Tez(w.AvailableMutez)} available" : "")
            : "";
        compose.Visibility = step == Step.Compose ? Visibility.Visible : Visibility.Collapsed;
        pastedBox.Visibility = chosen == Pasted ? Visibility.Visible : Visibility.Collapsed;

        recipientInfo.Children.Clear();
        if (chosen == Pasted)
        {
            if (parseError is { } problem && pastedJSON.Trim().Length > 0) recipientInfo.Children.Add(Caution(problem));
            else if (parsed is { } contact)
            {
                recipientInfo.Children.Add(Fingerprint(contact.Fingerprint));
                if (contact.Name.Length == 0)
                {
                    var name = BoundTextBox(() => saveName, value => saveName = value, "Name for the address book");
                    name.Width = 240;
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    row.Children.Add(name);
                    row.Children.Add(MakeButton("Save", () => Save(contact)));
                    recipientInfo.Children.Add(row);
                }
                else
                {
                    recipientInfo.Children.Add(Note($"Already in the address book as “{contact.Name}”."));
                }
            }
        }
        else if (Recipient is { } contact)
        {
            recipientInfo.Children.Add(Fingerprint(contact.Fingerprint));
        }

        costs.Children.Clear();
        if (estimate is { } e)
        {
            costs.Children.Add(LabeledContent("Rollup and producer fees", new TextBlock { Text = ShieldSheet.Tez(e.Fees) }));
            costs.Children.Add(LabeledContent("Spent from notes", new TextBlock
            {
                Text = $"{ShieldSheet.Tez(e.InputMutez)} in {e.Inputs.Count} note{(e.Inputs.Count == 1 ? "" : "s")}, {ShieldSheet.Tez(e.Change)} back as change",
                TextWrapping = TextWrapping.Wrap, MaxWidth = 360, TextAlignment = TextAlignment.Right,
            }));
        }
        else if (Amount is not null && Recipient is not null)
        {
            costs.Children.Add(LabeledContent("Rollup and producer fees", estimateError is { } error ? Caution(error) : new ProgressRing { Width = 14, Height = 14, IsActive = true }));
        }

        notices.Children.Clear();
        if (!model.CanShield) notices.Children.Add(Caution("Sending needs Shielded Transactions on for this network and an account Signet can sign for."));
        else if (!TokenIsSet) notices.Children.Add(Caution("Sending needs the TzEL operator's access token. Enter it in Shielded settings."));

        RenderSent();
        result.Children.Clear();
        if (receipt is { } r)
        {
            result.Children.Add(Success($"Sent {ShieldSheet.Tez(r.Amount)} to {r.Transfer.RecipientName ?? Short(r.Transfer.RecipientFingerprint)}"));
            result.Children.Add(Note($"The operator has the transfer (submission {r.Transfer.SubmissionID}). The rollup spends your notes once it applies it, and the recipient finds theirs on their next scan."));
            if (r.Transfer.OperationHash is { } hash) result.Children.Add(ShieldSheet.OperationLine("Delivered in", hash, model.Network));
        }
        ShowProgress(step == Step.Working ? model.ShieldProgress?.Text ?? $"Sending {ShieldSheet.Tez(Amount ?? 0)}…" : null);
        (PrimaryButtonText, CloseButtonText) = step switch
        {
            Step.Done => ("Done", ""),
            Step.Working => ("Send", ""),
            _ => ("Send", "Cancel"),
        };
        Validate();
    }

    private static Grid Fingerprint(string fingerprint) =>
        LabeledContent("Fingerprint", new TextBlock { Text = Short(fingerprint), Style = (Style)Application.Current.Resources["MonoText"], IsTextSelectionEnabled = true });

    /// <summary>Transfers of this account the rollup has not applied yet, and recent ones.</summary>
    private void RenderSent()
    {
        sent.Children.Clear();
        var list = model.ShieldedTransfers;
        if (list.Count == 0) return;
        sent.Children.Add(new TextBlock { Text = "Sent", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        foreach (var transfer in list)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = ShieldSheet.Tez(transfer.Amount), VerticalAlignment = VerticalAlignment.Center });
            var to = new TextBlock { Text = $"to {transfer.RecipientName ?? Short(transfer.RecipientFingerprint)}", Foreground = Secondary, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(to, 1);
            row.Children.Add(to);
            var state = new TextBlock
            {
                Text = transfer.Applied ? "Applied by the rollup" : transfer.Submitted ? "With the operator" : "Handed to the operator, no acknowledgement received",
                FontSize = 12, Foreground = Secondary, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
            };
            Grid.SetColumn(state, 2);
            row.Children.Add(state);
            var id = transfer.Id;
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (!transfer.Applied && !transfer.Submitted)
            {
                if (resubmitting == id) buttons.Children.Add(new ProgressRing { Width = 14, Height = 14, IsActive = true });
                else
                {
                    var again = MakeButton("Submit again", () => _ = Resubmit(id), resubmitting is null && model.CanShield);
                    ToolTipService.SetToolTip(again, "Hands the same message to the operator again. The rollup cannot spend the notes twice, so a copy that landed after all is simply reported as a duplicate.");
                    buttons.Children.Add(again);
                }
            }
            if (!transfer.Applied && transfer.Created < DateTimeOffset.UtcNow.AddMinutes(-30))
            {
                var forget = MakeButton("Forget", () =>
                {
                    ShowError(null);
                    try { model.ForgetTransfer(id); }
                    catch (Exception e) { ShowError(e.Message); }
                });
                ToolTipService.SetToolTip(forget, "Drops the record and frees its notes. Only for a transfer the operator never delivered; if it lands after all, the notes show as spent on the next refresh.");
                buttons.Children.Add(forget);
            }
            if (buttons.Children.Count > 0)
            {
                Grid.SetColumn(buttons, 3);
                row.Children.Add(buttons);
            }
            sent.Children.Add(row);
        }
    }

    protected override void Validate() => IsPrimaryButtonEnabled = step == Step.Done || CanConfirm;

    // ---- Actions ---------------------------------------------------------------------------------

    private async void ParsePasted()
    {
        parsing?.Cancel();
        parsed = null;
        parseError = null;
        Reestimate();
        if (pastedJSON.Trim().Length == 0) return;
        var cancel = parsing = new CancellationTokenSource();
        try { await Task.Delay(300, cancel.Token); }
        catch (OperationCanceledException) { return; }
        try
        {
            var contact = await model.ShieldedRecipient(pastedJSON);
            if (cancel.IsCancellationRequested) return;
            parsed = contact;
        }
        catch (Exception e)
        {
            if (cancel.IsCancellationRequested) return;
            parseError = e.Message;
        }
        Reestimate();
    }

    private void Save(ShieldedContact contact)
    {
        ShowError(null);
        try
        {
            model.AddShieldedContact(contact, saveName);
            contacts = model.ShieldedContacts();
            chosen = contact.Fingerprint;
            saveName = "";
            FillPicker();
            Render();
        }
        catch (Exception e) { ShowError(e.Message); }
    }

    private async void Reestimate()
    {
        estimating?.Cancel();
        estimate = null;
        estimateError = null;
        Render();
        if (step != Step.Compose || Amount is not { } amount || Recipient is not { } recipient || !model.CanShield) return;
        var cancel = estimating = new CancellationTokenSource();
        var forMemo = memo;
        try { await Task.Delay(300, cancel.Token); }
        catch (OperationCanceledException) { return; }
        try
        {
            var made = await model.EstimateSendShielded(amount, recipient, forMemo);
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

    private async Task Send()
    {
        if (step == Step.Done) return;   // "Done"
        KeepOpen = true;
        if (estimate is not { } approved || !CanConfirm) return;
        submitted = approved;
        step = Step.Working;
        Render();
        try
        {
            receipt = await model.SendShielded(approved);
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

    private async Task Resubmit(string id)
    {
        ShowError(null);
        resubmitting = id;
        Render();
        try { await model.ResubmitTransfer(id); }
        catch (Exception e) { ShowError(e.Message); }
        finally
        {
            resubmitting = null;
            Render();
        }
    }
}
