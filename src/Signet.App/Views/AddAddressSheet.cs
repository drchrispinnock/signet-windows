using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Signet.Models;
using Signet.ViewModels;

namespace Signet.App.Views;

/// <summary>Adds an address book entry: a named address Signet holds no key for.</summary>
public sealed partial class AddAddressSheet : Sheet
{
    private readonly WalletViewModel model;
    private readonly TextBox alias = new() { Header = "Name", PlaceholderText = "e.g. Alice", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button useSuggested = new() { VerticalAlignment = VerticalAlignment.Bottom, Visibility = Visibility.Collapsed };
    private readonly TextBox address = new() { Header = "Address", PlaceholderText = "tz1… or KT1…", FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas") };
    private readonly StackPanel preview = new() { Orientation = Orientation.Horizontal, Spacing = 10 };
    private string? suggestedName;
    private IReadOnlyList<string> previewDomains = [];
    private CancellationTokenSource? lookup;

    public AddAddressSheet(WalletViewModel model) : base("Add address", 480)
    {
        this.model = model;
        ToolTipService.SetToolTip(useSuggested, "Name from the account's TzProfile");
        useSuggested.Click += (_, _) => { if (suggestedName is not null) alias.Text = suggestedName; };
        var nameRow = new Grid { ColumnSpacing = 8 };
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(useSuggested, 1);
        nameRow.Children.Add(alias);
        nameRow.Children.Add(useSuggested);

        Form.Children.Add(nameRow);
        Form.Children.Add(address);
        Form.Children.Add(LabeledContent("Preview", preview));
        Form.Children.Add(Note("This only records the address. You can watch its balance and send to it, but not spend from it."));

        CloseButtonText = "Cancel";
        Primary("Add", () => model.AddWatchOnlyWallet(alias.Text, address.Text));
        alias.TextChanged += (_, _) => { Validate(); UpdateSuggestion(); };
        address.TextChanged += async (_, _) => await AddressChanged();
        Opened += (_, _) => alias.Focus(FocusState.Programmatic);
        Validate();
        UpdatePreview();
    }

    private Address Trimmed => new(address.Text.Trim());
    private bool AddressLooksValid => Trimmed.IsValidAccount;

    protected override void Validate() =>
        IsPrimaryButtonEnabled = alias.Text.Trim().Length > 0 && AddressLooksValid;

    private void UpdateSuggestion()
    {
        var show = suggestedName is not null && suggestedName != alias.Text.Trim();
        useSuggested.Content = $"Use “{suggestedName}”";
        useSuggested.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdatePreview()
    {
        preview.Children.Clear();
        if (AddressLooksValid)
        {
            preview.Children.Add(new AccountAvatarView(Trimmed, 32));
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock { Text = Trimmed.Shortened(), Style = (Style)Application.Current.Resources["MonoText"] });
            if (suggestedName is not null) text.Children.Add(new TextBlock { Text = suggestedName });
            foreach (var domain in previewDomains) text.Children.Add(new TextBlock { Text = domain, Style = (Style)Application.Current.Resources["SecondaryText"] });
            var kind = Trimmed.Scheme is { } scheme ? scheme.DisplayName() : Trimmed.IsContract ? "Smart contract" : null;
            if (kind is not null) text.Children.Add(new TextBlock { Text = kind, FontSize = 12, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"] });
            preview.Children.Add(text);
        }
        else if (address.Text.Length == 0)
        {
            preview.Children.Add(new TextBlock { Text = "Paste or type an address", Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"] });
        }
        else
        {
            preview.Children.Add(Label("Not a valid Tezos address", ""));
        }
    }

    private async Task AddressChanged()
    {
        Validate();
        lookup?.Cancel();
        previewDomains = [];
        suggestedName = null;
        UpdateSuggestion();
        UpdatePreview();
        if (!AddressLooksValid) return;
        var cancel = lookup = new CancellationTokenSource();
        var target = Trimmed;
        try { await Task.Delay(300, cancel.Token); }  // debounce typing
        catch (TaskCanceledException) { return; }
        var domains = model.DomainNames(target);
        var profile = model.ProfileName(target);
        previewDomains = await domains;
        suggestedName = await profile;
        if (cancel.IsCancellationRequested) return;
        // Offer the TzProfiles name as the alias when the user has not typed one yet.
        if (suggestedName is not null && alias.Text.Trim().Length == 0) alias.Text = suggestedName;
        UpdateSuggestion();
        UpdatePreview();
    }
}
