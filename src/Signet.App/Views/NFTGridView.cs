using System.Globalization;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Signet.App.Services;
using Signet.Models;
using Signet.Services;
using Signet.ViewModels;
using Windows.System;
using Windows.UI.Core;

namespace Signet.App.Views;

/// <summary>
/// What the NFT grid's context menu can do with the tokens, handed in by the dashboard; null
/// (previews) leaves only the links and copies.
/// </summary>
/// <param name="Network">The network tokens on a layer live on, for explorer links.</param>
/// <param name="CanOperate">Whether the selected account can transfer or burn tokens on a layer.</param>
/// <param name="Start">Opens the Transfer or Burn sheet on the tokens.</param>
/// <param name="Download">Saves the tokens' artwork to disk.</param>
public sealed record NFTActions(Func<int, Network> Network, Func<int, bool> CanOperate, Action<NFTOperation> Start, Action<NFTDownload> Download);

/// <summary>
/// Grid of NFT thumbnails at the bottom of the default screen. Tiles select like files in
/// Explorer (click, Ctrl-click to add, Shift-click for a range) and a right-click opens the menu
/// for the clicked tile, or for the whole selection when it is part of one. The control is kept
/// across refreshes, so the selection survives them (cut to the tokens still listed).
/// </summary>
public sealed partial class NFTGridView : UserControl
{
    private readonly NFTActions? actions;
    private readonly Grid root = new() { RowSpacing = 10 };
    private IReadOnlyList<NFT> nfts = [];
    private readonly HashSet<string> selection = [];
    /// <summary>Where a Shift-click range starts: the last tile clicked without Shift.</summary>
    private string? anchor;
    private readonly Dictionary<string, Border> frames = [];
    private readonly Dictionary<string, TextBlock> captions = [];

    public NFTGridView(NFTActions? actions = null)
    {
        this.actions = actions;
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Content = root;
        MinHeight = 180;
    }

    /// <summary>Shows <c>nfts</c>, keeping the selection of those still listed.</summary>
    public void Update(IReadOnlyList<NFT> nfts, bool isLoading, Portfolio.PortfolioState state, string? layerEmptyText)
    {
        this.nfts = nfts;
        selection.IntersectWith(nfts.Select(n => n.Id));
        root.Children.Clear();
        frames.Clear();
        captions.Clear();
        if (ActivityTabsView.PortfolioStateNote(state) is { } note) root.Children.Add(note);
        UIElement content;
        if (nfts.Count == 0)
        {
            content = ActivityTabsView.EmptyNote(isLoading ? "Loading…" : state is Portfolio.PortfolioState.Unavailable ? "NFT data is unavailable." : layerEmptyText ?? "No NFTs in this account.");
        }
        else
        {
            var tiles = new ItemsRepeater
            {
                Layout = new UniformGridLayout { MinItemWidth = 140, MinItemHeight = 166, MinColumnSpacing = 12, MinRowSpacing = 16, ItemsStretch = UniformGridLayoutItemsStretch.None },
                ItemsSource = nfts.Select(Tile).ToList(),
            };
            var scroller = new ScrollViewer { Content = tiles, Padding = new Thickness(0, 2, 0, 2), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            // A click between the tiles clears the selection.
            scroller.Tapped += (_, e) => { if (!e.Handled) { selection.Clear(); Restyle(); } };
            content = scroller;
        }
        Grid.SetRow((FrameworkElement)content, 1);
        root.Children.Add(content);
    }

    private UIElement Tile(NFT nft)
    {
        var (tile, frame, caption) = NFTTile(nft, 140);
        frames[nft.Id] = frame;
        captions[nft.Id] = caption;
        StyleTile(nft.Id);
        tile.Tapped += (_, e) => { e.Handled = true; Click(nft); };
        tile.ContextRequested += (_, e) =>
        {
            e.Handled = true;
            var menu = ContextMenu(Targets(nft));
            if (e.TryGetPosition(tile, out var point)) menu.ShowAt(tile, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = point });
            else menu.ShowAt(tile);
        };
        return tile;
    }

    /// <summary>The tokens a menu on <c>nft</c> acts on: the selection when the tile is in it, else that tile alone.</summary>
    private IReadOnlyList<NFT> Targets(NFT nft) =>
        selection.Contains(nft.Id) ? nfts.Where(n => selection.Contains(n.Id)).ToList() : [nft];

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void Click(NFT nft)
    {
        if (IsDown(VirtualKey.Control))
        {
            if (!selection.Remove(nft.Id)) selection.Add(nft.Id);
        }
        else if (IsDown(VirtualKey.Shift) && anchor is { } start && IndexOf(start) is { } from && IndexOf(nft.Id) is { } to)
        {
            selection.Clear();
            for (var i = Math.Min(from, to); i <= Math.Max(from, to); i++) selection.Add(nfts[i].Id);
            Restyle();
            return;
        }
        else
        {
            selection.Clear();
            selection.Add(nft.Id);
        }
        anchor = nft.Id;
        Restyle();
    }

    private int? IndexOf(string id) => nfts.Select((n, i) => (n, i)).FirstOrDefault(p => p.n.Id == id) is var (found, index) && found is not null ? index : null;

    private void Restyle()
    {
        foreach (var id in frames.Keys) StyleTile(id);
    }

    private void StyleTile(string id)
    {
        var selected = selection.Contains(id);
        frames[id].BorderBrush = selected
            ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
            : (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"];
        frames[id].BorderThickness = new Thickness(selected ? 3 : 1);
        captions[id].Foreground = (Brush)Application.Current.Resources[selected ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush"];
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(frames[id], selected ? "Selected" : "");
    }

    // ---- Context menu ----------------------------------------------------------------------------

    /// <summary>
    /// The right-click menu of one tile or of the selection: Transfer and Burn when the account
    /// can sign for its tokens (never for a selection spanning Tezos and Etherlink, or a soulbound
    /// token), Download for the artwork, then the links and copies of a single token, then selection.
    /// </summary>
    private MenuFlyout ContextMenu(IReadOnlyList<NFT> targets)
    {
        var layer = targets.FirstOrDefault()?.Layer ?? 1;
        var sameLayer = targets.Select(n => n.Layer).Distinct().Count() == 1;
        var transferable = targets.All(n => n.IsTransferable);
        var canOperate = sameLayer && transferable && targets.Count <= TokenTransferRequest.MaxTokens && (actions?.CanOperate(layer) ?? false);
        var plural = targets.Count == 1 ? "" : $" {targets.Count} NFTs";

        var menu = new MenuFlyout();
        menu.Items.Add(Item($"Transfer{plural}…", () => actions?.Start(new NFTOperation(NFTOperation.OperationKind.Transfer, targets)), canOperate, ""));
        menu.Items.Add(Item($"Burn{plural}…", () => actions?.Start(new NFTOperation(NFTOperation.OperationKind.Burn, targets)), canOperate, ""));
        if (Reason(targets, sameLayer, transferable, layer) is { } reason)
            menu.Items.Add(new MenuFlyoutItem { Text = reason, IsEnabled = false });
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item($"Download{plural}…", () => actions?.Download(new NFTDownload(targets)),
                            actions is not null && targets.All(n => n.ArtifactURLs.Count > 0 || n.ImageURLs.Count > 0), ""));
        if (targets.Count == 1 && targets[0] is { } nft)
        {
            if (actions is not null)
            {
                var network = actions.Network(nft.Layer);
                if (network.ExplorerURLForToken(nft.Contract, nft.TokenId) is { } explorer) menu.Items.Add(Open("Open in Explorer", explorer));
                if (network.ObjktURL(nft.Contract, nft.TokenId) is { } objkt) menu.Items.Add(Open("Open on objkt.com", objkt));
            }
            if (nft.ArtifactURLs.FirstOrDefault() is { } artwork)
                menu.Items.Add(Open(nft.IsMoving ? "View Artwork (plays in the browser)" : "View Artwork", artwork));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Item("Copy Contract Address", () => Pasteboard.SetString(nft.Contract)));
            menu.Items.Add(Item("Copy Token ID", () => Pasteboard.SetString(nft.TokenId)));
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Select All", () => { foreach (var n in nfts) selection.Add(n.Id); Restyle(); }, selection.Count < nfts.Count));
        if (selection.Count > 0) menu.Items.Add(Item("Deselect All", () => { selection.Clear(); Restyle(); }));
        return menu;
    }

    /// <summary>Why Transfer and Burn are off, when they are.</summary>
    private string? Reason(IReadOnlyList<NFT> targets, bool sameLayer, bool transferable, int layer)
    {
        if (!sameLayer) return "Choose NFTs on one chain at a time";
        if (!transferable) return targets.Count == 1 ? "This token cannot be transferred" : "One of these tokens cannot be transferred";
        if (targets.Count > TokenTransferRequest.MaxTokens) return $"At most {TokenTransferRequest.MaxTokens} NFTs at once";
        if (actions is not null && !actions.CanOperate(layer)) return layer == 2 ? "Signet cannot sign for this account on Etherlink" : "Signet cannot sign for this account";
        return null;
    }

    private static MenuFlyoutItem Item(string text, Action action, bool enabled = true, string? glyph = null)
    {
        var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
        if (glyph is not null) item.Icon = new FontIcon { Glyph = glyph };
        item.Click += (_, _) => action();
        return item;
    }

    private static MenuFlyoutItem Open(string text, Uri url) => Item(text, () => _ = Launcher.LaunchUriAsync(url));

    // ---- Tile ------------------------------------------------------------------------------------

    /// <summary>
    /// One tile: the artwork centre-cropped into a square, the Etherlink mark, a play or speaker
    /// badge for a video or sound, the edition count, and the name. <c>frame</c> carries the
    /// selection outline and <c>caption</c> the name.
    /// </summary>
    internal static (StackPanel Tile, Border Frame, TextBlock Caption) NFTTile(NFT nft, double size, bool showName = true)
    {
        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        var square = new Grid { Width = size, Height = size };
        var radius = new CornerRadius(size >= 100 ? 10 : 6);
        square.Children.Add(new Border { Background = (Brush)Application.Current.Resources["ControlAltFillColorTertiaryBrush"], CornerRadius = radius });
        var placeholder = new ProgressRing { Width = 20, Height = 20, IsActive = nft.ImageURLs.Count > 0 };
        var failed = new FontIcon { Glyph = "", FontSize = size >= 100 ? 22 : 14, Foreground = secondary, Visibility = nft.ImageURLs.Count > 0 ? Visibility.Collapsed : Visibility.Visible };
        square.Children.Add(placeholder);
        square.Children.Add(failed);
        var image = new Image { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        square.Children.Add(new Border { CornerRadius = radius, Child = image });
        var frame = new Border { CornerRadius = radius, BorderThickness = new Thickness(1), BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"] };
        square.Children.Add(frame);
        if (size >= 100)
        {
            if (nft.IsLayer2)
                square.Children.Add(Badge(ActivityTabsView.EtherlinkMark(16), HorizontalAlignment.Left, VerticalAlignment.Top, circle: true));
            if (nft.IsMoving)
            {
                var audio = nft.ArtifactMime?.StartsWith("audio/", StringComparison.Ordinal) == true;
                var badge = Badge(new FontIcon { Glyph = audio ? "" : "", FontSize = 10, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) },
                                  HorizontalAlignment.Right, VerticalAlignment.Bottom);
                ToolTipService.SetToolTip(badge, audio ? "A sound: the tile shows its cover image" : "A video: the tile shows its still image");
                square.Children.Add(badge);
            }
            if (nft.Balance > 1)
                square.Children.Add(Badge(new TextBlock
                {
                    Text = $"×{nft.Balance.ToString("0.##", CultureInfo.CurrentCulture)}", FontSize = 10, FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                }, HorizontalAlignment.Right, VerticalAlignment.Top));
        }
        if (nft.ImageURLs.Count > 0)
            ArtworkImages.Load(image, nft.ImageURLs, (int)Math.Max(80, size * 2), ok =>
            {
                placeholder.IsActive = false;
                failed.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
            });

        var tile = new StackPanel { Spacing = 6, Width = size, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        tile.Children.Add(square);
        var caption = new TextBlock { Text = nft.Name, FontSize = 12, Foreground = secondary, TextTrimming = TextTrimming.CharacterEllipsis };
        if (showName) tile.Children.Add(caption);
        ToolTipService.SetToolTip(tile, (nft.IsLayer2 ? "On Etherlink\n" : "") + $"{nft.Name}\n{nft.Contract} #{nft.TokenId}");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(tile, nft.Name + (nft.IsMoving ? ", video or sound" : "") + (nft.IsLayer2 ? ", on Etherlink" : ""));
        return (tile, frame, caption);
    }

    private static Border Badge(UIElement content, HorizontalAlignment horizontal, VerticalAlignment vertical, bool circle = false) => new()
    {
        HorizontalAlignment = horizontal, VerticalAlignment = vertical, Margin = new Thickness(6),
        Padding = circle ? new Thickness(3) : new Thickness(6, 2, 6, 2), CornerRadius = new CornerRadius(circle ? 11 : 8),
        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(153, 0, 0, 0)),
        Child = content,
    };
}
