using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Signet.ViewModels;

namespace Signet.App.Views;

/// <summary>Changes the alias of the selected wallet.</summary>
public sealed partial class RenameWalletSheet : Sheet
{
    private readonly WalletViewModel model;
    private readonly TextBox alias = new() { Header = "Name", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button sync = new() { VerticalAlignment = VerticalAlignment.Bottom };

    public RenameWalletSheet(WalletViewModel model) : base("Rename account", 440)
    {
        this.model = model;
        alias.Text = model.SelectedWallet?.Alias ?? "";

        var syncContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        syncContent.Children.Add(new FontIcon { Glyph = "", FontSize = 14 });
        syncContent.Children.Add(new TextBlock { Text = "Sync with TzProfile" });
        sync.Content = syncContent;
        ToolTipService.SetToolTip(sync, "Fetch the name this address publishes in its TzProfile");
        sync.Click += async (_, _) => await SyncWithProfile();

        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(sync, 1);
        row.Children.Add(alias);
        row.Children.Add(sync);
        Form.Children.Add(row);
        if (model.SelectedWallet is { } wallet)
            Form.Children.Add(LabeledContent("Address", new TextBlock { Text = wallet.Address.Shortened(), Style = (Style)Application.Current.Resources["MonoText"] }));
        Form.Children.Add(Note("The alias is renamed in %USERPROFILE%\\.signet."));

        CloseButtonText = "Cancel";
        Primary("Rename", () => { model.RenameSelectedWallet(alias.Text.Trim()); return Task.CompletedTask; });
        alias.TextChanged += (_, _) => Validate();
        Opened += (_, _) => { alias.Focus(FocusState.Programmatic); alias.SelectAll(); };
        Validate();
    }

    protected override void Validate()
    {
        var trimmed = alias.Text.Trim();
        IsPrimaryButtonEnabled = trimmed.Length > 0 && trimmed != model.SelectedWallet?.Alias;
    }

    private async Task SyncWithProfile()
    {
        if (model.SelectedWallet is not { } wallet) return;
        ShowError(null);
        sync.IsEnabled = false;
        try
        {
            if (await model.ProfileName(wallet.Address) is { } name) alias.Text = name;
            else ShowError("This address has no TzProfile name.");
        }
        finally { sync.IsEnabled = true; }
    }
}
