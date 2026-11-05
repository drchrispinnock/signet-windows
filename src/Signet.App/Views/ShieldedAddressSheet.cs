using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Signet.App.Services;
using Signet.Services;
using Signet.ViewModels;

namespace Signet.App.Views;

/// <summary>
/// The account's shielded address, to give to whoever will send to it, and the network's
/// shielded address book. A shielded address is the payment address record itself (there is
/// no short form in the protocol), so it is shown as the JSON other wallets take, with a
/// fingerprint to read out to the sender. "New address" makes a fresh one so that two payers
/// cannot tell they paid the same wallet.
/// </summary>
public sealed partial class ShieldedAddressSheet : Sheet
{
    private readonly WalletViewModel model;
    private ShieldedReceiveAddress? address;
    private bool loading;
    private bool copied;
    private IReadOnlyList<ShieldedContact> contacts = [];
    private string newName = "";
    private ShieldedContact? parsed;
    private string? parseError;
    private CancellationTokenSource? parsing;
    private readonly StackPanel own = new() { Spacing = 8 };
    private readonly StackPanel book = new() { Spacing = 8 };
    private readonly TextBox newJSON = new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 60, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12,
        IsSpellCheckEnabled = false, PlaceholderText = "Paste a shielded address",
    };
    private readonly TextBlock parseLine = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly Button add = new() { Content = "Add", IsEnabled = false };

    public ShieldedAddressSheet(WalletViewModel model) : base("Shielded address", 620)
    {
        this.model = model;
        if (model.SelectedWallet is { } wallet) Form.Children.Add(AccountHeading(wallet));
        Form.Children.Add(new TextBlock { Text = "Your address", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        Form.Children.Add(own);
        Form.Children.Add(new TextBlock { Text = "Address book", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        Form.Children.Add(book);
        var name = BoundTextBox(() => newName, value => newName = value, "Name", changed: UpdateAdd);
        name.Width = 160;
        var entry = new Grid { ColumnSpacing = 8 };
        entry.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        entry.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        entry.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        entry.Children.Add(name);
        Grid.SetColumn(newJSON, 1);
        entry.Children.Add(newJSON);
        Grid.SetColumn(add, 2);
        add.VerticalAlignment = VerticalAlignment.Top;
        entry.Children.Add(add);
        Form.Children.Add(entry);
        Form.Children.Add(parseLine);
        newJSON.TextChanged += (_, _) => ParseNew();
        add.Click += (_, _) => Add();

        CloseButtonText = "Done";
        DefaultButton = ContentDialogButton.Close;
        model.PropertyChanged += ModelChanged;
        Closed += (_, _) => { model.PropertyChanged -= ModelChanged; parsing?.Cancel(); };
        Opened += async (_, _) => await Load();
        RenderOwn();
        RenderBook();
    }

    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WalletViewModel.ShieldProgress) && loading) RenderOwn();
    }

    private void RenderOwn()
    {
        own.Children.Clear();
        if (address is { } a)
        {
            var line = new Grid { ColumnSpacing = 8 };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.Children.Add(new TextBlock { Text = $"Address #{a.Index}", Foreground = Secondary, VerticalAlignment = VerticalAlignment.Center });
            var fingerprint = new TextBlock
            {
                Text = $"fingerprint {SendShieldedSheet.Short(a.Fingerprint)}", Style = (Style)Application.Current.Resources["MonoText"],
                IsTextSelectionEnabled = true, VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(fingerprint, 1);
            line.Children.Add(fingerprint);
            var buttons = Row(MakeButton(copied ? "Copied" : "Copy", () => { Pasteboard.SetString(a.Json); copied = true; RenderOwn(); }),
                              MakeButton("New address", () => _ = MakeNew(), !loading));
            Grid.SetColumn(buttons, 2);
            line.Children.Add(buttons);
            own.Children.Add(line);
            own.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(6), Padding = new Thickness(6), Height = 110,
                Background = (Brush)Application.Current.Resources["ControlAltFillColorTertiaryBrush"],
                Child = new ScrollViewer
                {
                    Content = new TextBlock { Text = a.Json, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap },
                },
            });
            own.Children.Add(Note("Give the sender this JSON (Copy puts all of it on the clipboard) and read them the fingerprint so they can check what they pasted. Anything sent to it shows in your Shielded balance; nothing on the rollup links it to you."));
            own.Children.Add(Note("The key that spends what arrives is kept unencrypted in the wallet directory and its backups, even when this account's own key has a password."));
        }
        else if (loading)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(new ProgressRing { Width = 14, Height = 14, IsActive = true });
            row.Children.Add(new TextBlock { Text = model.ShieldProgress?.Text ?? "Preparing…", Foreground = Secondary });
            own.Children.Add(row);
        }
        else if (!model.CanShield)
        {
            own.Children.Add(Caution("Shielded addresses need Shielded Transactions on for this network and an account Signet can sign for."));
        }
    }

    private void RenderBook()
    {
        book.Children.Clear();
        if (contacts.Count == 0) book.Children.Add(Note("No shielded addresses saved yet."));
        foreach (var contact in contacts)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = contact.Name, VerticalAlignment = VerticalAlignment.Center });
            var print = new TextBlock { Text = SendShieldedSheet.Short(contact.Fingerprint), Style = (Style)Application.Current.Resources["MonoText"], Foreground = Secondary, IsTextSelectionEnabled = true, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(print, 1);
            row.Children.Add(print);
            var fingerprint = contact.Fingerprint;
            var remove = MakeButton("Remove", () => Remove(fingerprint));
            Grid.SetColumn(remove, 2);
            row.Children.Add(remove);
            book.Children.Add(row);
        }
    }

    private void UpdateAdd() => add.IsEnabled = parsed is not null && newName.Trim().Length > 0;

    // ---- Actions ---------------------------------------------------------------------------------

    private async Task Load()
    {
        try { contacts = model.ShieldedContacts(); } catch (Exception) { contacts = []; }
        RenderBook();
        if (!model.CanShield) { RenderOwn(); return; }
        loading = true;
        RenderOwn();
        try { address = await model.ShieldedReceiveAddressForSelected(); }
        catch (Exception e) { ShowError(e.Message); }
        finally
        {
            loading = false;
            RenderOwn();
        }
    }

    private async Task MakeNew()
    {
        ShowError(null);
        loading = true;
        RenderOwn();
        try
        {
            address = await model.MakeShieldedAddress();
            copied = false;
        }
        catch (Exception e) { ShowError(e.Message); }
        finally
        {
            loading = false;
            RenderOwn();
        }
    }

    private async void ParseNew()
    {
        parsing?.Cancel();
        parsed = null;
        parseError = null;
        parseLine.Text = "";
        UpdateAdd();
        if (newJSON.Text.Trim().Length == 0) return;
        var cancel = parsing = new CancellationTokenSource();
        try { await Task.Delay(300, cancel.Token); }
        catch (OperationCanceledException) { return; }
        try
        {
            var contact = await model.ShieldedRecipient(newJSON.Text);
            if (cancel.IsCancellationRequested) return;
            parsed = contact;
            parseLine.Text = $"Fingerprint {SendShieldedSheet.Short(contact.Fingerprint)}";
            parseLine.Foreground = Secondary;
        }
        catch (Exception e)
        {
            if (cancel.IsCancellationRequested) return;
            parseError = e.Message;
            parseLine.Text = e.Message;
            parseLine.Foreground = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
        }
        UpdateAdd();
    }

    private void Add()
    {
        if (parsed is not { } contact) return;
        ShowError(null);
        try
        {
            model.AddShieldedContact(contact, newName);
            contacts = model.ShieldedContacts();
            newName = "";
            newJSON.Text = "";
            RenderBook();
        }
        catch (Exception e) { ShowError(e.Message); }
    }

    private void Remove(string fingerprint)
    {
        ShowError(null);
        try
        {
            model.RemoveShieldedContact(fingerprint);
            contacts = model.ShieldedContacts();
            RenderBook();
        }
        catch (Exception e) { ShowError(e.Message); }
    }
}
