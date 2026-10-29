using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Signet.Models;
using Signet.ViewModels;

namespace Signet.App.Views;

/// <summary>
/// Send, Receive, Buy (Get on testnets), Stake. Selling is out of scope: it would mean handling bank
/// accounts. Buttons whose flow does not exist yet are disabled.
/// </summary>
public sealed partial class ActionButtonsView : UserControl
{
    private sealed record Action(string Title, string Glyph, bool Enabled, System.Action Perform);

    private readonly WalletViewModel model;
    private readonly Grid row = new() { ColumnSpacing = 12 };
    private readonly TextBlock reason = new() { Style = (Style)Application.Current.Resources["CaptionSecondaryText"], TextWrapping = TextWrapping.Wrap };
    private readonly Grid reasonRow = new() { ColumnSpacing = 6 };

    public ActionButtonsView(WalletViewModel model)
    {
        this.model = model;
        for (var i = 0; i < 4; i++) row.ColumnDefinitions.Add(new ColumnDefinition());
        reasonRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        reasonRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        reasonRow.Children.Add(new FontIcon { Glyph = "", FontSize = 12, Foreground = reason.Foreground, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetColumn(reason, 1);
        reasonRow.Children.Add(reason);
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(row);
        stack.Children.Add(reasonRow);
        Content = stack;
        model.PropertyChanged += Changed;
        Update();
    }

    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WalletViewModel.SelectedWallet) or nameof(WalletViewModel.AccountNotOnChain) or nameof(WalletViewModel.Network))
            Update();
    }

    /// <summary>Address-book entries have no key, so none of the actions apply to them.</summary>
    private bool IsWatchOnly => model.SelectedWallet?.KeyKind == KeyKind.None;
    /// <summary>Faucets only fund implicit accounts; a contract (KT1) is funded by sending to it.</summary>
    private bool IsContract => model.SelectedWallet?.Address.IsContract == true;
    /// <summary>Keys on disk (clear or encrypted) and on a Ledger can sign; remote signers cannot yet.</summary>
    private bool CanSign => model.SelectedWallet?.KeyKind.CanSign() == true;

    // Windows: Buy is not ported yet, so it stays off whatever the account.
    private IReadOnlyList<Action> Actions =>
    [
        new("Send", "", CanSign && !model.AccountNotOnChain, () => model.IsPresentingSend = true),
        new("Receive", "", model.SelectedWallet is not null && !IsWatchOnly, () => model.IsPresentingReceive = true),
        model.Network.FaucetURL is not null
            ? new("Get", "", model.SelectedWallet is not null && !IsContract, () => model.IsPresentingFaucet = true)
            : new("Buy", "", false, () => model.IsPresentingBuy = true),
        new("Stake", "", CanSign && !model.AccountNotOnChain, () => model.IsPresentingStaking = true),
    ];

    private string HelpText(Action action)
    {
        if (action.Enabled) return action.Title;
        if (action.Title is "Buy" && SendUnavailableReason is null && !IsWatchOnly) return $"{action.Title} is not available on Windows yet";
        if (action.Title == "Buy" && !model.Network.IsMainnet) return "Buying is only possible on Mainnet";
        if (action.Title == "Get" && IsContract) return "The faucet cannot fund a contract; send it tez from an account";
        if (IsWatchOnly) return "Not available for a watch-only address";
        if (action.Title == "Send")
        {
            if (model.AccountNotOnChain) return "This address has no tez on this network";
            if (model.SelectedWallet?.KeyKind is { } kind && !kind.CanSign()) return $"Sending with {kind.ToString().ToLowerInvariant()} keys is not supported yet";
        }
        return $"{action.Title} is coming soon";
    }

    /// <summary>Why Send is off, spelled out under the row so it is not just a tooltip.</summary>
    private string? SendUnavailableReason
    {
        get
        {
            if (model.SelectedWallet is not { } wallet || CanSign && !model.AccountNotOnChain) return null;
            if (IsWatchOnly) return "This is an address-book entry; Signet holds no key for it, so it cannot send.";
            if (model.AccountNotOnChain) return $"This address has no tez on {model.Network.Name}, so there is nothing to send.";
            return wallet.KeyKind == KeyKind.Remote
                ? "This account signs through a remote signer, which Signet does not support yet."
                : "Signet cannot sign for this account yet.";
        }
    }

    private void Update()
    {
        row.Children.Clear();
        var actions = Actions;
        for (var i = 0; i < actions.Count; i++)
        {
            var action = actions[i];
            var tile = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
            tile.Children.Add(new FontIcon { Glyph = action.Glyph, FontSize = 18, Height = 22 });
            tile.Children.Add(new TextBlock { Text = action.Title, HorizontalAlignment = HorizontalAlignment.Center });
            // A tile that always fills its share of the row, so all four buttons are the same size.
            var button = new Button
            {
                Content = tile, IsEnabled = action.Enabled, HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(0, 12, 0, 12), CornerRadius = new CornerRadius(8),
            };
            button.Click += (_, _) => action.Perform();
            // A disabled button shows no tooltip, so it goes on a wrapper.
            var wrapper = new Border { Child = button, Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            ToolTipService.SetToolTip(wrapper, HelpText(action));
            Grid.SetColumn(wrapper, i);
            row.Children.Add(wrapper);
        }
        var text = SendUnavailableReason;
        reason.Text = text ?? "";
        reasonRow.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
