using System.ComponentModel;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Signet.Models;
using Signet.ViewModels;

namespace Signet.App.Views;

/// <summary>The network pill in the header: green on mainnet, orange on a testnet. Click it to switch.</summary>
public sealed partial class NetworkBadgeMenu : UserControl
{
    private readonly WalletViewModel model;
    private readonly Button button = new() { Padding = new Thickness(8, 3, 6, 4), CornerRadius = new CornerRadius(11), MinHeight = 0, VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock label = new() { FontSize = 12, FontWeight = FontWeights.SemiBold };
    private readonly FontIcon chevron = new() { Glyph = "", FontSize = 8, Opacity = 0.8, Margin = new Thickness(0, 2, 0, 0) };
    private readonly MenuFlyout menu = new() { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight };

    public NetworkBadgeMenu(WalletViewModel model)
    {
        this.model = model;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        row.Children.Add(label);
        row.Children.Add(chevron);
        button.Content = row;
        button.Flyout = menu;
        menu.Opening += (_, _) => Fill();
        // A hover outline so the lozenge reads as clickable.
        button.BorderThickness = new Thickness(0);
        button.PointerEntered += (_, _) => button.BorderThickness = new Thickness(1);
        button.PointerExited += (_, _) => button.BorderThickness = new Thickness(0);
        Content = button;
        model.PropertyChanged += Changed;
        Update();
    }

    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WalletViewModel.Network)) Update();
    }

    private void Update()
    {
        var network = model.Network;
        var tint = (Brush)Application.Current.Resources[network.IsMainnet ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush"];
        var fill = (Brush)Application.Current.Resources[network.IsMainnet ? "SystemFillColorSuccessBackgroundBrush" : "SystemFillColorCautionBackgroundBrush"];
        label.Text = network.Name;
        label.Foreground = chevron.Foreground = tint;
        button.Background = fill;
        button.BorderBrush = tint;
        ToolTipService.SetToolTip(button, $"Connected to {network.RpcURL.AbsoluteString()}. Click to switch network.");
        AutomationProperties.SetName(button, $"Network {network.Name}. Switch network");
    }

    private void Fill()
    {
        menu.Items.Clear();
        foreach (var @base in Network.All)
        {
            var network = model.Resolved(@base);
            var item = new ToggleMenuFlyoutItem { Text = $"{network.Name}  {network.RpcURL.Host}", IsChecked = network.Name == model.Network.Name };
            item.Click += (_, _) => model.SwitchNetwork(@base);
            menu.Items.Add(item);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        var networks = new MenuFlyoutItem { Text = "Networks…", IsEnabled = false };
        ToolTipService.SetToolTip(networks, "Not available on Windows yet");
        menu.Items.Add(networks);
    }
}
