using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Signet.App.Services;
using Signet.Models;
using Signet.Services;
using Signet.ViewModels;

namespace Signet.App.Views;

/// <summary>Bottom section of the dashboard: recent transactions, tokens or NFTs, chosen with a segmented control.</summary>
public sealed partial class ActivityTabsView : UserControl
{
    private readonly WalletViewModel model;
    private readonly SelectorBar tabs = new() { MaxWidth = 400, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ContentControl body = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };

    private static readonly (WalletViewModel.ActivityTab Tab, string Title)[] Tabs =
    [
        (WalletViewModel.ActivityTab.Transactions, "Recent transactions"),
        (WalletViewModel.ActivityTab.Assets, "Assets"),
        (WalletViewModel.ActivityTab.Nfts, "NFTs"),
    ];

    public ActivityTabsView(WalletViewModel model)
    {
        this.model = model;
        foreach (var (tab, title) in Tabs)
            tabs.Items.Add(new SelectorBarItem { Text = title, Tag = tab, IsSelected = tab == model.ActivityTabSelection });
        tabs.SelectionChanged += (_, _) =>
        {
            if (tabs.SelectedItem?.Tag is WalletViewModel.ActivityTab tab) model.ActivityTabSelection = tab;
        };
        var grid = new Grid { RowSpacing = 10 };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(body, 1);
        grid.Children.Add(tabs);
        grid.Children.Add(body);
        Content = grid;
        model.PropertyChanged += Changed;
        Update();
    }

    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WalletViewModel.ActivityTabSelection) or nameof(WalletViewModel.Transactions) or nameof(WalletViewModel.Tokens)
            or nameof(WalletViewModel.Nfts) or nameof(WalletViewModel.IsLoading) or nameof(WalletViewModel.PortfolioState) or nameof(WalletViewModel.Wallets))
            Update();
    }

    private void Update()
    {
        body.Content = model.ActivityTabSelection switch
        {
            WalletViewModel.ActivityTab.Transactions => TransactionList(),
            WalletViewModel.ActivityTab.Assets => TokenList(),
            _ => NFTGrid(),
        };
    }

    internal static TextBlock EmptyNote(string text) => new()
    {
        Text = text, Style = (Style)Application.Current.Resources["SecondaryText"],
        HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 24, 0, 0),
    };

    // ---- Transactions ----------------------------------------------------------------------------

    /// <summary>The last few operations, each with the counterparty's avatar and best-known name.</summary>
    private UIElement TransactionList()
    {
        if (model.Transactions.Count == 0) return EmptyNote(model.IsLoading ? "Loading…" : "No transactions yet.");
        var list = new StackPanel();
        foreach (var tx in model.Transactions)
        {
            list.Children.Add(new TransactionRowView(model, tx));
            list.Children.Add(Divider(56));
        }
        return new ScrollViewer { Content = list };
    }

    internal static Border Divider(double leading) => new()
    {
        Height = 1, Margin = new Thickness(leading, 0, 0, 0),
        Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
    };

    // ---- Tokens ----------------------------------------------------------------------------------

    /// <summary>The Assets tab: fungible (DeFi) token balances with their logos.</summary>
    private UIElement TokenList()
    {
        var state = model.PortfolioState;
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (model.Tokens.Count == 0)
        {
            grid.Children.Add(EmptyNote(model.IsLoading ? "Loading…" : state is Portfolio.PortfolioState.Unavailable ? "Token data is unavailable." : "No tokens in this account."));
        }
        else
        {
            var list = new StackPanel();
            foreach (var token in model.Tokens)
            {
                list.Children.Add(TokenRow(token));
                list.Children.Add(Divider(56));
            }
            grid.Children.Add(new ScrollViewer { Content = list });
        }
        if (PortfolioStateNote(state) is { } note)
        {
            Grid.SetRow(note, 1);
            grid.Children.Add(note);
        }
        return grid;
    }

    /// <summary>A line under the Assets and NFTs tabs when the list is not the whole story (S26).</summary>
    private static FrameworkElement? PortfolioStateNote(Portfolio.PortfolioState state)
    {
        var text = state switch
        {
            Portfolio.PortfolioState.Partial p => $"Showing the {p.Fetched} most recently changed tokens; older holdings may be missing.",
            Portfolio.PortfolioState.Unavailable u => $"The indexer could not be read ({u.Reason}). Showing the last known list.",
            _ => null,
        };
        if (text is null) return null;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 6, 0, 6) };
        row.Children.Add(new FontIcon { Glyph = "", FontSize = 12, Foreground = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"] });
        row.Children.Add(new TextBlock { Text = text, FontSize = 12, MaxLines = 2, Style = (Style)Application.Current.Resources["CautionText"] });
        return row;
    }

    private UIElement TokenRow(AssetBalance token)
    {
        var contract = token.Kind is AssetBalance.AssetKind.Token t ? t.Contract : null;
        var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 8, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var logo = new Grid { Width = 40, Height = 40 };
        logo.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Fill = (Brush)Application.Current.Resources["ControlAltFillColorTertiaryBrush"] });
        logo.Children.Add(new TextBlock
        {
            Text = token.Symbol.Length > 3 ? token.Symbol[..3] : token.Symbol, FontSize = 11, FontWeight = FontWeights.Bold,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        });
        if (token.IconURLs.Count > 0)
        {
            var image = new Image { Stretch = Stretch.UniformToFill };
            logo.Children.Add(new Border { CornerRadius = new CornerRadius(20), Child = image });
            ImageLoader.Load(image, token.IconURLs, 80);
        }
        logo.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Stroke = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"], StrokeThickness = 1 });
        row.Children.Add(logo);

        var names = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(new TextBlock { Text = token.Name, FontWeight = FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis });
        var detail = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (contract is not null)
            detail.Children.Add(new TextBlock { Text = new Address(contract).Shortened(), Style = (Style)Application.Current.Resources["MonoText"], Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
        if (token.Standard is { } standard)
            detail.Children.Add(new Border
            {
                Style = (Style)Application.Current.Resources["TagBorder"],
                Child = new TextBlock { Text = standard.ToUpperInvariant(), FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] },
            });
        names.Children.Add(detail);
        Grid.SetColumn(names, 1);
        row.Children.Add(names);

        var amount = new TextBlock { Text = token.FormattedAmount, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(amount, 2);
        row.Children.Add(amount);

        ToolTipService.SetToolTip(row, contract is null ? token.Name : $"{token.Name}\n{contract}");
        if (contract is not null)
        {
            var menu = new MenuFlyout();
            var copy = new MenuFlyoutItem { Text = "Copy contract address" };
            copy.Click += (_, _) => Pasteboard.SetString(contract);
            menu.Items.Add(copy);
            if (model.Network.ExplorerURLForContract(contract) is { } url)
            {
                var view = new MenuFlyoutItem { Text = "View in explorer" };
                view.Click += async (_, _) => await Windows.System.Launcher.LaunchUriAsync(url);
                menu.Items.Add(view);
            }
            row.ContextFlyout = menu;
        }
        return row;
    }

    // ---- NFTs ------------------------------------------------------------------------------------

    /// <summary>Grid of NFT thumbnails.</summary>
    private UIElement NFTGrid()
    {
        var state = model.PortfolioState;
        var stack = new Grid { RowSpacing = 10 };
        stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        stack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        if (PortfolioStateNote(state) is { } note) stack.Children.Add(note);
        UIElement content;
        if (model.Nfts.Count == 0)
        {
            content = EmptyNote(model.IsLoading ? "Loading…" : state is Portfolio.PortfolioState.Unavailable ? "NFT data is unavailable." : "No NFTs in this account.");
        }
        else
        {
            var tiles = new ItemsRepeater
            {
                Layout = new UniformGridLayout { MinItemWidth = 140, MinItemHeight = 166, MinColumnSpacing = 12, MinRowSpacing = 16, ItemsStretch = UniformGridLayoutItemsStretch.None },
                ItemsSource = model.Nfts.Select(NFTTile).ToList(),
            };
            content = new ScrollViewer { Content = tiles, Padding = new Thickness(0, 2, 0, 2) };
        }
        Grid.SetRow((FrameworkElement)content, 1);
        stack.Children.Add(content);
        return stack;
    }

    private static UIElement NFTTile(NFT nft)
    {
        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        var square = new Grid { Width = 140, Height = 140 };
        square.Children.Add(new Border { Background = (Brush)Application.Current.Resources["ControlAltFillColorTertiaryBrush"], CornerRadius = new CornerRadius(10) });
        var placeholder = new ProgressRing { Width = 20, Height = 20, IsActive = nft.ImageURLs.Count > 0 };
        var failed = new FontIcon { Glyph = "", FontSize = 22, Foreground = secondary, Visibility = nft.ImageURLs.Count > 0 ? Visibility.Collapsed : Visibility.Visible };
        square.Children.Add(placeholder);
        square.Children.Add(failed);
        var image = new Image { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        square.Children.Add(new Border { CornerRadius = new CornerRadius(10), Child = image });
        square.Children.Add(new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"] });
        if (nft.Balance > 1)
            square.Children.Add(new Border
            {
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6),
                Padding = new Thickness(6, 2, 6, 2), CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(153, 0, 0, 0)),
                Child = new TextBlock { Text = $"×{nft.Balance.ToString("0.##", CultureInfo.CurrentCulture)}", FontSize = 10, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) },
            });
        if (nft.ImageURLs.Count > 0)
            ImageLoader.Load(image, nft.ImageURLs, 260, ok =>
            {
                placeholder.IsActive = false;
                failed.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
            });

        var tile = new StackPanel { Spacing = 6, Width = 140 };
        tile.Children.Add(square);
        tile.Children.Add(new TextBlock { Text = nft.Name, FontSize = 12, Foreground = secondary, TextTrimming = TextTrimming.CharacterEllipsis });
        ToolTipService.SetToolTip(tile, $"{nft.Name}\n{nft.Contract} #{nft.TokenId}");
        return tile;
    }
}

/// <summary>One line of history: the counterparty's avatar and name, what happened, and the amount.</summary>
public sealed partial class TransactionRowView : UserControl
{
    private readonly WalletViewModel model;
    private readonly TezosTransaction transaction;

    public TransactionRowView(WalletViewModel model, TezosTransaction transaction)
    {
        this.model = model;
        this.transaction = transaction;
        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        var tertiary = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
        var party = Party;

        var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(4, 8, 4, 8), CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var avatar = new Grid { Width = 44, Height = 44 };
        avatar.Children.Add(transaction.Counterparty is { } address
            ? new AccountAvatarView(address, 40) { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top }
            : new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 40, Height = 40, Fill = (Brush)Application.Current.Resources["ControlAltFillColorTertiaryBrush"], HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top });
        avatar.Children.Add(DirectionBadge());
        row.Children.Add(avatar);

        var middle = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        line.Children.Add(new TextBlock { Text = Headline, FontWeight = FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis });
        if (party.IsOurs)
        {
            var seal = new FontIcon { Glyph = "", FontSize = 12, Foreground = (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"] };
            ToolTipService.SetToolTip(seal, "One of your accounts or address-book entries");
            line.Children.Add(seal);
        }
        if (!transaction.IsApplied)
            line.Children.Add(new Border
            {
                Padding = new Thickness(5, 1, 5, 1), CornerRadius = new CornerRadius(7),
                Background = (Brush)Application.Current.Resources["SystemFillColorCriticalBackgroundBrush"],
                Child = new TextBlock { Text = "Failed", FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"] },
            });
        middle.Children.Add(line);
        middle.Children.Add(new TextBlock { Text = Subtitle, Style = (Style)Application.Current.Resources["CaptionSecondaryText"], TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(middle, 1);
        row.Children.Add(middle);

        var right = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        if (transaction.Amount > 0)
        {
            right.Children.Add(new TextBlock
            {
                Text = SignedAmount, FontWeight = FontWeights.Medium, HorizontalAlignment = HorizontalAlignment.Right,
                Foreground = transaction.Direction == TezosTransaction.TransactionDirection.Incoming
                    ? (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"] : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
            });
        }
        else if (transaction.Kind == TezosTransaction.TransactionKind.Delegation && transaction.DelegatedBalance is > 0 and var balance)
        {
            right.Children.Add(new TextBlock { Text = AssetBalance.Format(balance, "tz"), Foreground = secondary, HorizontalAlignment = HorizontalAlignment.Right });
            right.Children.Add(new TextBlock { Text = "delegated balance", FontSize = 10, Foreground = tertiary, HorizontalAlignment = HorizontalAlignment.Right });
        }
        else
        {
            right.Children.Add(new TextBlock { Text = transaction.Kind == TezosTransaction.TransactionKind.Delegation ? "Delegation" : "Call", Foreground = secondary, HorizontalAlignment = HorizontalAlignment.Right });
        }
        if (transaction.Direction != TezosTransaction.TransactionDirection.Incoming && transaction.Fee > 0)
            right.Children.Add(new TextBlock { Text = $"fee {AssetBalance.Format(transaction.Fee, "tz")}", FontSize = 10, Foreground = tertiary, HorizontalAlignment = HorizontalAlignment.Right });
        Grid.SetColumn(right, 2);
        row.Children.Add(right);

        var hover = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
        row.PointerEntered += (_, _) => row.Background = hover;
        row.PointerExited += (_, _) => row.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        row.Tapped += async (_, _) =>
        {
            if (model.Network.ExplorerURLForOperation(transaction.Hash) is { } url) await Windows.System.Launcher.LaunchUriAsync(url);
        };
        ToolTipService.SetToolTip(row, HelpText);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(row, $"{Headline}, {Subtitle}, {(transaction.Amount > 0 ? SignedAmount : "")}");
        Content = row;
    }

    private (string Name, bool IsOurs) Party =>
        transaction.Counterparty is { } address ? model.DisplayName(address, transaction.CounterpartyAlias) : ("Unknown", false);

    private TezosTransaction.DelegationRoleKind? Role =>
        model.SelectedWallet is { } wallet ? transaction.DelegationRole(wallet.Address) : null;

    private Border DirectionBadge()
    {
        var (glyph, color) = (Role, transaction.Direction) switch
        {
            (TezosTransaction.DelegationRoleKind.DelegatorJoined, _) => ("", "SystemFillColorSuccessBrush"),
            (TezosTransaction.DelegationRoleKind.DelegatorLeft or TezosTransaction.DelegationRoleKind.DelegatorMoved, _) => ("", "SystemFillColorCautionBrush"),
            (TezosTransaction.DelegationRoleKind.WeDelegated or TezosTransaction.DelegationRoleKind.WeUndelegated, _) => ("", "AccentFillColorDefaultBrush"),
            (_, TezosTransaction.TransactionDirection.Incoming) => ("", "SystemFillColorSuccessBrush"),
            (_, TezosTransaction.TransactionDirection.Outgoing) => ("", "AccentFillColorDefaultBrush"),
            _ => ("", "SystemFillColorNeutralBrush"),
        };
        return new Border
        {
            Width = 18, Height = 18, CornerRadius = new CornerRadius(9), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Background = (Brush)Application.Current.Resources[color],
            BorderBrush = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"], BorderThickness = new Thickness(1.5),
            Child = new FontIcon { Glyph = glyph, FontSize = 9, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) },
        };
    }

    private string Headline
    {
        get
        {
            var name = Party.Name;
            if (Role is { } role)
                return role switch
                {
                    TezosTransaction.DelegationRoleKind.WeDelegated => $"Delegated to {name}",
                    TezosTransaction.DelegationRoleKind.WeUndelegated => "Undelegated",
                    TezosTransaction.DelegationRoleKind.DelegatorJoined => $"{name} delegated to you",
                    TezosTransaction.DelegationRoleKind.DelegatorLeft => $"{name} undelegated",
                    _ => $"{name} moved their delegation",
                };
            if (transaction.Kind == TezosTransaction.TransactionKind.Origination) return $"Originated {name}";
            return transaction.Direction switch
            {
                TezosTransaction.TransactionDirection.Incoming => $"From {name}",
                TezosTransaction.TransactionDirection.SelfTransfer => "To yourself",
                _ => $"To {name}",
            };
        }
    }

    private string Subtitle
    {
        get
        {
            var parts = new List<string> { Relative(transaction.Timestamp) };
            if (transaction.Entrypoint is { } entrypoint) parts.Add($"entrypoint {entrypoint}");
            else if (transaction.IsContractCall) parts.Add("contract");
            if (transaction.Via is { } via) parts.Add($"via {model.DisplayName(via, null).Name}");
            if (!Party.IsOurs && transaction.CounterpartyAlias is not null) parts.Add("name from TzKT");
            return string.Join(" · ", parts);
        }
    }

    private string SignedAmount
    {
        get
        {
            var sign = transaction.Direction switch
            {
                TezosTransaction.TransactionDirection.Incoming => "+",
                TezosTransaction.TransactionDirection.Outgoing => "−",
                _ => "",
            };
            return sign + AssetBalance.Format(transaction.Amount, "tz");
        }
    }

    private string HelpText
    {
        get
        {
            var lines = new List<string> { transaction.Hash, $"Block {transaction.Level.ToString("N0", CultureInfo.CurrentCulture)}", transaction.Timestamp.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) };
            if (transaction.Counterparty is { } address) lines.Add(address.Value);
            if (model.Network.ExplorerURLForOperation(transaction.Hash) is not null) lines.Add("Click to view in the explorer");
            return string.Join("\n", lines);
        }
    }

    /// <summary>Abbreviated relative time, like <c>RelativeDateTimeFormatter</c>'s <c>.abbreviated</c> style ("3 hr. ago").</summary>
    private static string Relative(DateTimeOffset date)
    {
        var seconds = (DateTimeOffset.Now - date).TotalSeconds;
        if (seconds < 60) return seconds < 5 ? "now" : $"{(int)seconds} sec. ago";
        if (seconds < 3600) return $"{(int)(seconds / 60)} min. ago";
        if (seconds < 86400) return $"{(int)(seconds / 3600)} hr. ago";
        if (seconds < 7 * 86400) return $"{(int)(seconds / 86400)} day{((int)(seconds / 86400) == 1 ? "" : "s")} ago";
        if (seconds < 30 * 86400) return $"{(int)(seconds / (7 * 86400))} wk. ago";
        if (seconds < 365 * 86400) return $"{(int)(seconds / (30 * 86400))} mo. ago";
        return $"{(int)(seconds / (365 * 86400))} yr. ago";
    }
}
