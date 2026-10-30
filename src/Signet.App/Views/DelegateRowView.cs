using System.ComponentModel;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Signet.Models;
using Signet.ViewModels;

namespace Signet.App.Views;

/// <summary>
/// One line under the balances: who the wallet delegates to (or that it is a baker). A click opens
/// Staking, or Sign multisig for a contract, as upstream does.
/// </summary>
public sealed partial class DelegateRowView : UserControl
{
    private readonly WalletViewModel model;
    private readonly ContentControl icon = new() { Width = 44, Height = 44 };
    private readonly TextBlock headline = new() { FontWeight = FontWeights.Medium };
    private readonly TextBlock subtitle = new() { Style = (Style)Application.Current.Resources["CaptionSecondaryText"], TextWrapping = TextWrapping.Wrap };
    private string? delegateName;
    private Address? nameFor;

    public DelegateRowView(WalletViewModel model)
    {
        this.model = model;
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(headline);
        text.Children.Add(subtitle);
        Grid.SetColumn(text, 1);
        var chevron = new FontIcon { Glyph = "", FontSize = 12, Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"], VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(chevron, 2);
        row.Children.Add(icon);
        row.Children.Add(text);
        row.Children.Add(chevron);
        // Click → Staking, or for a contract Sign multisig on it: a contract delegates through its signers.
        var button = new Button
        {
            Content = row, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), Padding = new Thickness(0, 4, 0, 4),
        };
        button.Click += (_, _) =>
        {
            if (model.SelectedWallet is { Address.IsContract: true } wallet)
            {
                model.SignMultisigPreset = new WalletViewModel.MultisigSheetPreset(new MultisigContract(wallet.Alias, wallet.Address), Delegating: true);
                model.IsPresentingSignMultisig = true;
            }
            else
            {
                model.IsPresentingStaking = true;
            }
        };
        ToolTipService.SetToolTip(button, "Delegation and staking");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, "Delegation and staking");
        Content = button;
        model.PropertyChanged += Changed;
        Update();
    }

    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WalletViewModel.DelegateInfo) or nameof(WalletViewModel.SelectedWallet)) Update();
    }

    private async void Update()
    {
        var info = model.DelegateInfo;
        icon.Content = Icon(info);
        headline.Text = Headline(info);
        subtitle.Text = Subtitle(info);

        // The delegate's name: one of ours, else its TzProfile, else the shortened address.
        var target = info is { IsBaker: false } ? info.Delegate : null;
        if (target == nameFor) return;
        nameFor = target;
        delegateName = null;
        if (target is not { } address) return;
        var known = model.DisplayName(address, null);
        var name = known.IsOurs ? known.Name : await model.ProfileName(address) ?? address.Shortened();
        if (nameFor != target) return;
        delegateName = name;
        headline.Text = Headline(model.DelegateInfo);
    }

    private static UIElement Icon(DelegateInfo? info)
    {
        if (info is { IsBaker: false, Delegate: { } delegateAddress }) return new AccountAvatarView(delegateAddress, 44);
        var grid = new Grid();
        grid.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Fill = (Brush)Application.Current.Resources["ControlAltFillColorTertiaryBrush"] });
        grid.Children.Add(info?.IsBaker == true
            ? new TextBlock { Text = "👨‍🍳", FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            : new FontIcon { Glyph = "", Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
        return grid;
    }

    private string Headline(DelegateInfo? info)
    {
        if (info is null) return "Delegation";
        if (info.IsBaker) return model.SelectedWallet?.KeyKind.CanSign() == true ? "You are a baker" : "This address is a baker";
        if (info.Delegate is { } delegateAddress) return $"Delegated to {delegateName ?? delegateAddress.Shortened()}";
        return "Not delegated";
    }

    private string Subtitle(DelegateInfo? info)
    {
        if (info is null) return "Checking…";
        if (model.SelectedWallet?.Address.IsContract == true)
            return "A multisig delegates with its signers' signatures; tap to propose a change.";
        if (info.Baker is { } baker)
        {
            if (model.SelectedWallet?.KeyKind is { } kind && !kind.CanSign())
            {
                var why = kind == KeyKind.Remote ? "it signs remotely" : "Signet holds only its public key";
                return (baker.Deactivated ? "Deactivated. " : "") + $"Watch only: {why}. Baking shows its keys and parameters.";
            }
            return baker.Deactivated ? "Deactivated; see Baking in the menu" : "Self-delegated; keys and staking parameters under Baking";
        }
        if (info.Delegate is not null)
            return info.DelegateAcceptsStaking switch
            {
                true => "This baker accepts staked tez",
                false => "This baker does not accept staking",
                null => "Tap to stake or change delegate",
            };
        return "Delegate to a baker to earn rewards";
    }
}
