using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Signet.App.Services;
using Signet.Models;
using Signet.ViewModels;

namespace Signet.App.Views;

/// <summary>
/// The Domains tab: the account's Tezos Domains names, the one it publishes as its own first,
/// each with where it points, who owns it and when it expires.
/// </summary>
public static class DomainListView
{
    public static UIElement Build(WalletViewModel model)
    {
        switch (model.CurrentDomainList)
        {
            case null or WalletViewModel.DomainListState.Loading:
                return ActivityTabsView.EmptyNote("Loading…");
            case WalletViewModel.DomainListState.Unavailable:
                return ActivityTabsView.EmptyNote("Tezos Domains names exist on Mainnet only.");
            case WalletViewModel.DomainListState.Failed failed:
            {
                var panel = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
                var note = ActivityTabsView.EmptyNote($"Tezos Domains could not be read: {failed.Reason}");
                note.TextWrapping = TextWrapping.Wrap;
                note.TextAlignment = TextAlignment.Center;
                panel.Children.Add(note);
                var retry = new Button { Content = "Try Again", HorizontalAlignment = HorizontalAlignment.Center };
                retry.Click += async (_, _) => await model.ReloadDomainList();
                panel.Children.Add(retry);
                return panel;
            }
            case WalletViewModel.DomainListState.Loaded { Domains: var domains }:
                if (domains.Count == 0) return ActivityTabsView.EmptyNote("No .tez names belong to or point at this account.");
                if (model.SelectedWallet?.Address is not { } account) return new Grid();
                var list = new StackPanel();
                foreach (var domain in domains)
                {
                    list.Children.Add(Row(domain, account, model));
                    list.Children.Add(ActivityTabsView.Divider(56));
                }
                return new ScrollViewer { Content = list };
            default:
                return new Grid();
        }
    }

    private static Grid Row(TezosDomain domain, Address account, WalletViewModel model)
    {
        var accent = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
        var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 8, 0, 8), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(20),
            Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
            Child = new FontIcon { Glyph = domain.IsReverseRecord ? "" : "", FontSize = 18, Foreground = accent },
        };
        row.Children.Add(icon);

        var text = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        title.Children.Add(new TextBlock { Text = domain.Name, FontWeight = FontWeights.Medium, IsTextSelectionEnabled = true, TextTrimming = TextTrimming.CharacterEllipsis });
        if (domain.IsReverseRecord)
        {
            var tag = new Border
            {
                Padding = new Thickness(6, 1, 6, 1), CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Center,
                Background = (Brush)Application.Current.Resources["SubtleFillColorTertiaryBrush"],
                Child = new TextBlock { Text = "Primary name", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] },
            };
            ToolTipService.SetToolTip(tag, "The account's reverse record: the name wallets and explorers show for this address.");
            title.Children.Add(tag);
        }
        text.Children.Add(title);
        text.Children.Add(new TextBlock { Text = Detail(domain, account, model), Style = (Style)Application.Current.Resources["SecondaryText"], TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        var expiry = Expiry(domain);
        Grid.SetColumn(expiry, 2);
        row.Children.Add(expiry);

        var menu = new MenuFlyout();
        AddItem(menu, "Copy Name", () => Pasteboard.SetString(domain.Name));
        if (domain.Address is { } address) AddItem(menu, "Copy Address It Points To", () => Pasteboard.SetString(address.Value));
        if (domain.AppURL is { } url) AddItem(menu, "Open on Tezos Domains", () => _ = Windows.System.Launcher.LaunchUriAsync(url));
        row.ContextFlyout = menu;
        return row;
    }

    private static void AddItem(MenuFlyout menu, string text, Action action)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    /// <summary>Who owns it and where it points, in words.</summary>
    private static string Detail(TezosDomain domain, Address account, WalletViewModel model)
    {
        var target = domain.Address is { } a ? (domain.PointsAt(account) ? "this account" : model.DisplayName(a, null).Name) : null;
        var points = target is null ? "Points nowhere" : $"Points to {target}";
        if (domain.IsOwnedBy(account)) return $"Owned by this account · {points}";
        var owner = domain.Owner is { } o ? model.DisplayName(o, null).Name : "someone else";
        return $"{points} · owned by {owner}";
    }

    private static FrameworkElement Expiry(TezosDomain domain)
    {
        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        if (domain.Expires is not { } expires)
        {
            var subdomain = new TextBlock { Text = "Subdomain", FontSize = 12, Foreground = secondary, VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(subdomain, "A subdomain lasts as long as its parent name.");
            return subdomain;
        }
        var days = (int)Math.Floor((expires - DateTimeOffset.Now).TotalDays);
        var panel = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        panel.Children.Add(new TextBlock { Text = days < 0 ? "Expired" : "Expires", FontSize = 12, Foreground = secondary, HorizontalAlignment = HorizontalAlignment.Right });
        var date = new TextBlock { Text = expires.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture), HorizontalAlignment = HorizontalAlignment.Right };
        if (days < 0) date.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        else if (days < 30) date.Foreground = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
        panel.Children.Add(date);
        var help = days < 0 ? "This name has expired; anyone may register it." : days < 30 ? "Renew it on Tezos Domains before it expires." : null;
        if (help is not null) ToolTipService.SetToolTip(panel, help);
        return panel;
    }
}
