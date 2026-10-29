using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Signet.Models;
using Signet.Services;
using Signet.ViewModels;

namespace Signet.App.Views;

/// <summary>
/// Networks: which node Signet talks to on each network, and the TzKT API, block explorer and
/// faucet links it uses there. Opened from the burger menu and from the network badge; the
/// network in use is switched with the badge, not here. (Upstream's Layer 2 switch and node
/// field wait for Etherlink.)
/// </summary>
public sealed partial class NetworksSheet : Sheet
{
    private readonly WalletViewModel model;
    private readonly ComboBox picker = new() { Header = "Network", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly SectionHeader nodeHeader = new("Networks");
    private readonly SectionHeader linksHeader = new("Links");
    /// <summary>
    /// The node beneath the network dropdown is free text so any RPC endpoint can replace the
    /// default for that network. The text is applied on Enter or with Apply.
    /// </summary>
    private readonly TextBox nodeText = new() { FontFamily = new FontFamily("Cascadia Mono, Consolas"), IsSpellCheckEnabled = false };
    private readonly TextBlock nodeMessage = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly StackPanel checking = new() { Orientation = Orientation.Horizontal, Spacing = 6, Visibility = Visibility.Collapsed };
    private readonly Button useDefault = new() { Content = "Use Default" };
    private readonly Button apply = new() { Content = "Apply" };
    private readonly Button useAnyway = new() { Content = "Use Anyway", Visibility = Visibility.Collapsed };
    private readonly StackPanel links = new() { Spacing = 16 };
    /// <summary>
    /// A node on a network that pins a chain id is asked which chain it serves before Apply
    /// adopts it; a wrong or silent answer is shown here with a Use Anyway button.
    /// </summary>
    private NodeIdentity? nodeWarning;
    private bool nodeChecking;
    private bool syncing;

    public NetworksSheet(WalletViewModel model) : base("Networks", 520)
    {
        this.model = model;
        foreach (var network in Network.All)
            picker.Items.Add(new ComboBoxItem { Content = network.Name + (network.Name == model.Network.Name ? "  (in use)" : ""), Tag = network.Name });
        // Starts on the network in use.
        picker.SelectedIndex = Math.Max(0, Network.All.ToList().FindIndex(n => n.Name == model.Network.Name));

        checking.Children.Add(new ProgressRing { Width = 16, Height = 16, IsActive = true });
        checking.Children.Add(new TextBlock { Text = "Checking which chain the node serves…", Style = (Style)Application.Current.Resources["SecondaryText"] });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(useDefault);
        buttons.Children.Add(apply);
        buttons.Children.Add(useAnyway);

        Form.Children.Add(nodeHeader);
        Form.Children.Add(picker);
        // A label of its own: a TextBox header would take the field's monospaced font.
        Form.Children.Add(new TextBlock { Text = "Node" });
        Form.Children.Add(nodeText);
        Form.Children.Add(nodeMessage);
        Form.Children.Add(checking);
        Form.Children.Add(buttons);
        Form.Children.Add(linksHeader);
        Form.Children.Add(links);

        CloseButtonText = "Done";
        DefaultButton = ContentDialogButton.Close;

        picker.SelectionChanged += (_, _) => SyncNodeText();
        nodeText.TextChanged += (_, _) =>
        {
            if (!syncing) nodeWarning = null;
            Update();
        };
        nodeText.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;
            e.Handled = true;
            ApplyNode();
        };
        useDefault.Click += (_, _) =>
        {
            if (EditingNetwork is { } @base) { model.UseDefaultNode(@base); SyncNodeText(); }
        };
        apply.Click += (_, _) => ApplyNode();
        useAnyway.Click += (_, _) =>
        {
            if (EditingNetwork is { } @base) { model.SetNode(nodeText.Text, @base); SyncNodeText(); }
        };
        model.PropertyChanged += Changed;
        Closed += (_, _) => model.PropertyChanged -= Changed;
        SyncNodeText();
    }

    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WalletViewModel.Network) && EditingNetwork?.Name == model.Network.Name) SyncNodeText();
    }

    /// <summary>The network whose node is being edited.</summary>
    private Network? EditingNetwork => Network.Named((picker.SelectedItem as ComboBoxItem)?.Tag as string);

    private bool NodeIsDirty => EditingNetwork is { } @base && Network.NodeURL(nodeText.Text) != model.NodeURL(@base);

    private void Update()
    {
        var @base = EditingNetwork;
        useDefault.IsEnabled = @base is not null && model.NodeURL(@base) != @base.DefaultRPCURL;
        apply.IsEnabled = NodeIsDirty && !nodeChecking;
        apply.Visibility = nodeWarning is null ? Visibility.Visible : Visibility.Collapsed;
        useAnyway.Visibility = nodeWarning is null ? Visibility.Collapsed : Visibility.Visible;
        checking.Visibility = nodeChecking && nodeWarning is null && nodeMessage.Visibility == Visibility.Collapsed ? Visibility.Visible : Visibility.Collapsed;
        if (nodeWarning is { } warning && @base is not null)
            ShowNodeMessage(Warning(warning, @base), "SystemFillColorCautionBrush");
    }

    private void ShowNodeMessage(string? text, string brush = "SystemFillColorCriticalBrush")
    {
        nodeMessage.Text = text ?? "";
        nodeMessage.Foreground = (Brush)Application.Current.Resources[brush];
        nodeMessage.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private string NodeHelp(Network @base)
    {
        var text = $"The node Signet uses for {@base.Name}. Each network keeps its own; switch networks with the badge at the top of the main window.";
        text += @base.Chain == "custom"
            ? " Custom is yours to point anywhere, for example a node you run yourself."
            : $" The default is {@base.DefaultRPCURL.AbsoluteString()}.";
        if (@base.Chain == "weeklynet")
            text += " Weeklynet restarts every Wednesday and its address carries that date, so the default follows the calendar.";
        if (@base.ExpectedChainID is not null)
            text += " A new node is asked which chain it serves before it is used, and one on another chain is warned about.";
        return text;
    }

    private static string LinksHelp(Network @base)
    {
        var text = "The TzKT API feeds the history, token and NFT lists; the explorer is where operations are opened; the faucet is behind Get test tez. Leave a link empty to do without it.";
        if (@base.IsMainnet) text += " Mainnet has no faucet.";
        else text += @base.FaucetKind == Network.FaucetKindValue.Pqpark
            ? " A replacement faucet must speak PQPark's faucet API."
            : " A replacement faucet must speak the teztnets faucet API.";
        return text;
    }

    /// <summary>The warning under the node field: the node serves another chain, or could not be asked.</summary>
    private static string Warning(NodeIdentity identity, Network @base) => identity switch
    {
        NodeIdentity.Mismatch m => $"This node serves chain {m.Found}, not {@base.Name} ({m.Expected}). Signet would show it in red and refuse to sign through it.",
        NodeIdentity.Unchecked u => $"Signet could not check which chain this node serves. {u.Reason}.",
        _ => "",
    };

    private void SyncNodeText()
    {
        if (EditingNetwork is not { } @base) return;
        syncing = true;
        nodeText.Text = Network.NodeDisplayTextStandalone(model.NodeURL(@base));
        syncing = false;
        nodeWarning = null;
        ShowNodeMessage(null);
        nodeHeader.Help = NodeHelp(@base);
        linksHeader.Help = LinksHelp(@base);
        // The links beside the node. Each field is applied on Enter or with Apply; empty text
        // turns the link off (no explorer links, no Get test tez button); Use Default restores ours.
        links.Children.Clear();
        foreach (var service in Enum.GetValues<Network.Service>().Where(@base.Offers))
            links.Children.Add(new LinkField(service.Title(), () => model.ServiceURL(service, @base), @base.DefaultURLFor(service),
                                             text => model.SetServiceURL(text, service, @base),
                                             () => model.UseDefaultServiceURL(service, @base)));
        Update();
    }

    /// <summary>
    /// Apply asks a node on a pinned network which chain it serves first; only a matching (or
    /// unpinned) node is adopted straight away, anything else waits for Use Anyway.
    /// </summary>
    private async void ApplyNode()
    {
        if (EditingNetwork is not { } @base) return;
        if (Network.NodeURL(nodeText.Text) is null)
        {
            ShowNodeMessage(Network.NodeURLProblem(nodeText.Text));
            return;
        }
        ShowNodeMessage(null);
        nodeWarning = null;
        nodeChecking = true;
        Update();
        var text = nodeText.Text;
        var identity = await model.CheckNode(text, @base);
        nodeChecking = false;
        // The field or the dropdown moved on while the node was being asked.
        if (text != nodeText.Text || EditingNetwork?.Name != @base.Name)
        {
            Update();
            return;
        }
        if (identity is NodeIdentity.Unpinned or NodeIdentity.Matches)
        {
            model.SetNode(text, @base);
            SyncNodeText();
        }
        else
        {
            nodeWarning = identity;
            Update();
        }
    }
}
