using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Signet.Models;
using Signet.ViewModels;

namespace Signet.App.Views;

/// <summary>
/// Deploys an octez-client generic multisig: a name, a threshold and the signers, paid for by
/// the selected account. Signers are our accounts, address-book entries, or pasted public keys
/// or tz addresses; an address is resolved to the key it has revealed on chain.
/// </summary>
public sealed partial class CreateMultisigSheet : Sheet
{
    /// <summary>One line of the signer list: where it came from and the key it resolved to.</summary>
    private sealed class Signer(Wallet? wallet, string? pasted)
    {
        public Wallet? Wallet { get; } = wallet;
        public string? Pasted { get; } = pasted;
        public string? Key { get; set; }
        public string? Error { get; set; }
        public string Id => Wallet is { } w ? $"wallet:{w.Id}" : $"pasted:{Pasted}";
        public string Title => Wallet?.Alias ?? (WalletViewModel.IsPublicKey(Pasted!) ? "Public key" : new Address(Pasted!).Shortened());
        public string Detail => Wallet?.Address.Shortened() ?? $"{Pasted![..Math.Min(14, Pasted!.Length)]}…{Pasted![^Math.Min(6, Pasted!.Length)..]}";
    }

    private readonly WalletViewModel model;
    private readonly Wallet? payer;
    private readonly List<Signer> signers = [];
    private readonly TextBox alias = new() { PlaceholderText = "e.g. treasury" };
    private readonly StackPanel signerRows = new() { Spacing = 6 };
    private readonly TextBox pasteText = new() { PlaceholderText = "edpk…, sppk…, p2pk…, BLpk… or tz1…", FontFamily = new FontFamily("Cascadia Mono, Consolas"), IsSpellCheckEnabled = false };
    private readonly Button pasteAdd = new() { Content = "Add" };
    private readonly NumberBox threshold = new() { Value = 2, Minimum = 1, Maximum = 1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, SmallChange = 1 };
    private readonly TextBlock thresholdText = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox amountText = new() { Text = "0", Width = 110, TextAlignment = TextAlignment.Right };
    private readonly TextBlock amountNote = new() { TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["CaptionSecondaryText"] };
    private readonly StackPanel costRows = new() { Spacing = 8 };
    private readonly PasswordBox passphrase = new();
    private readonly TextBlock summary = new() { Style = (Style)Application.Current.Resources["NoteText"] };
    private TransferEstimate? estimate;
    private string? estimateError;
    private int estimateVersion;
    private bool isWorking;

    public CreateMultisigSheet(WalletViewModel model) : base("Create multisig", 600)
    {
        this.model = model;
        payer = model.SelectedWallet;

        Form.Children.Add(new TextBlock { Text = "Name" });
        Form.Children.Add(alias);

        Form.Children.Add(new TextBlock { Text = "Signers", Style = (Style)Application.Current.Resources["SheetTitle"], Margin = new Thickness(0, 8, 0, 0) });
        Form.Children.Add(signerRows);
        var addRow = new Grid { ColumnSpacing = 8 };
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var mine = model.Wallets.Where(w => w.KeyKind != KeyKind.None).ToList();
        var book = model.Wallets.Where(w => w.KeyKind == KeyKind.None && !w.Address.IsContract).ToList();
        var accounts = ChooseMenu("Add account…",
            ("My accounts", mine.Select(w => ($"{w.Alias}  {w.Address.Shortened()}", (Action)(() => Add(new Signer(w, null))), true))),
            ("Address book", book.Select(w => ($"{w.Alias}  {w.Address.Shortened()}", (Action)(() => Add(new Signer(w, null))), true))));
        // Entries already listed are disabled each time the menu opens.
        ((MenuFlyout)accounts.Flyout).Opening += (flyout, _) =>
        {
            foreach (var item in ((MenuFlyout)flyout!).Items.OfType<MenuFlyoutItem>())
                if (model.Wallets.FirstOrDefault(w => $"{w.Alias}  {w.Address.Shortened()}" == item.Text) is { } w)
                    item.IsEnabled = !signers.Any(s => s.Wallet?.Id == w.Id);
        };
        addRow.Children.Add(accounts);
        Grid.SetColumn(pasteText, 1);
        addRow.Children.Add(pasteText);
        Grid.SetColumn(pasteAdd, 2);
        addRow.Children.Add(pasteAdd);
        Form.Children.Add(addRow);
        Form.Children.Add(new TextBlock
        {
            Text = "An address must have revealed its public key (sent at least one operation on this network); otherwise paste the public key.",
            Style = (Style)Application.Current.Resources["CaptionSecondaryText"], TextWrapping = TextWrapping.Wrap,
        });

        var thresholdRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        thresholdRow.Children.Add(threshold);
        thresholdRow.Children.Add(thresholdText);
        Form.Children.Add(LabeledContent("Signatures required", thresholdRow));

        var amountRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        amountRow.Children.Add(amountText);
        amountRow.Children.Add(new TextBlock { Text = "tz", Style = (Style)Application.Current.Resources["SecondaryText"], VerticalAlignment = VerticalAlignment.Center });
        Form.Children.Add(LabeledContent("Initial balance", amountRow));
        Form.Children.Add(amountNote);

        if (payer is not null)
        {
            var paid = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            paid.Children.Add(new AccountAvatarView(payer.Address, 20));
            paid.Children.Add(new TextBlock { Text = payer.Alias, VerticalAlignment = VerticalAlignment.Center });
            if (!payer.KeyKind.CanSign()) paid.Children.Add(new TextBlock { Text = "cannot sign", Style = (Style)Application.Current.Resources["ErrorText"], VerticalAlignment = VerticalAlignment.Center });
            Form.Children.Add(LabeledContent("Paid by", paid));
        }
        Form.Children.Add(costRows);
        if (NeedsPassphrase && payer is not null)
        {
            Form.Children.Add(new TextBlock { Text = $"Password for “{payer.Alias}”" });
            Form.Children.Add(passphrase);
        }
        Form.Children.Add(summary);

        CloseButtonText = "Cancel";
        Primary("Create", Create);
        alias.TextChanged += (_, _) => Validate();
        passphrase.PasswordChanged += (_, _) => Validate();
        pasteText.TextChanged += (_, _) => pasteAdd.IsEnabled = PasteLooksUsable;
        pasteText.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; AddPasted(); } };
        pasteAdd.Click += (_, _) => AddPasted();
        threshold.ValueChanged += (_, _) => Changed();
        amountText.TextChanged += (_, _) => Changed();
        Closing += (_, args) => { if (isWorking) args.Cancel = true; };
        pasteAdd.IsEnabled = false;
        Changed();
    }

    private bool NeedsPassphrase => payer?.KeyKind == KeyKind.Encrypted;
    private bool SignsOnLedger => payer?.KeyKind == KeyKind.Ledger;
    private IReadOnlyList<string> Keys => signers.Select(s => s.Key).OfType<string>().ToList();
    private int Threshold => double.IsNaN(threshold.Value) ? 0 : (int)threshold.Value;

    /// <summary>Tez to move into the contract when it is created; <c>null</c> when the field is not a number ≥ 0.</summary>
    private decimal? Amount => ParseTez(amountText.Text) is >= 0 and var value ? value : null;

    private bool IsFunded => (Amount ?? 0) > 0;
    private bool AllResolved => signers.Count > 0 && signers.All(s => s.Key is not null);

    private bool PasteLooksUsable
    {
        get
        {
            var t = pasteText.Text.Trim();
            return WalletViewModel.IsPublicKey(t) || (new Address(t).IsValidAccount && !new Address(t).IsContract);
        }
    }

    private bool done;

    protected override void Validate() =>
        IsPrimaryButtonEnabled = done || alias.Text.Trim().Length > 0 && AllResolved && Threshold >= 1 && Threshold <= Keys.Count
            && payer?.KeyKind.CanSign() == true && estimate is not null && Amount is not null
            && !(NeedsPassphrase && passphrase.Password.Length == 0) && !isWorking;

    private void AddPasted()
    {
        var text = pasteText.Text.Trim();
        if (!PasteLooksUsable || signers.Any(s => s.Id == $"pasted:{text}")) return;
        Add(new Signer(null, text));
        pasteText.Text = "";
    }

    private void Add(Signer signer)
    {
        if (signers.Any(s => s.Id == signer.Id)) return;
        signers.Add(signer);
        Changed();
        _ = Resolve(signer);
    }

    /// <summary>Fills in the key for one signer line: our own key, or the one the address revealed on chain.</summary>
    private async Task Resolve(Signer signer)
    {
        var text = signer.Wallet is { } w ? w.PublicKey ?? w.Address.Value : signer.Pasted!;
        try
        {
            var key = await model.MultisigSignerKey(text);
            if (!signers.Contains(signer)) return;
            if (signers.Any(s => s != signer && s.Key == key)) signer.Error = "Already listed";
            else signer.Key = key;
        }
        catch (Exception e)
        {
            signer.Error = e.Message;
        }
        Changed();
    }

    /// <summary>Something the estimate depends on changed: redraw and estimate again (upstream's <c>.task(id:)</c>).</summary>
    private void Changed()
    {
        threshold.Maximum = Math.Max(1, signers.Count);
        if (Threshold > threshold.Maximum) threshold.Value = threshold.Maximum;
        thresholdText.Text = $"{Threshold} of {signers.Count}";
        DrawSigners();
        amountNote.Text = Amount is null
            ? "Enter a number of tez, 0 or more."
            : "Moved into the contract from the paying account as it is created. 0 leaves it empty; it can be sent tez at any time.";
        amountNote.Foreground = (Brush)Application.Current.Resources[Amount is null ? "SystemFillColorCautionBrush" : "TextFillColorSecondaryBrush"];
        summary.Text = $"The contract holds tez; spending needs {Threshold} of the {signers.Count} signers. octez-client recognises it as a multisig too.";
        _ = RefreshEstimate();
    }

    private void DrawSigners()
    {
        signerRows.Children.Clear();
        foreach (var signer in signers)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            if (signer.Wallet is { } w) name.Children.Add(new AccountAvatarView(w.Address, 20));
            name.Children.Add(new TextBlock { Text = signer.Title, VerticalAlignment = VerticalAlignment.Center });
            name.Children.Add(new TextBlock { Text = signer.Detail, Style = (Style)Application.Current.Resources["MonoText"], Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(name);
            FrameworkElement state;
            if (signer.Error is { } error)
            {
                state = Label(error, "", "SystemFillColorCautionBrush");
                state.MaxWidth = 240;
            }
            else if (signer.Key is { } key)
            {
                // The key that will go into the contract, checked to hash to the address shown.
                state = new TextBlock { Text = key[..Math.Min(10, key.Length)] + "…", Style = (Style)Application.Current.Resources["MonoText"], FontSize = 11, Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"], VerticalAlignment = VerticalAlignment.Center };
                ToolTipService.SetToolTip(state, key);
            }
            else
            {
                state = new ProgressRing { Width = 16, Height = 16, IsActive = true };
            }
            Grid.SetColumn(state, 2);
            row.Children.Add(state);
            var remove = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, Padding = new Thickness(6), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
            ToolTipService.SetToolTip(remove, "Remove");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, $"Remove {signer.Title}");
            remove.Click += (_, _) => { signers.Remove(signer); Changed(); };
            Grid.SetColumn(remove, 3);
            row.Children.Add(remove);
            signerRows.Children.Add(row);
        }
    }

    private async Task RefreshEstimate()
    {
        var version = ++estimateVersion;
        estimate = null;
        estimateError = null;
        DrawCosts();
        Validate();
        if (!AllResolved || Threshold > Keys.Count || payer?.PublicKey is null || Amount is not { } amount) return;
        try
        {
            var result = await model.EstimateCreateMultisig(Threshold, Keys, amount);
            if (version != estimateVersion) return;
            estimate = result;
        }
        catch (Exception e)
        {
            if (version != estimateVersion) return;
            estimateError = e.Message;
        }
        DrawCosts();
        Validate();
    }

    private void DrawCosts()
    {
        costRows.Children.Clear();
        if (estimate is { } e)
        {
            costRows.Children.Add(LabeledContent("Fee", new TextBlock { Text = AssetBalance.Format(e.Fee, "tz") }));
            costRows.Children.Add(LabeledContent("Storage", new TextBlock { Text = AssetBalance.Format(e.Burn, "tz") }));
            if (Amount is > 0 and var amount)
                costRows.Children.Add(LabeledContent("Total", new TextBlock { Text = AssetBalance.Format(e.Fee + e.Burn + amount, "tz") }));
        }
        else if (AllResolved && Threshold <= Keys.Count && Amount is not null && estimateError is null)
        {
            costRows.Children.Add(LabeledContent("Fee", new ProgressRing { Width = 16, Height = 16, IsActive = true }));
        }
        if (estimateError is not null) costRows.Children.Add(new TextBlock { Text = estimateError, Style = (Style)Application.Current.Resources["ErrorText"] });
    }

    private async Task Create()
    {
        isWorking = true;
        ShowProgress(SignsOnLedger ? "Confirm the origination on your Ledger…" : "Deploying…");
        try
        {
            var contract = await model.CreateMultisig(alias.Text, Threshold, Keys, Amount ?? 0, estimate?.Limits, NeedsPassphrase ? passphrase.Password : null);
            KeepOpen = true;
            Done(contract);
        }
        finally
        {
            isWorking = false;
            ShowProgress(null);
        }
    }

    private void Done(MultisigContract contract)
    {
        Form.Children.Clear();
        done = true;
        Form.Children.Add(Success($"“{contract.Alias}” is deployed"));
        Form.Children.Add(CopyableLine(contract.Address.Value, "address"));
        Form.Children.Add(Note(IsFunded
            ? $"It was created holding {AssetBalance.Format(Amount ?? 0, "tz")}. Give the other signers the address so they can add it on their side."
            : "Send tez to that address to fund it. Give the other signers the address so they can add it on their side."));
        CloseButtonText = "";
        Primary("Done", () => Task.CompletedTask);
    }
}
