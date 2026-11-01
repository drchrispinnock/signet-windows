using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Signet.App.Views;

/// <summary>Which side of the bridge a sheet is about: the account on Tezos, or the same account on Etherlink.</summary>
public enum ChainSide { Tezos, Etherlink }

public static class ChainSideExtensions
{
    public static string RawValue(this ChainSide side) => side == ChainSide.Tezos ? "Tezos" : "Etherlink";

    public static string Logo(this ChainSide side) => side == ChainSide.Tezos ? "Assets/TezosToken.svg" : "Assets/EtherlinkLogo.svg";

    /// <summary>The asset drawn at <c>points</c>, rasterised at twice that for sharp edges.</summary>
    public static Image SmallLogo(string path, double points) => new()
    {
        Source = Services.SvgLogo.Load(path, points * 2),
        Width = points, Height = points, VerticalAlignment = VerticalAlignment.Center,
    };
}

/// <summary>
/// The chain's logo and, when <c>canSwitch</c>, a drop-down beside it to switch between Tezos and
/// Etherlink. Used at the top left of the Receive and Send sheets.
/// </summary>
public sealed partial class ChainSidePicker : UserControl
{
    private readonly StackPanel row = new() { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox picker = new() { VerticalAlignment = VerticalAlignment.Center };
    private ChainSide side;

    /// <summary>Raised when the user picks the other side.</summary>
    public event Action<ChainSide>? SideChanged;

    public ChainSidePicker(ChainSide side, bool canSwitch)
    {
        this.side = side;
        row.Children.Add(ChainSideExtensions.SmallLogo(side.Logo(), 22));
        if (canSwitch)
        {
            foreach (var candidate in Enum.GetValues<ChainSide>()) picker.Items.Add(candidate.RawValue());
            picker.SelectedIndex = (int)side;
            ToolTipService.SetToolTip(picker, "Switch between Tezos and Etherlink.");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(picker, "Chain");
            picker.SelectionChanged += (_, _) =>
            {
                if (picker.SelectedIndex < 0 || (ChainSide)picker.SelectedIndex == this.side) return;
                this.side = (ChainSide)picker.SelectedIndex;
                row.Children[0] = ChainSideExtensions.SmallLogo(this.side.Logo(), 22);
                SideChanged?.Invoke(this.side);
            };
            row.Children.Add(picker);
        }
        Content = row;
    }

    public ChainSide Side => side;
}
