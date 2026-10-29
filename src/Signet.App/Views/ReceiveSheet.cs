using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Signet.App.Services;
using Signet.Models;
using Signet.Services;

namespace Signet.App.Views;

/// <summary>
/// Shows the selected wallet's address as a QR code and text so someone can send tez to it.
/// (Upstream's Etherlink side, with its <c>ChainSidePicker</c> and alias linking, is not ported yet.)
/// </summary>
public sealed partial class ReceiveSheet : ContentDialog
{
    public ReceiveSheet(Wallet wallet, IReadOnlyList<string> domains)
    {
        Title = "Receive";
        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        var stack = new StackPanel { Spacing = 16, Width = 372, HorizontalAlignment = HorizontalAlignment.Center };

        var names = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
        names.Children.Add(new TextBlock { Text = wallet.Alias, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        foreach (var domain in domains) names.Children.Add(new TextBlock { Text = domain, Foreground = secondary, HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(names);

        var image = new Image { Width = 220, Height = 220 };
        var bitmap = new BitmapImage();
        using (var stream = new MemoryStream(QRCode.Png(wallet.Address.Value, 220)))
            bitmap.SetSource(stream.AsRandomAccessStream());
        image.Source = bitmap;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(image, $"QR code for {wallet.Address.Value}");
        stack.Children.Add(new Border
        {
            Child = image, Padding = new Thickness(12), CornerRadius = new CornerRadius(12), HorizontalAlignment = HorizontalAlignment.Center,
            Background = new SolidColorBrush(Microsoft.UI.Colors.White),
            BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"], BorderThickness = new Thickness(1),
        });

        stack.Children.Add(new TextBlock
        {
            Text = wallet.Address.Value, Style = (Style)Application.Current.Resources["MonoText"], IsTextSelectionEnabled = true,
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center,
        });
        stack.Children.Add(new TextBlock
        {
            Text = $"Only send tez and Tezos tokens to this address{(domains.Count == 0 ? "." : $", or to {domains[0]}.")}",
            Foreground = secondary, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
        });
        Content = stack;

        PrimaryButtonText = "Copy address";
        CloseButtonText = "Done";
        DefaultButton = ContentDialogButton.Close;
        PrimaryButtonClick += async (_, args) =>
        {
            // Copying keeps the sheet open, as on the Mac.
            args.Cancel = true;
            Pasteboard.SetString(wallet.Address.Value);
            PrimaryButtonText = "Copied";
            await Task.Delay(1500);
            PrimaryButtonText = "Copy address";
        };
    }
}
