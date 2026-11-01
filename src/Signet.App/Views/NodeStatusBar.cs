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
/// Thin strip at the bottom of the window: a coloured light and a line about the layer 1 node
/// and, while the network has Layer 2 on, a second line beneath it for the Layer 2 Michelson
/// node. The lines are a grid so hosts and summaries line up in columns. A healthy node shows
/// just the green light and its host; the level, head age and latency are in the tooltip.
/// </summary>
public sealed partial class NodeStatusBar : UserControl
{
    private readonly NodeLine layer1;
    private readonly NodeLine? layer2;

    public NodeStatusBar(NodeMonitor monitor, NodeMonitor? layer2 = null)
    {
        var grid = new Grid { ColumnSpacing = 8, RowSpacing = 4, Padding = new Thickness(12, 6, 12, 6), Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"] };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layer1 = new NodeLine(monitor, "Layer 1:", grid, 0);
        if (layer2 is not null) this.layer2 = new NodeLine(layer2, "Layer 2:", grid, 1);
        Content = grid;
    }

    /// <summary>
    /// One node's cells: light, label, host and, unless the node is healthy, the summary, which
    /// takes the rest of the width. Clicking the line checks that node again.
    /// </summary>
    private sealed class NodeLine
    {
        private readonly NodeMonitor monitor;
        private readonly string label;
        private readonly Ellipse light = new() { Width = 9, Height = 9, StrokeThickness = 1, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock title = new() { FontSize = 12 };
        private readonly TextBlock host = new() { FontWeight = FontWeights.Medium, FontSize = 12 };
        private readonly TextBlock summary = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Right };
        private readonly FrameworkElement[] cells;

        public NodeLine(NodeMonitor monitor, string label, Grid grid, int row)
        {
            this.monitor = monitor;
            this.label = label;
            light.Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(38, 0, 0, 0));
            summary.Foreground = title.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
            title.Text = label;
            cells = [light, title, host, summary];
            for (var column = 0; column < cells.Length; column++)
            {
                Grid.SetColumn(cells[column], column);
                Grid.SetRow(cells[column], row);
                // A transparent background makes the whole cell clickable, not just its glyphs.
                cells[column].Tapped += async (_, _) => await monitor.CheckNow();
                grid.Children.Add(cells[column]);
            }
            monitor.PropertyChanged += Changed;
            Update();
        }

        private void Changed(object? sender, PropertyChangedEventArgs e) => Update();

        private void Update()
        {
            // The Layer 2 line is there only while the network has a Layer 2 node to watch.
            var visibility = monitor.Endpoint is null && monitor.Layer == NodeMonitor.LayerValue.Two ? Visibility.Collapsed : Visibility.Visible;
            foreach (var cell in cells) cell.Visibility = visibility;
            var status = monitor.Status;
            light.Fill = new SolidColorBrush(status.Light switch
            {
                NodeStatus.LightColor.Green => Microsoft.UI.Colors.LimeGreen,
                NodeStatus.LightColor.Yellow => Microsoft.UI.Colors.Gold,
                NodeStatus.LightColor.Red => Microsoft.UI.Colors.Red,
                _ => Microsoft.UI.Colors.Gray,
            });
            var url = monitor.Endpoint;
            host.Text = url is null ? "—" : url.Host.Length > 0 ? url.Host : url.AbsoluteString();
            summary.Text = status.IsHealthy ? "" : status.Summary;
            var lines = new List<string> { url?.AbsoluteString() ?? "No node", status.Detail };
            if (status.Level is { } level) lines.Add($"Head level {level.ToString("N0", CultureInfo.CurrentCulture)}");
            if (status is NodeStatus.Healthy healthy) lines.Add($"Reply in {healthy.Latency.ToString("0.00", CultureInfo.CurrentCulture)} s");
            if (monitor.LastChecked is { } checkedAt) lines.Add($"Checked {checkedAt.ToLocalTime():T}");
            lines.Add("Click to check again.");
            var help = string.Join("\n", lines);
            foreach (var cell in cells) ToolTipService.SetToolTip(cell, help);
            AutomationProperties.SetName(host, $"{label} {host.Text}: {status.Summary}");
        }
    }
}
