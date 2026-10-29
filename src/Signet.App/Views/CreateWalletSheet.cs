using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Signet.Models;
using Signet.ViewModels;

namespace Signet.App.Views;

/// <summary>Names a new wallet and picks its key type, then generates and stores the key.</summary>
public sealed partial class CreateWalletSheet : Sheet
{
    private readonly WalletViewModel model;
    private readonly TextBox alias = new() { Header = "Name", PlaceholderText = "My Account" };
    private readonly ComboBox scheme = new() { Header = "Key type", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock caveat = new() { Style = (Style)Application.Current.Resources["NoteText"] };
    private readonly ToggleSwitch encrypt = new() { Header = "Encrypt with a password" };
    private readonly PasswordBox password = new() { Header = "Password" };
    private readonly PasswordBox confirmPassword = new() { Header = "Confirm password" };
    private readonly TextBlock passwordProblem = new() { Style = (Style)Application.Current.Resources["NoteText"] };
    private readonly StackPanel encrypted = new() { Spacing = 12 };
    private readonly StackPanel unprotected = new();

    private static readonly IReadOnlyList<AddressScheme> Schemes = AddressSchemeExtensions.AllCases.Where(s => s.IsSupported()).ToList();

    public CreateWalletSheet(WalletViewModel model) : base("Create account", 480)
    {
        this.model = model;
        alias.Text = model.SuggestedAlias();
        foreach (var option in Schemes)
            scheme.Items.Add(option == AddressScheme.Tz1 ? $"{option.RawValue()} · {option.DisplayName()} (recommended)" : $"{option.RawValue()} · {option.DisplayName()}");
        scheme.SelectedIndex = 0;
        // Real money defaults to encrypted; testnets default to convenience.
        encrypt.IsOn = model.Network.IsMainnet;

        encrypted.Children.Add(password);
        encrypted.Children.Add(confirmPassword);
        encrypted.Children.Add(passwordProblem);
        encrypted.Children.Add(Note("You will be asked for this password every time the key signs. It cannot be recovered; if you lose it the key is lost."));
        unprotected.Children.Add(Label($"Unencrypted keys are stored on disk unprotected. Anyone who can read {WalletFolder(model)} can spend from this account.", "", "SystemFillColorCautionBrush"));

        Form.Children.Add(alias);
        Form.Children.Add(scheme);
        Form.Children.Add(caveat);
        Form.Children.Add(encrypt);
        Form.Children.Add(encrypted);
        Form.Children.Add(unprotected);
        Form.Children.Add(Note($"The key is saved to {WalletFolder(model)} in octez-client's format, so octez-client can use it too."));

        CloseButtonText = "Cancel";
        Primary("Create", Create);
        alias.TextChanged += (_, _) => Validate();
        scheme.SelectionChanged += (_, _) => Validate();
        encrypt.Toggled += (_, _) => Validate();
        password.PasswordChanged += (_, _) => Validate();
        confirmPassword.PasswordChanged += (_, _) => Validate();
        Opened += (_, _) => { alias.Focus(FocusState.Programmatic); alias.SelectAll(); };
        Validate();
    }

    private AddressScheme SelectedScheme => Schemes[Math.Max(0, scheme.SelectedIndex)];

    private string? PasswordProblem
    {
        get
        {
            if (!encrypt.IsOn) return null;
            if (password.Password.Length < WalletViewModel.MinimumPassphraseLength) return $"Use at least {WalletViewModel.MinimumPassphraseLength} characters.";
            if (password.Password != confirmPassword.Password) return "The passwords do not match.";
            return null;
        }
    }

    protected override void Validate()
    {
        var caveatText = SelectedScheme.Caveat();
        caveat.Text = caveatText ?? "";
        caveat.Visibility = caveatText is null ? Visibility.Collapsed : Visibility.Visible;
        encrypted.Visibility = encrypt.IsOn ? Visibility.Visible : Visibility.Collapsed;
        unprotected.Visibility = encrypt.IsOn ? Visibility.Collapsed : Visibility.Visible;
        var problem = PasswordProblem;
        passwordProblem.Text = problem ?? "";
        passwordProblem.Visibility = problem is not null && password.Password.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        IsPrimaryButtonEnabled = alias.Text.Trim().Length > 0 && SelectedScheme.IsSupported() && !model.IsCreatingWallet && problem is null;
    }

    private async Task Create()
    {
        ShowProgress("Generating key…");
        try { await model.CreateWallet(alias.Text.Trim(), SelectedScheme, encrypt.IsOn ? password.Password : null); }
        finally { ShowProgress(null); }
    }
}
