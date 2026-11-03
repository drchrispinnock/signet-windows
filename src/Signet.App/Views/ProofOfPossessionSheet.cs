using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Signet.App.Services;
using Signet.Models;
using Signet.ViewModels;

namespace Signet.App.Views;

/// <summary>
/// Makes the selected tz4 account's BLS proof of possession, as <c>octez-client create bls proof
/// for &lt;alias&gt;</c> does: the key's signature over its own public key, which a baker needs when the
/// key becomes its consensus or companion key. The proof is public; the secret key never leaves
/// the signer. Encrypted keys are opened with their password first.
/// </summary>
public sealed partial class ProofOfPossessionSheet : Sheet
{
    private readonly WalletViewModel model;
    private readonly Wallet? wallet;
    private readonly PasswordBox passphrase = new();
    private readonly StackPanel values = new() { Spacing = 12 };
    private string? proof;

    public ProofOfPossessionSheet(WalletViewModel model) : base("Create proof of possession", 560)
    {
        this.model = model;
        wallet = model.SelectedWallet;
        if (wallet is not null) Form.Children.Add(AccountHeading(wallet));
        Form.Children.Add(Note("A BLS proof of possession is this account's signature over its own public key. A baker needs it to make this key its consensus or companion key, and an unrevealed tz4 account's first operation carries one. It is the same proof octez-client's “create bls proof for” prints, and it can be shared freely: it proves the key is held without exposing it."));
        Form.Children.Add(values);

        CloseButtonText = "Cancel";
        Primary(NeedsPassphrase ? "Unlock and create" : "Create", Prove);
        passphrase.PasswordChanged += (_, _) => Validate();
        passphrase.KeyDown += async (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter || passphrase.Password.Length == 0) return;
            e.Handled = true;
            try { await Prove(); }
            catch (Exception error) { ShowError(error.Message); }
        };
        Opened += async (_, _) =>
        {
            if (NeedsPassphrase) { passphrase.Focus(FocusState.Programmatic); return; }
            try { await Prove(); }
            catch (Exception error) { ShowError(error.Message); }
        };
        Closed += (_, _) => { proof = null; passphrase.Password = ""; };
        Render();
    }

    private bool NeedsPassphrase => wallet?.KeyKind == KeyKind.Encrypted;

    protected override void Validate() =>
        IsPrimaryButtonEnabled = proof is not null || !NeedsPassphrase || passphrase.Password.Length > 0;

    private async Task Prove()
    {
        if (proof is not null || wallet is null) return;  // "Done"
        ShowError(null);
        ShowProgress("Signing…");
        try
        {
            proof = await model.ProofOfPossession(wallet, NeedsPassphrase ? passphrase.Password : null);
            passphrase.Password = "";
        }
        finally { ShowProgress(null); }
        KeepOpen = true;
        Render();
    }

    private void Render()
    {
        values.Children.Clear();
        if (wallet?.PublicKey is { } publicKey) values.Children.Add(ValueRow("Public key", publicKey));
        if (proof is { } made)
        {
            values.Children.Add(ValueRow("Proof of possession", made));
            PrimaryButtonText = "Done";
            CloseButtonText = "";
        }
        else if (NeedsPassphrase && wallet is not null)
        {
            passphrase.Header = $"Password for “{wallet.Alias}”";
            values.Children.Add(passphrase);
            values.Children.Add(Note("The key is stored encrypted. Enter its password to sign the proof."));
        }
        Validate();
    }

    private static Grid ValueRow(string title, string value)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        row.Children.Add(new TextBlock
        {
            Text = value, Style = (Style)Application.Current.Resources["MonoText"], MaxWidth = 300, MaxLines = 2,
            TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis,
            IsTextSelectionEnabled = true, VerticalAlignment = VerticalAlignment.Center,
        });
        var copyIcon = new FontIcon { Glyph = "", FontSize = 14 };
        var copy = new Button { Content = copyIcon, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
        ToolTipService.SetToolTip(copy, "Copy to clipboard");
        copy.Click += async (_, _) =>
        {
            Pasteboard.SetString(value);
            copyIcon.Glyph = "";
            await Task.Delay(1500);
            copyIcon.Glyph = "";
        };
        row.Children.Add(copy);
        return LabeledContent(title, row);
    }
}
