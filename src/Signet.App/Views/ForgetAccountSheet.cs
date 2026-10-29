using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Signet.Models;
using Signet.ViewModels;

namespace Signet.App.Views;

/// <summary>
/// Removes an account from Signet's list and key store. Everyone gets one warning; accounts whose
/// secret key Signet holds get a second, plus the password for encrypted keys.
/// </summary>
public sealed partial class ForgetAccountSheet : Sheet
{
    private readonly WalletViewModel model;
    private readonly Wallet? wallet;
    private readonly PasswordBox passphrase = new();
    private bool second;

    public ForgetAccountSheet(WalletViewModel model) : base("Forget account", 520)
    {
        this.model = model;
        wallet = model.SelectedWallet;
        CloseButtonText = "Cancel";
        passphrase.PasswordChanged += (_, _) => Validate();
        ShowFirst();
    }

    private bool HoldsSecret => wallet?.KeyKind is KeyKind.Unencrypted or KeyKind.Encrypted;
    private bool NeedsPassphrase => wallet?.KeyKind == KeyKind.Encrypted;
    private bool IsSessionOnly => wallet?.IsSessionOnly == true;
    private string BackupFolder => model.BackupDirectory ?? "the backup folder";

    private void ShowFirst()
    {
        Title = "Forget account";
        Form.Children.Clear();
        if (wallet is not null) Form.Children.Add(AccountHeading(wallet));
        Form.Children.Add(Label(FirstText, "", "SystemFillColorCautionBrush"));
        Form.Children.Add(Note("The account itself stays on the chain with everything in it; forgetting only changes what this PC knows."));
        if (HoldsSecret)
        {
            // Continue… leads to the second warning without closing the sheet.
            Primary("Continue…", () =>
            {
                KeepOpen = true;
                ShowSecond();
                return Task.CompletedTask;
            });
        }
        else
        {
            Primary("Forget", Forget);
        }
    }

    private void ShowSecond()
    {
        second = true;
        Title = "Are you sure?";
        Form.Children.Clear();
        if (wallet is not null) Form.Children.Add(AccountHeading(wallet));
        Form.Children.Add(Label(IsSessionOnly
            ? "Last chance. The key will be wiped from memory. Make sure you have the paper copy or the recovery phrase first."
            : "Last chance. The secret key will be deleted from this PC's wallet directory. Make sure you have exported it or have the recovery phrase first.",
            "", "SystemFillColorCriticalBrush"));
        if (!IsSessionOnly)
            Form.Children.Add(Label($"Earlier backups in {BackupFolder} may still contain this key. Delete those too if you need the key gone from this PC entirely.", ""));
        if (NeedsPassphrase && wallet is not null)
        {
            passphrase.Header = $"Password for “{wallet.Alias}”";
            Form.Children.Add(passphrase);
            Form.Children.Add(Note("The key is encrypted; its password is needed before it can be thrown away."));
        }
        Primary("Forget for good", Forget);
        Validate();
        if (NeedsPassphrase) passphrase.Focus(FocusState.Programmatic);
    }

    protected override void Validate()
    {
        IsPrimaryButtonEnabled = !second || !NeedsPassphrase || passphrase.Password.Length > 0;
        if (second || !HoldsSecret) PrimaryButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"];
    }

    private string FirstText
    {
        get
        {
            if (IsSessionOnly)
                return "This is a paper key held in memory for this session only; it was never written to disk. Forgetting wipes it now. Without the key or its recovery phrase elsewhere, the account and its tez are lost.";
            return wallet?.KeyKind switch
            {
                KeyKind.Unencrypted or KeyKind.Encrypted => "Signet holds this account's secret key. Forgetting deletes that key from %USERPROFILE%\\.signet. Without another copy of the key or its recovery phrase, the account and its tez are lost.",
                KeyKind.Ledger => "This account's key stays on your Ledger; Signet will only forget how to reach it. You can connect it again later.",
                KeyKind.Remote or KeyKind.Unknown => "Signet will forget this account's signer entry. The key itself lives elsewhere and is not touched.",
                _ => "This is an address-book entry. Signet will forget the name and address; nothing else changes.",
            };
        }
    }

    private Task Forget() => model.ForgetSelectedWallet(NeedsPassphrase ? passphrase.Password : null);
}
