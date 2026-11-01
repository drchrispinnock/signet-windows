using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Signet.Models;

namespace Signet.App.Views;

/// <summary>Key balances: tez first, Etherlink second, then any other tokens.</summary>
public sealed partial class AssetListView : UserControl
{
    private readonly StackPanel list = new() { Spacing = 12 };

    public AssetListView()
    {
        Content = list;
    }

    public IReadOnlyList<AssetBalance> Assets
    {
        set
        {
            list.Children.Clear();
            foreach (var asset in value) list.Children.Add(new AssetRowView(asset));
        }
    }
}

/// <summary>One balance. Rows with a breakdown get a chevron that expands the detail lines beneath.</summary>
public sealed partial class AssetRowView : UserControl
{
    private readonly AssetBalance asset;
    private readonly StackPanel details = new() { Spacing = 6, Margin = new Thickness(60, 0, 24, 0), Visibility = Visibility.Collapsed };
    private readonly FontIcon chevron = new() { Glyph = "", FontSize = 12 };
    private readonly Button toggle = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), Padding = new Thickness(6) };

    public AssetRowView(AssetBalance asset)
    {
        this.asset = asset;
        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

        var row = new Grid { ColumnSpacing = 16 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = Icon();
        row.Children.Add(icon);

        UIElement amount;
        if (asset.Unavailable is { } unavailable)
        {
            var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, VerticalAlignment = VerticalAlignment.Center };
            var dash = new TextBlock { Text = "—", FontSize = 20, Foreground = secondary };
            ToolTipService.SetToolTip(dash, unavailable);
            stack.Children.Add(dash);
            stack.Children.Add(new TextBlock { Text = unavailable, Foreground = secondary, TextWrapping = TextWrapping.Wrap, MaxLines = 2 });
            amount = stack;
        }
        else
        {
            amount = new TextBlock { Text = asset.FormattedAmount, FontSize = 20, VerticalAlignment = VerticalAlignment.Center };
        }
        Grid.SetColumn((FrameworkElement)amount, 1);
        row.Children.Add(amount);

        if (asset.Details.Count > 0)
        {
            chevron.Foreground = secondary;
            toggle.Content = chevron;
            toggle.VerticalAlignment = VerticalAlignment.Center;
            toggle.Click += (_, _) => Toggle();
            ToolTipService.SetToolTip(toggle, "Show breakdown");
            AutomationProperties.SetName(toggle, "Show breakdown");
            Grid.SetColumn(toggle, 2);
            row.Children.Add(toggle);
            row.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            row.Tapped += (_, e) => { if (e.OriginalSource is not FontIcon) Toggle(); };

            foreach (var line in asset.Details)
            {
                var detail = new Grid();
                detail.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                detail.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                detail.Children.Add(new TextBlock { Text = line.Label, Foreground = secondary });
                var value = new TextBlock { Text = AssetBalance.Format(line.Amount, asset.Symbol) };
                Grid.SetColumn(value, 1);
                detail.Children.Add(value);
                details.Children.Add(detail);
            }
        }

        var outer = new StackPanel { Spacing = 8 };
        outer.Children.Add(row);
        outer.Children.Add(details);
        Content = outer;
        AutomationProperties.SetName(this, $"{asset.Name} balance {asset.FormattedAmount}");
    }

    private void Toggle()
    {
        var expanded = details.Visibility != Visibility.Visible;
        details.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        chevron.Glyph = expanded ? "" : "";
        var help = expanded ? "Hide breakdown" : "Show breakdown";
        ToolTipService.SetToolTip(toggle, help);
        AutomationProperties.SetName(toggle, help);
    }

    private FrameworkElement Icon() => asset.Kind switch
    {
        AssetBalance.AssetKind.Tez => Logo("Assets/TezosToken.svg"),
        AssetBalance.AssetKind.Etherlink => Logo("Assets/EtherlinkLogo.svg"),
        AssetBalance.AssetKind.Shielded => new Image { Source = new BitmapImage(new Uri("ms-appx:///Assets/ShieldLogo.png")), Width = 44, Height = 44 },
        _ => Badge(asset.Symbol.Length > 4 ? asset.Symbol[..4] : asset.Symbol),
    };

    private static Image Logo(string path) => new()
    {
        Source = Services.SvgLogo.Load(path, 88),
        Width = 44, Height = 44,
    };

    private static Grid Badge(string text)
    {
        var grid = new Grid { Width = 44, Height = 44 };
        grid.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Stroke = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], StrokeThickness = 1.5 });
        grid.Children.Add(new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        return grid;
    }
}
