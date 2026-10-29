using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Signet.App.Services;
using Signet.Models;
using Signet.ViewModels;

namespace Signet.App.Views;

/// <summary>
/// Shows the selected account's secret key, masked until revealed, with a copy button and a
/// warning. Encrypted keys are opened with their password first.
/// </summary>
public sealed partial class ExportKeySheet : Sheet
{
    private readonly WalletViewModel model;
    private readonly Wallet? wallet;
    private readonly PasswordBox passphrase = new();
    private readonly StackPanel keys = new() { Spacing = 12 };
    private WalletViewModel.ExportedKey? exported;
    private bool revealed;

    public ExportKeySheet(WalletViewModel model) : base("Export secret key", 560)
    {
        this.model = model;
        wallet = model.SelectedWallet;
        if (wallet is not null) Form.Children.Add(AccountHeading(wallet));

        var warning = new Border
        {
            Padding = new Thickness(12), CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.Resources["SystemFillColorCautionBackgroundBrush"],
            Child = Label("Anyone who has this secret key controls the account and everything in it. Never share it, never type it into a website or a chat, and keep any copy offline. Signet shows it only here and puts it on the clipboard only when you press Copy.",
                          "", "SystemFillColorCautionBrush"),
        };
        Form.Children.Add(warning);
        Form.Children.Add(keys);

        CloseButtonText = "Cancel";
        Primary(NeedsPassphrase ? "Unlock" : "Show key", Unlock);
        passphrase.PasswordChanged += (_, _) => Validate();
        Opened += async (_, _) =>
        {
            if (!NeedsPassphrase) await Run(Unlock);
            else passphrase.Focus(FocusState.Programmatic);
        };
        Closed += (_, _) => { exported = null; passphrase.Password = ""; };
        Render();
    }

    private bool NeedsPassphrase => wallet?.KeyKind == KeyKind.Encrypted;

    protected override void Validate() =>
        IsPrimaryButtonEnabled = exported is not null || !NeedsPassphrase || passphrase.Password.Length > 0;

    private async Task Run(Func<Task> action)
    {
        try { await action(); }
        catch (Exception e) { ShowError(e.Message); }
    }

    private async Task Unlock()
    {
        if (exported is not null) return;  // "Done"
        ShowProgress("Decrypting…");
        try
        {
            exported = await model.ExportSecretKey(NeedsPassphrase ? passphrase.Password : null);
            passphrase.Password = "";
        }
        finally { ShowProgress(null); }
        KeepOpen = true;
        Render();
    }

    private void Render()
    {
        keys.Children.Clear();
        if (exported is { } key)
        {
            keys.Children.Add(KeyRow("Secret key", key.Clear, maskable: true));
            if (key.Encrypted is { } encrypted)
            {
                keys.Children.Add(KeyRow("Encrypted form", encrypted, maskable: false));
                keys.Children.Add(Note($"The encrypted form is what {WalletFolder(model)} holds; it still needs this account's password. octez-client accepts both."));
            }
            PrimaryButtonText = "Done";
            CloseButtonText = "";
        }
        else if (NeedsPassphrase && wallet is not null)
        {
            passphrase.Header = $"Password for “{wallet.Alias}”";
            keys.Children.Add(passphrase);
            keys.Children.Add(Note("The key is stored encrypted. Enter its password to decrypt and show it."));
        }
        Validate();
    }

    private Grid KeyRow(string title, string value, bool maskable)
    {
        var text = new TextBlock
        {
            Text = maskable && !revealed ? new string('•', 24) : value, Style = (Style)Application.Current.Resources["MonoText"],
            TextWrapping = TextWrapping.Wrap, MaxWidth = 340, IsTextSelectionEnabled = !maskable || revealed, VerticalAlignment = VerticalAlignment.Center,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        row.Children.Add(text);
        if (maskable)
        {
            var reveal = new Button { Content = new FontIcon { Glyph = revealed ? "" : "", FontSize = 14 }, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
            ToolTipService.SetToolTip(reveal, revealed ? "Hide" : "Reveal");
            reveal.Click += (_, _) => { revealed = !revealed; Render(); };
            row.Children.Add(reveal);
        }
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
