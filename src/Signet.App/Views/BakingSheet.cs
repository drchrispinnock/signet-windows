using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Signet.Models;
using Signet.ViewModels;

namespace Signet.App.Views;

/// <summary>
/// Baking: register this wallet as a baker (self-delegate), set its consensus and companion keys,
/// and its staking parameters. Read-only when Signet holds no usable key for the wallet.
/// </summary>
public sealed partial class BakingSheet : Sheet
{
    private enum KeyRole { Consensus, Companion }

    private abstract record Mode
    {
        public sealed record Overview : Mode;
        public sealed record PickKey(KeyRole Role) : Mode;
        public sealed record Parameters : Mode;
        public sealed record Confirm(StakingOperation Operation) : Mode;
        public sealed record Working(StakingOperation Operation) : Mode;
        public sealed record Done(string Hash, int Level, StakingOperation Operation) : Mode;
    }

    private readonly WalletViewModel model;
    private readonly Wallet? wallet;
    private Mode mode = new Mode.Overview();
    private TransferEstimate? estimate;
    private string passphrase = "";
    private string keyPassphrase = "";
    private Wallet? chosenKeyWallet;
    private string pastedPublicKey = "";
    private string pastedProof = "";
    private string limitText = "";
    private string edgeText = "";
    /// <summary>Redrawn in place while picking a key, so typing in the fields keeps its focus.</summary>
    private StackPanel? proofArea;
    private TextBlock? parametersProblem;
    private TextBlock? fee;
    /// <summary>Set once an operation went through, so closing the sheet refreshes the dashboard.</summary>
    public bool DidOperate { get; private set; }

    public BakingSheet(WalletViewModel model) : base("Baking", 560)
    {
        this.model = model;
        wallet = model.SelectedWallet;
        PrimaryButtonClick += async (_, args) =>
        {
            if (mode is Mode.Done) return;
            args.Cancel = true;
            var deferral = args.GetDeferral();
            try
            {
                switch (mode)
                {
                    case Mode.PickKey pick: await PrepareKeyUpdate(pick.Role); break;
                    case Mode.Parameters when ParsedParameters is { } p && wallet is not null:
                        StartConfirm(new StakingOperation.SetStakingParameters(p, wallet.Address));
                        break;
                    case Mode.Confirm confirm: await Perform(confirm.Operation); break;
                }
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
        model.PropertyChanged += DelegateInfoChanged;
        Closed += (_, _) => model.PropertyChanged -= DelegateInfoChanged;
        SetMode(new Mode.Overview());
    }

    private void DelegateInfoChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WalletViewModel.DelegateInfo) && mode is Mode.Overview) SetMode(mode);
    }

    private DelegateInfo.BakerRecord? Baker => model.DelegateInfo?.Baker;
    private bool NeedsPassphrase => wallet?.KeyKind == KeyKind.Encrypted;
    private bool SignsOnLedger => wallet?.KeyKind == KeyKind.Ledger;
    private bool CanOperate => wallet?.KeyKind.CanSign() == true;
    /// <summary>Quantumnet has no companion keys; consensus keys there may be tz6 (XMSS).</summary>
    private bool HasCompanionKeys => model.Network.Chain != "quantumnet";

    // ---- Modes -----------------------------------------------------------------------------------

    private void SetMode(Mode next)
    {
        mode = next;
        Title = next switch
        {
            Mode.PickKey { Role: KeyRole.Consensus } => "Set consensus key",
            Mode.PickKey => "Set companion key",
            Mode.Parameters => "Staking parameters",
            Mode.Confirm c => c.Operation.Title,
            Mode.Working w => w.Operation.Title,
            Mode.Done d => $"{d.Operation.Title}: done",
            _ => "Baking",
        };
        Form.Children.Clear();
        proofArea = null;
        parametersProblem = null;
        fee = null;
        if (wallet is not null) Form.Children.Add(AccountHeading(wallet));
        switch (next)
        {
            case Mode.Overview: Overview(); break;
            case Mode.PickKey pick: KeyPicker(pick.Role); break;
            case Mode.Parameters: ParametersForm(); break;
            case Mode.Confirm confirm: Confirmation(confirm.Operation); break;
            case Mode.Working working: AddWorkingRow(working.Operation.Title, SignsOnLedger); break;
            case Mode.Done done: AddIncludedInBlock(model.Network, done.Hash, done.Level); break;
        }
        (PrimaryButtonText, SecondaryButtonText, CloseButtonText) = mode switch
        {
            Mode.Overview => ("", "", "Done"),
            Mode.PickKey or Mode.Parameters => ("Continue", "Back", ""),
            Mode.Confirm => ("Confirm", "Back", ""),
            Mode.Working => ("", "", ""),
            _ => ("Done", "", ""),
        };
        DefaultButton = mode is Mode.Overview ? ContentDialogButton.Close : ContentDialogButton.Primary;
        PrimaryButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"];
        Validate();
    }

    protected override void Validate()
    {
        IsPrimaryButtonEnabled = mode switch
        {
            Mode.PickKey pick => PickedPublicKey is not null && ProofAvailable && !(pick.Role == KeyRole.Companion && !PickedIsBLS),
            Mode.Parameters => ParsedParameters is not null,
            Mode.Confirm => estimate is not null && !(NeedsPassphrase && passphrase.Length == 0),
            _ => true,
        };
        if (parametersProblem is not null)
            parametersProblem.Visibility = ParsedParameters is null && limitText.Length > 0 && edgeText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- Overview --------------------------------------------------------------------------------

    private void Overview()
    {
        if (!CanOperate) Form.Children.Add(Label(WatchOnlyNote, ""));

        Form.Children.Add(new SectionHeader("Baker"));
        if (model.DelegateInfo is null)
        {
            Form.Children.Add(new ProgressRing { Width = 16, Height = 16, IsActive = true, HorizontalAlignment = HorizontalAlignment.Left });
            return;
        }
        if (Baker is not { } baker)
        {
            Form.Children.Add(Note(CanOperate
                ? "This account is not a baker. Registering self-delegates it; baking rights need at least the protocol's minimum stake and a running baker daemon."
                : "This address is not a baker."));
            if (CanOperate) Form.Children.Add(Row(MakeButton("Register as a baker…", () => StartConfirm(new StakingOperation.RegisterAsBaker()))));
            return;
        }
        Form.Children.Add(LabeledContent("Status", new TextBlock { Text = baker.Deactivated ? "Registered, deactivated" : "Registered, active" }));
        if (baker.GracePeriod is { } grace) Form.Children.Add(LabeledContent("Grace period until cycle", new TextBlock { Text = grace.ToString(CultureInfo.InvariantCulture) }));

        Form.Children.Add(new SectionHeader("Consensus key"));
        KeyRows(baker.ConsensusKey, baker.PendingConsensusKeys, "Baker's own key");
        if (CanOperate) Form.Children.Add(Row(MakeButton("Set consensus key…", () => { ResetKeyPicker(); SetMode(new Mode.PickKey(KeyRole.Consensus)); })));
        Form.Children.Add(Note(HasCompanionKeys
            ? "The key the baker daemon signs blocks and attestations with. A tz4 (BLS) key needs its proof of possession."
            : "The key the baker daemon signs with. On Quantumnet this can be a post-quantum tz6 (XMSS) key."));

        if (HasCompanionKeys)
        {
            Form.Children.Add(new SectionHeader("Companion key"));
            KeyRows(baker.CompanionKey, baker.PendingCompanionKeys, "None");
            if (CanOperate) Form.Children.Add(Row(MakeButton("Set companion key…", () => { ResetKeyPicker(); SetMode(new Mode.PickKey(KeyRole.Companion)); })));
            Form.Children.Add(Note("A tz4 (BLS) key used for DAL attestations alongside a non-BLS consensus key."));
        }

        Form.Children.Add(new SectionHeader("Staking parameters"));
        if (baker.StakingParameters is { } p)
        {
            Form.Children.Add(LabeledContent("Limit of staking over baking", new TextBlock { Text = $"{Formatted(p.LimitMultiplier)}×" }));
            Form.Children.Add(LabeledContent("Edge of baking over staking", new TextBlock { Text = $"{Formatted(p.EdgePercent)} %" }));
        }
        else
        {
            Form.Children.Add(LabeledContent("Active", new TextBlock { Text = "Defaults (no staking accepted)" }));
        }
        foreach (var pending in baker.PendingStakingParameters)
            Form.Children.Add(LabeledContent($"Pending from cycle {pending.Cycle?.ToString(CultureInfo.InvariantCulture) ?? "?"}",
                new TextBlock { Text = $"{Formatted(pending.LimitMultiplier)}× · {Formatted(pending.EdgePercent)} %" }));
        if (CanOperate)
        {
            Form.Children.Add(Row(MakeButton("Change…", () =>
            {
                limitText = Formatted(baker.StakingParameters?.LimitMultiplier ?? 0);
                edgeText = Formatted(baker.StakingParameters?.EdgePercent ?? 0);
                SetMode(new Mode.Parameters());
            })));
        }
        Form.Children.Add(Note("The limit caps how much stake you accept from others, as a multiple of your own (0 = none, up to 9×). The edge is the share of stakers' rewards you keep (0 to 100 %). Changes apply after a few cycles."));
    }

    private string WatchOnlyNote => wallet?.KeyKind == KeyKind.Remote
        ? "Watch only: this baker signs through a remote signer. Its keys and parameters are shown but cannot be changed here."
        : "Watch only: Signet holds just this baker's public key. Its keys and parameters are shown but cannot be changed here.";

    private void KeyRows(Address? current, IReadOnlyList<Address> pending, string fallback)
    {
        if (current is { } key)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(new AccountAvatarView(key, 20));
            row.Children.Add(new TextBlock { Text = model.DisplayName(key, null).Name, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = key.Shortened(), Style = (Style)Application.Current.Resources["MonoText"], Foreground = Secondary, VerticalAlignment = VerticalAlignment.Center });
            Form.Children.Add(LabeledContent("Active", row));
        }
        else
        {
            Form.Children.Add(LabeledContent("Active", new TextBlock { Text = fallback, Foreground = Secondary }));
        }
        foreach (var next in pending)
            Form.Children.Add(LabeledContent("Pending", new TextBlock { Text = next.Shortened(), Style = (Style)Application.Current.Resources["MonoText"], Foreground = Secondary }));
    }

    // ---- Key picker ------------------------------------------------------------------------------

    private void ResetKeyPicker()
    {
        chosenKeyWallet = null;
        pastedPublicKey = "";
        pastedProof = "";
        keyPassphrase = "";
    }

    private IReadOnlyList<Wallet> EligibleWallets(KeyRole role) =>
        model.Wallets.Where(w => w.PublicKey is not null && w.Address != wallet?.Address
            // consensus: any scheme the network allows
            && (role != KeyRole.Companion || w.Scheme == AddressScheme.Tz4)).ToList();

    private string? PickedPublicKey
    {
        get
        {
            if (chosenKeyWallet?.PublicKey is { } key) return key;
            var pasted = pastedPublicKey.Trim();
            return pasted.Length == 0 ? null : pasted;
        }
    }

    private bool PickedIsBLS => PickedPublicKey?.StartsWith("BLpk", StringComparison.Ordinal) == true;
    private bool ProofNeeded => PickedIsBLS;
    private bool ProvesFromOwnKey => chosenKeyWallet is { Scheme: AddressScheme.Tz4 } w && w.KeyKind.CanSign();

    private bool ProofAvailable
    {
        get
        {
            if (!ProofNeeded) return true;
            if (ProvesFromOwnKey) return chosenKeyWallet!.KeyKind != KeyKind.Encrypted || keyPassphrase.Length > 0;
            return pastedProof.Trim().Length > 0;
        }
    }

    private void KeyPicker(KeyRole role)
    {
        var eligible = EligibleWallets(role);
        var picker = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        picker.Items.Add("Choose…");
        foreach (var w in eligible)
            picker.Items.Add($"{w.Alias}  {w.Address.Shortened()}  ({w.Scheme.RawValue()}{(w.KeyKind.CanSign() ? "" : ", public key only")})");
        picker.SelectedIndex = chosenKeyWallet is null ? 0 : eligible.ToList().FindIndex(w => w.Id == chosenKeyWallet.Id) + 1;
        Form.Children.Add(new TextBlock { Text = "From your accounts" });
        Form.Children.Add(picker);

        Form.Children.Add(new TextBlock { Text = "Or paste a public key" });
        var pasted = BoundTextBox(() => pastedPublicKey, value => pastedPublicKey = value,
            role == KeyRole.Companion ? "BLpk…" : "edpk… / sppk… / p2pk… / BLpk… / mdpk… / xmpk…", monospaced: true);
        Form.Children.Add(pasted);
        proofArea = new StackPanel { Spacing = 8 };
        Form.Children.Add(proofArea);
        Form.Children.Add(Note(role == KeyRole.Consensus ? "Takes effect after the consensus-rights delay (a few cycles)." : "Companion keys must be tz4 (BLS)."));

        picker.SelectionChanged += (_, _) =>
        {
            chosenKeyWallet = picker.SelectedIndex > 0 ? eligible[picker.SelectedIndex - 1] : null;
            if (chosenKeyWallet is not null && pasted.Text.Length > 0) pasted.Text = "";
            DrawProof();
        };
        pasted.TextChanged += (_, _) =>
        {
            if (pastedPublicKey.Length > 0 && picker.SelectedIndex != 0) picker.SelectedIndex = 0;
            DrawProof();
        };
        DrawProof();
    }

    /// <summary>The proof-of-possession part of the key picker, redrawn when the chosen key changes.</summary>
    private void DrawProof()
    {
        if (proofArea is null) return;
        proofArea.Children.Clear();
        if (ProofNeeded)
        {
            if (ProvesFromOwnKey && chosenKeyWallet is { } w)
            {
                if (w.KeyKind == KeyKind.Encrypted)
                {
                    proofArea.Children.Add(new TextBlock { Text = $"Password for “{w.Alias}” (to prove possession)" });
                    proofArea.Children.Add(BoundPasswordBox(() => keyPassphrase, value => keyPassphrase = value, Validate));
                }
                else
                {
                    proofArea.Children.Add(Note("Signet will produce the BLS proof of possession from this account's key."));
                }
            }
            else
            {
                proofArea.Children.Add(new TextBlock { Text = "Proof of possession" });
                proofArea.Children.Add(BoundTextBox(() => pastedProof, value => pastedProof = value, "BLsig…", monospaced: true, Validate));
                proofArea.Children.Add(Note("BLS keys must prove possession. Paste the proof made where the key lives, e.g. octez-client's BLS proof for that key."));
            }
        }
        Validate();
    }

    // ---- Parameters ------------------------------------------------------------------------------

    private void ParametersForm()
    {
        Form.Children.Add(new TextBlock { Text = "Limit of staking over baking" });
        Form.Children.Add(FieldWithUnit(BoundTextBox(() => limitText, value => limitText = value, changed: Validate), "× your own stake (0–9)"));
        Form.Children.Add(new TextBlock { Text = "Edge of baking over staking" });
        Form.Children.Add(FieldWithUnit(BoundTextBox(() => edgeText, value => edgeText = value, changed: Validate), "% of stakers' rewards you keep (0–100)"));
        parametersProblem = Note("The limit must be between 0 and 9 and the edge between 0 and 100.");
        Form.Children.Add(parametersProblem);
        Form.Children.Add(Note("Set the limit to 0 to stop accepting new stake. Changes take effect after a few cycles."));
    }

    private static Grid FieldWithUnit(TextBox field, string unit)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(field);
        var label = new TextBlock { Text = unit, Foreground = Secondary, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        return row;
    }

    private StakingParameters? ParsedParameters
    {
        get
        {
            static decimal? Number(string s) => decimal.TryParse(s.Trim().Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;
            return Number(limitText) is { } limit && Number(edgeText) is { } edge ? StakingParameters.FromUserValues(limit, edge) : null;
        }
    }

    private static string Formatted(decimal d) => d.ToString("0.##", CultureInfo.CurrentCulture);

    // ---- Confirm ---------------------------------------------------------------------------------

    private void Confirmation(StakingOperation operation)
    {
        Form.Children.Add(LabeledContent("Operation", new TextBlock { Text = operation.Title }));
        switch (operation)
        {
            case StakingOperation.UpdateConsensusKey consensus:
                Form.Children.Add(PublicKeyRow(consensus.PublicKey));
                break;
            case StakingOperation.UpdateCompanionKey companion:
                Form.Children.Add(PublicKeyRow(companion.PublicKey));
                break;
            case StakingOperation.SetStakingParameters set:
                Form.Children.Add(LabeledContent("Limit", new TextBlock { Text = $"{Formatted(set.Parameters.LimitMultiplier)}×" }));
                Form.Children.Add(LabeledContent("Edge", new TextBlock { Text = $"{Formatted(set.Parameters.EdgePercent)} %" }));
                break;
        }
        fee = new TextBlock { Text = estimate is { } known ? AssetBalance.Format(known.Fee, "tz") : "Estimating…" };
        Form.Children.Add(LabeledContent("Fee", fee));
        if (NeedsPassphrase && wallet is not null)
        {
            Form.Children.Add(new TextBlock { Text = $"Password for “{wallet.Alias}”" });
            Form.Children.Add(BoundPasswordBox(() => passphrase, value => passphrase = value, Validate));
        }
        if (SignsOnLedger) Form.Children.Add(Note("Your Ledger will show this operation for approval."));
    }

    private static Grid PublicKeyRow(string key) =>
        LabeledContent("Public key", new TextBlock { Text = key, Style = (Style)Application.Current.Resources["MonoText"], TextTrimming = TextTrimming.CharacterEllipsis });

    // ---- Actions ---------------------------------------------------------------------------------

    private async Task PrepareKeyUpdate(KeyRole role)
    {
        ShowError(null);
        if (PickedPublicKey is not { } publicKey) return;
        try
        {
            string? proof = null;
            if (PickedIsBLS)
            {
                proof = ProvesFromOwnKey && chosenKeyWallet is { } keyWallet
                    ? await model.ProofOfPossession(keyWallet, keyWallet.KeyKind == KeyKind.Encrypted ? keyPassphrase : null)
                    : pastedProof.Trim();
            }
            StartConfirm(role == KeyRole.Consensus
                ? new StakingOperation.UpdateConsensusKey(publicKey, proof)
                : new StakingOperation.UpdateCompanionKey(publicKey, proof));
        }
        catch (Exception e)
        {
            ShowError(e.Message);
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
            if (fee is not null) fee.Text = AssetBalance.Format(result.Fee, "tz");
        }
        catch (Exception e)
        {
            if (mode is not Mode.Confirm current || current.Operation != operation) return;
            ShowError(e.Message);
            if (fee is not null) fee.Text = "—";
        }
        Validate();
    }

    private async Task Perform(StakingOperation operation)
    {
        ShowError(null);
        SetMode(new Mode.Working(operation));
        try
        {
            var result = await model.PerformStaking(operation, estimate?.Limits, NeedsPassphrase ? passphrase : null);
            DidOperate = true;
            SetMode(new Mode.Done(result.Hash, result.Level, operation));
        }
        catch (Exception e)
        {
            SetMode(new Mode.Confirm(operation));
            ShowError(e.Message);
        }
    }
}
