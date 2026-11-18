using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Signet.Models;
using Signet.ViewModels;

namespace Signet.App.Views;

/// <summary>
/// Adds an account whose key is in AirGap Vault, from the Vault's "Pair Account" code that Scan
/// QR code read. Like a Ledger account, Signet keeps only the address and public key; the secret
/// never leaves the phone, and operations are signed there (<c>AirGapSigning</c>).
/// </summary>
public sealed partial class AddAirGapAccountSheet : Sheet
{
    private readonly WalletViewModel model;
    private readonly ComboBox picker = new() { Header = "Account", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox alias = new() { Header = "Name", PlaceholderText = "e.g. Savings", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel addressRow = new() { Orientation = Orientation.Horizontal, Spacing = 10 };
    private readonly StackPanel details = new() { Spacing = 8 };
    private readonly Dictionary<AirGap.Account, Address> addresses = [];
    private AirGap.Account chosen;

    public AddAirGapAccountSheet(WalletViewModel model, IReadOnlyList<AirGap.Account> accounts) : base("Add AirGap Vault account", 520)
    {
        this.model = model;
        chosen = accounts[0];
        var label = accounts[0].GroupLabel.Trim();
        alias.Text = label.Length == 0 ? "AirGap Vault" : label;

        if (accounts.Count > 1)
        {
            foreach (var account in accounts)
                picker.Items.Add(new ComboBoxItem { Content = $"{(account.GroupLabel.Length == 0 ? "Account" : account.GroupLabel)} ({account.DerivationPath})", Tag = account });
            picker.SelectedIndex = 0;
            picker.SelectionChanged += (_, _) =>
            {
                if (picker.SelectedItem is ComboBoxItem { Tag: AirGap.Account account }) chosen = account;
                Render();
            };
            Form.Children.Add(picker);
        }
        Form.Children.Add(alias);
        Form.Children.Add(LabeledContent("Address", addressRow));
        Form.Children.Add(details);
        Form.Children.Add(Note("The secret key stays in AirGap Vault on your phone. Signet keeps only the address and public key, as it does for a Ledger. Check the address against the one the Vault shows before you use it."));
        Form.Children.Add(Label("To send, Signet shows each operation as a QR code for the Vault to scan; you approve it on the phone and hold its signed reply up to this PC's camera. Signing messages for dApps or multisig proposals is not available with the Vault.", ""));

        CloseButtonText = "Cancel";
        Primary("Add Account", () => model.AddAirGapAccount(alias.Text, chosen));
        alias.TextChanged += (_, _) => Validate();
        Opened += async (_, _) =>
        {
            alias.Focus(FocusState.Programmatic);
            foreach (var account in accounts)
            {
                try { addresses[account] = await model.AddressForScannedPublicKey(account.PublicKey); }
                catch (Exception) { ShowError("Signet could not read the public key in the Vault's code."); }
            }
            Render();
        };
        Render();
    }

    private Address? ChosenAddress => addresses.TryGetValue(chosen, out var address) ? address : null;
    private Wallet? Existing => ChosenAddress is { } a ? model.Wallets.FirstOrDefault(w => w.Address == a) : null;

    protected override void Validate() =>
        IsPrimaryButtonEnabled = alias.Text.Trim().Length > 0 && ChosenAddress is not null && Existing is null;

    private void Render()
    {
        addressRow.Children.Clear();
        if (ChosenAddress is { } address)
        {
            addressRow.Children.Add(new AccountAvatarView(address, 32));
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock { Text = address.Value, Style = (Style)Application.Current.Resources["MonoText"], IsTextSelectionEnabled = true });
            if (Existing is { } existing)
                text.Children.Add(new TextBlock { Text = $"Already in your list as “{existing.Alias}”", FontSize = 12, Foreground = Secondary });
            else
                text.Children.Add(new TextBlock { Text = AddressScheme.Tz1.DisplayName(), FontSize = 12, Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"] });
            addressRow.Children.Add(text);
        }
        else
        {
            addressRow.Children.Add(new ProgressRing { IsActive = true, Width = 16, Height = 16 });
        }

        details.Children.Clear();
        details.Children.Add(LabeledContent("Derivation path", new TextBlock { Text = chosen.DerivationPath, Style = (Style)Application.Current.Resources["MonoText"] }));
        if (chosen.MasterFingerprint.Length > 0)
            details.Children.Add(LabeledContent("Vault fingerprint", new TextBlock { Text = chosen.MasterFingerprint, Style = (Style)Application.Current.Resources["MonoText"] }));
        Validate();
    }
}
