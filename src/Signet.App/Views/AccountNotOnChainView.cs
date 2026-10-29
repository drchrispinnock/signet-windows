using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Signet.App.Services;
using Signet.Models;
using Signet.ViewModels;

namespace Signet.App.Views;

/// <summary>
/// Shown where the balance would be when the node has never seen the address, with the way to
/// fix it: buy tez on mainnet, or use the faucet on a testnet.
/// </summary>
public sealed partial class AccountNotOnChainView : UserControl
{
    public AccountNotOnChainView(WalletViewModel model, Wallet wallet)
    {
        var network = model.Network;
        var orange = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"];

        var badge = new Grid { Width = 44, Height = 44 };
        badge.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Fill = (Brush)Application.Current.Resources["SystemFillColorCautionBackgroundBrush"] });
        badge.Children.Add(new FontIcon { Glyph = "", Foreground = orange });

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = "Key not found on chain", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.Medium });
        text.Children.Add(new TextBlock { Text = Explanation(wallet, network), Style = (Style)Application.Current.Resources["NoteText"] });

        var top = new Grid { ColumnSpacing = 16 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(text, 1);
        top.Children.Add(badge);
        top.Children.Add(text);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(60, 0, 0, 0) };
        if (wallet.Address.IsContract)
        {
            buttons.Children.Add(CopyButton(wallet));
        }
        else if (network.FaucetURL is not null)
        {
            var get = new Button { Content = "Get test tez…", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            get.Click += (_, _) => model.IsPresentingFaucet = true;
            buttons.Children.Add(get);
            buttons.Children.Add(CopyButton(wallet));
        }
        else
        {
            var receive = new Button { Content = "Show address to receive tez…", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            receive.Click += (_, _) => model.IsPresentingReceive = true;
            buttons.Children.Add(receive);
            var buy = new Button { Content = "Buy tez…", IsEnabled = false };
            var wrapper = new Border { Child = buy, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            ToolTipService.SetToolTip(wrapper, "Buying is not available on Windows yet");
            buttons.Children.Add(wrapper);
        }

        var stack = new StackPanel { Spacing = 12 };
        stack.Children.Add(top);
        stack.Children.Add(buttons);
        Content = stack;
    }

    private static string Explanation(Wallet wallet, Network network)
    {
        if (wallet.Address.IsContract) return $"No contract with this address exists on {network.Name}.";
        if (network.FaucetURL is not null)
            return $"This address has never received tez on {network.Name}. Signet can ask the {network.Name} faucet for free test tez.";
        return $"This address has never received tez on {network.Name}. Buy some tez on an exchange and send it here, or have someone send you some.";
    }

    private static Button CopyButton(Wallet wallet)
    {
        var label = new TextBlock { Text = "Copy address" };
        var icon = new FontIcon { Glyph = "", FontSize = 14 };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(icon);
        content.Children.Add(label);
        var button = new Button { Content = content };
        button.Click += async (_, _) =>
        {
            Pasteboard.SetString(wallet.Address.Value);
            label.Text = "Address copied";
            icon.Glyph = "";
            await Task.Delay(1500);
            label.Text = "Copy address";
            icon.Glyph = "";
        };
        return button;
    }
}
