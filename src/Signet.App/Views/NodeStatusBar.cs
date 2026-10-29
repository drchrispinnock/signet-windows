using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Signet.Models;
using Signet.Services;

namespace Signet.App.Views;

/// <summary>
/// Thin strip at the bottom of the window: a coloured light and a line about the layer 1 node.
/// A healthy node shows just the green light and its host; the level, head age and latency are in
/// the tooltip. (Upstream adds a second line for the Layer 2 node; Etherlink is not ported yet.)
/// </summary>
public sealed partial class NodeStatusBar : UserControl
{
    private readonly NodeMonitor monitor;
    private readonly Ellipse light = new() { Width = 9, Height = 9, StrokeThickness = 1, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock host = new() { FontWeight = FontWeights.Medium, FontSize = 12 };
    private readonly TextBlock summary = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Right };

    public NodeStatusBar(NodeMonitor monitor)
    {
        this.monitor = monitor;
        light.Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(38, 0, 0, 0));
        summary.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        var label = new TextBlock { Text = "Layer 1:", FontSize = 12, Foreground = summary.Foreground };
        var grid = new Grid { ColumnSpacing = 8, Padding = new Thickness(12, 6, 12, 6), Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"] };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(label, 1);
        Grid.SetColumn(host, 2);
        Grid.SetColumn(summary, 3);
        grid.Children.Add(light);
        grid.Children.Add(label);
        grid.Children.Add(host);
        grid.Children.Add(summary);
        // Clicking the strip checks the node again.
        grid.Tapped += async (_, _) => await monitor.CheckNow();
        Content = grid;
        monitor.PropertyChanged += Changed;
        Update();
    }

    private void Changed(object? sender, PropertyChangedEventArgs e) => Update();

    private void Update()
    {
        var status = monitor.Status;
        light.Fill = new SolidColorBrush(status.Light switch
        {
            NodeStatus.LightColor.Green => Microsoft.UI.Colors.LimeGreen,
            NodeStatus.LightColor.Yellow => Microsoft.UI.Colors.Gold,
            NodeStatus.LightColor.Red => Microsoft.UI.Colors.Red,
            _ => Microsoft.UI.Colors.Gray,
        });
        var url = monitor.Network.RpcURL;
        host.Text = url.Host.Length > 0 ? url.Host : url.AbsoluteString();
        summary.Text = status.IsHealthy ? "" : status.Summary;
        var lines = new List<string> { url.AbsoluteString(), status.Detail };
        if (status.Level is { } level) lines.Add($"Head level {level.ToString("N0", CultureInfo.CurrentCulture)}");
        if (status is NodeStatus.Healthy healthy) lines.Add($"Reply in {healthy.Latency.ToString("0.00", CultureInfo.CurrentCulture)} s");
        if (monitor.LastChecked is { } checkedAt) lines.Add($"Checked {checkedAt.ToLocalTime():T}");
        lines.Add("Click to check now");
        ToolTipService.SetToolTip(Content as UIElement, string.Join("\n", lines));
        AutomationProperties.SetName(this, $"Layer 1: {host.Text}: {status.Summary}");
    }
}
