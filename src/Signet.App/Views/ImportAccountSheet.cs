using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Signet.Models;
using Signet.Services;
using Signet.ViewModels;

namespace Signet.App.Views;

/// <summary>
/// Brings in an existing account from a recovery phrase or a pasted secret key. The secret is
/// derived or checked through the bridge, previewed, then written to the key store (clear, or
/// encrypted with a password like Create account), or kept in memory for this session only: a
/// paper key that is never written to disk and is forgotten when Signet quits.
/// </summary>
public sealed partial class ImportAccountSheet : Sheet
{
    private enum Source { SecretKey, Phrase }

    private readonly WalletViewModel model;
    private readonly SelectorBar source = new();
    private readonly TextBox alias = new() { Header = "Name", PlaceholderText = "Imported" };
    // Phrase
    private readonly TextBox phrase = new() { Header = "Recovery phrase", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 96, IsSpellCheckEnabled = false,
                                              FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas") };
    private readonly TextBlock phraseCheck = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly ToggleSwitch legacy = new() { Header = "Legacy fundraiser derivation (email and password)" };
    private readonly TextBox fundraiserEmail = new() { Header = "Email", IsSpellCheckEnabled = false };
    private readonly PasswordBox fundraiserPassword = new() { Header = "Fundraiser password" };
    private readonly PasswordBox phrasePassphrase = new() { Header = "Phrase passphrase (optional)" };
    private readonly ComboBox curve = new() { Header = "Key type", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumberBox account = new() { Header = "Account", Minimum = 0, Maximum = 100, Value = 0, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
    private readonly StackPanel phraseFields = new() { Spacing = 12 };
    private readonly StackPanel legacyFields = new() { Spacing = 12 };
    private readonly StackPanel standardFields = new() { Spacing = 12 };
    // Secret key
    private readonly PasswordBox secretKey = new() { Header = "Secret key", PlaceholderText = "edsk… / spsk… / p2sk… / BLsk… / mdsk… / edesk…" };
    private readonly PasswordBox keyPassphrase = new() { Header = "Password for this key" };
    private readonly StackPanel keyFields = new() { Spacing = 12 };
    private readonly StackPanel keyPassphraseFields = new() { Spacing = 12 };
    // Preview and storage
    private readonly StackPanel previewRow = new() { Spacing = 2 };
    private readonly RadioButtons keep = new() { Header = "Keep the key" };
    private readonly TextBlock sessionNote = new() { Style = (Style)Application.Current.Resources["NoteText"] };
    private readonly ToggleSwitch encrypt = new();
    private readonly PasswordBox password = new() { Header = "Password" };
    private readonly PasswordBox confirmPassword = new() { Header = "Confirm password" };
    private readonly TextBlock passwordProblem = new() { Style = (Style)Application.Current.Resources["NoteText"] };
    private readonly StackPanel encryptFields = new() { Spacing = 12 };
    private readonly StackPanel unprotected = new();

    private KeyMaterial? preview;
    private string? previewError;
    private InspectedSecretKey? inspected;
    private CancellationTokenSource? previewTask;

    public ImportAccountSheet(WalletViewModel model) : base("Import account", 560)
    {
        this.model = model;
        alias.Text = model.SuggestedAlias("Imported");
        encrypt.IsOn = model.Network.IsMainnet;

        source.Items.Add(new SelectorBarItem { Text = "Secret key", Tag = Source.SecretKey, IsSelected = true });
        source.Items.Add(new SelectorBarItem { Text = "Recovery phrase", Tag = Source.Phrase });

        // Secret key
        keyFields.Children.Add(secretKey);
        keyPassphraseFields.Children.Add(keyPassphrase);
        keyPassphraseFields.Children.Add(Note("An octez-client encrypted key. It is stored as it is and keeps this password."));
        keyFields.Children.Add(keyPassphraseFields);

        // Phrase
        foreach (var option in MnemonicCurveExtensions.AllCases) curve.Items.Add(option.Title());
        curve.SelectedIndex = 0;
        phraseFields.Children.Add(phrase);
        phraseFields.Children.Add(phraseCheck);
        phraseFields.Children.Add(legacy);
        legacyFields.Children.Add(fundraiserEmail);
        legacyFields.Children.Add(fundraiserPassword);
        legacyFields.Children.Add(Note("For keys from the 2017 fundraiser and some very old wallets. Everything else uses the standard derivation below."));
        standardFields.Children.Add(phrasePassphrase);
        standardFields.Children.Add(curve);
        standardFields.Children.Add(account);
        standardFields.Children.Add(Note("Temple, Kukai, Umami and most wallets use Ed25519 at account 0. If the address shown is not the one you expect, try another account or key type."));
        phraseFields.Children.Add(legacyFields);
        phraseFields.Children.Add(standardFields);

        // Storage
        keep.Items.Add("In the wallet directory");
        keep.Items.Add("Only until Signet quits");
        keep.SelectedIndex = 0;
        sessionNote.Text = "A paper key for one session: held in memory only, never written to the wallet directory or a backup, and gone when Signet quits (or crashes). Import it again next time.";
        encryptFields.Children.Add(password);
        encryptFields.Children.Add(confirmPassword);
        encryptFields.Children.Add(passwordProblem);
        unprotected.Children.Add(Label("Unencrypted keys are stored on disk unprotected. Anyone who can read %USERPROFILE%\\.signet can spend from this account.", "", "SystemFillColorCautionBrush"));

        Form.Children.Add(source);
        Form.Children.Add(alias);
        Form.Children.Add(keyFields);
        Form.Children.Add(phraseFields);
        Form.Children.Add(LabeledContent("Address", previewRow));
        Form.Children.Add(keep);
        Form.Children.Add(sessionNote);
        Form.Children.Add(encrypt);
        Form.Children.Add(encryptFields);
        Form.Children.Add(unprotected);

        CloseButtonText = "Cancel";
        Primary("Import", ImportAccount);

        source.SelectionChanged += (_, _) => RefreshPreview();
        phrase.TextChanged += (_, _) => RefreshPreview();
        phrasePassphrase.PasswordChanged += (_, _) => RefreshPreview();
        curve.SelectionChanged += (_, _) => RefreshPreview();
        account.ValueChanged += (_, _) => RefreshPreview();
        legacy.Toggled += (_, _) => RefreshPreview();
        fundraiserEmail.TextChanged += (_, _) => RefreshPreview();
        fundraiserPassword.PasswordChanged += (_, _) => RefreshPreview();
        secretKey.PasswordChanged += (_, _) => RefreshPreview();
        keyPassphrase.PasswordChanged += (_, _) => RefreshPreview();
        alias.TextChanged += (_, _) => Validate();
        keep.SelectionChanged += (_, _) => Validate();
        encrypt.Toggled += (_, _) => Validate();
        password.PasswordChanged += (_, _) => Validate();
        confirmPassword.PasswordChanged += (_, _) => Validate();
        Validate();
    }

    private Source SelectedSource => source.SelectedItem?.Tag is Source s ? s : Source.SecretKey;
    private bool KeepForSession => keep.SelectedIndex == 1;
    private string DerivationPath => $"44'/1729'/{(int)(double.IsNaN(account.Value) ? 0 : account.Value)}'/0'";
    private Wallet? Existing => preview is { } m ? model.Wallets.FirstOrDefault(w => w.Address.Value == m.Address) : null;
    /// <summary>An encrypted key pasted in is stored as it is; the storage password question does not apply.</summary>
    private bool KeepsOwnEncryption => SelectedSource == Source.SecretKey && inspected?.IsEncrypted == true;

    private string? PasswordProblem
    {
        get
        {
            if (!encrypt.IsOn || KeepsOwnEncryption) return null;
            if (password.Password.Length < WalletViewModel.MinimumPassphraseLength) return $"Use at least {WalletViewModel.MinimumPassphraseLength} characters.";
            if (password.Password != confirmPassword.Password) return "The passwords do not match.";
            return null;
        }
    }

    private static readonly string[] EncryptedPrefixes = ["edesk", "spesk", "p2esk", "BLesk", "mdesk"];

    protected override void Validate()
    {
        var phraseMode = SelectedSource == Source.Phrase;
        phraseFields.Visibility = phraseMode ? Visibility.Visible : Visibility.Collapsed;
        keyFields.Visibility = phraseMode ? Visibility.Collapsed : Visibility.Visible;
        legacyFields.Visibility = legacy.IsOn ? Visibility.Visible : Visibility.Collapsed;
        standardFields.Visibility = legacy.IsOn ? Visibility.Collapsed : Visibility.Visible;
        account.Header = $"Account  ({DerivationPath})";
        keyPassphraseFields.Visibility = inspected?.IsEncrypted == true || EncryptedPrefixes.Any(p => secretKey.Password.StartsWith(p, StringComparison.Ordinal))
            ? Visibility.Visible : Visibility.Collapsed;

        sessionNote.Visibility = KeepForSession ? Visibility.Visible : Visibility.Collapsed;
        encrypt.Header = KeepForSession ? "Encrypt in memory with a password" : "Encrypt with a password";
        encrypt.Visibility = KeepsOwnEncryption ? Visibility.Collapsed : Visibility.Visible;
        encryptFields.Visibility = !KeepsOwnEncryption && encrypt.IsOn ? Visibility.Visible : Visibility.Collapsed;
        unprotected.Visibility = !KeepsOwnEncryption && !encrypt.IsOn && !KeepForSession ? Visibility.Visible : Visibility.Collapsed;
        var problem = PasswordProblem;
        passwordProblem.Text = problem ?? "";
        passwordProblem.Visibility = problem is not null && password.Password.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        previewRow.Children.Clear();
        if (preview is { } material)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
            line.Children.Add(new AccountAvatarView(new Address(material.Address), 20));
            line.Children.Add(new TextBlock { Text = material.Address, Style = (Style)Application.Current.Resources["MonoText"], IsTextSelectionEnabled = true });
            previewRow.Children.Add(line);
            if (Existing is { } existing)
            {
                var warning = Label($"Already in your list as “{existing.Alias}”", "", "SystemFillColorCautionBrush");
                warning.HorizontalAlignment = HorizontalAlignment.Right;
                previewRow.Children.Add(warning);
            }
        }
        else if (previewError is not null)
        {
            previewRow.Children.Add(new TextBlock { Text = previewError, Style = (Style)Application.Current.Resources["CautionText"], TextAlignment = TextAlignment.Right });
        }
        else
        {
            previewRow.Children.Add(new TextBlock { Text = "—", Style = (Style)Application.Current.Resources["SecondaryText"] });
        }

        IsPrimaryButtonEnabled = preview is not null && Existing is null && alias.Text.Trim().Length > 0 && problem is null;
    }

    private void ShowPhraseCheck(MnemonicCheck? check)
    {
        phraseCheck.Visibility = check is { WordCount: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        if (check is null) return;
        var caution = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
        var secondary = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        if (check.UnknownWords.Count > 0)
            (phraseCheck.Text, phraseCheck.Foreground) = ($"Not in the word list: {string.Join(", ", check.UnknownWords)}", caution);
        else if (!check.IsValid)
            (phraseCheck.Text, phraseCheck.Foreground) = ($"{check.WordCount} words; a phrase has 12, 15, 18, 21 or 24 words and a valid checksum.", caution);
        else
            (phraseCheck.Text, phraseCheck.Foreground) = ($"{check.WordCount}-word phrase", secondary);
    }

    private async void RefreshPreview()
    {
        previewTask?.Cancel();
        preview = null;
        previewError = null;
        inspected = null;
        Validate();
        var importer = model.KeyImporter;
        var cancel = previewTask = new CancellationTokenSource();
        try
        {
            if (SelectedSource == Source.Phrase)
            {
                var words = phrase.Text.Trim();
                if (words.Length == 0) { ShowPhraseCheck(null); return; }
                var (passphrase, path, chosenCurve, isLegacy, email, fundraiser) =
                    (phrasePassphrase.Password, DerivationPath, MnemonicCurveExtensions.AllCases[Math.Max(0, curve.SelectedIndex)], legacy.IsOn, fundraiserEmail.Text, fundraiserPassword.Password);
                await Task.Delay(400, cancel.Token);
                var check = await importer.Check(words);
                if (cancel.IsCancellationRequested) return;
                ShowPhraseCheck(check);
                if (!check.IsValid) return;
                var material = isLegacy
                    ? await importer.KeyFromFundraiser(email, fundraiser, words)
                    : await importer.KeyFromMnemonic(words, passphrase.Length == 0 ? null : passphrase, path, chosenCurve);
                if (cancel.IsCancellationRequested) return;
                preview = material;
            }
            else
            {
                var key = secretKey.Password.Trim();
                if (key.Length == 0) return;
                var passphrase = keyPassphrase.Password;
                await Task.Delay(400, cancel.Token);
                var result = await importer.Inspect(key, passphrase.Length == 0 ? null : passphrase);
                if (cancel.IsCancellationRequested) return;
                inspected = result;
                if (result.NeedsPassphrase)
                    previewError = "Enter the key's password.";
                else if (result is { PublicKey: { } pk, Address: { } address, SecretKey: { } sk })
                    preview = new KeyMaterial(address.Scheme ?? AddressScheme.Tz1, pk, address.Value, sk);
            }
        }
        catch (TaskCanceledException) { return; }
        catch (Exception e)
        {
            if (cancel.IsCancellationRequested) return;
            previewError = e.Message;
        }
        Validate();
    }

    private async Task ImportAccount()
    {
        if (preview is not { } material) return;
        ShowProgress("Importing…");
        try
        {
            await model.ImportAccount(alias.Text.Trim(), material, KeepsOwnEncryption,
                                      !KeepsOwnEncryption && encrypt.IsOn ? password.Password : null, KeepForSession);
        }
        finally { ShowProgress(null); }
    }
}
