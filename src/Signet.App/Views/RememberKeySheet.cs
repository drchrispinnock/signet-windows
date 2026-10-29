using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Signet.Models;
using Signet.ViewModels;

namespace Signet.App.Views;

/// <summary>
/// Keeps a session-only key: writes it into the wallet directory like any other account. Offered
/// only while the selected account's key would otherwise be forgotten at quit.
/// </summary>
public sealed partial class RememberKeySheet : Sheet
{
    private readonly WalletViewModel model;
    private readonly Wallet? wallet;
    private readonly ToggleSwitch protects = new() { Header = "Protect it with a password" };
    private readonly PasswordBox passphrase = new() { Header = "Password" };
    private readonly PasswordBox confirmation = new() { Header = "Confirm password" };
    private readonly StackPanel protectedFields = new() { Spacing = 12 };
    private readonly TextBlock unprotected;

    public RememberKeySheet(WalletViewModel model) : base("Remember key", 520)
    {
        this.model = model;
        wallet = model.SelectedWallet;
        var directory = model.WalletDirectory ?? "the wallet directory";
        if (wallet is not null) Form.Children.Add(AccountHeading(wallet));
        Form.Children.Add(Label($"This key is held in memory for this session only and would be forgotten when Signet quits. Remembering it writes it into {directory}, where it is kept like any other account and included in backups.",
                                "", "SystemFillColorCautionBrush"));
        unprotected = Caution("Without a password the key is written unencrypted and anyone with access to this PC's files can use it.");
        if (IsClear)
        {
            protectedFields.Children.Add(passphrase);
            protectedFields.Children.Add(confirmation);
            protectedFields.Children.Add(Note("The key is encrypted in octez-client's format; the password is asked for whenever the account signs."));
            Form.Children.Add(protects);
            Form.Children.Add(protectedFields);
            Form.Children.Add(unprotected);
        }
        else
        {
            Form.Children.Add(Note("The key is encrypted and will be stored as it is; its password stays the same."));
        }
        protects.IsOn = model.Network.IsMainnet;

        CloseButtonText = "Cancel";
        Primary("Remember", () => model.RememberSelectedWallet(IsClear && protects.IsOn ? passphrase.Password : null));
        protects.Toggled += (_, _) => Validate();
        passphrase.PasswordChanged += (_, _) => Validate();
        confirmation.PasswordChanged += (_, _) => Validate();
        Validate();
    }

    private bool IsClear => wallet?.KeyKind == KeyKind.Unencrypted;

    private string? PassphraseProblem
    {
        get
        {
            if (!IsClear || !protects.IsOn) return null;
            if (passphrase.Password.Length == 0) return "Enter a password.";
            if (passphrase.Password.Length < WalletViewModel.MinimumPassphraseLength) return $"Use at least {WalletViewModel.MinimumPassphraseLength} characters.";
            if (confirmation.Password != passphrase.Password) return "The passwords do not match.";
            return null;
        }
    }

    protected override void Validate()
    {
        protectedFields.Visibility = protects.IsOn ? Visibility.Visible : Visibility.Collapsed;
        unprotected.Visibility = protects.IsOn ? Visibility.Collapsed : Visibility.Visible;
        IsPrimaryButtonEnabled = PassphraseProblem is null && wallet?.IsSessionOnly == true;
    }
}
