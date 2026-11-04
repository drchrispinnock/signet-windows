using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Signet.Models;
using Signet.Services;
using Signet.ViewModels;

namespace Signet.App.Views;

/// <summary>
/// Shielded settings: shielded transactions (TzEL) on or off per layer 1 network, the services
/// of the deployment Signet talks to there (each replaceable, with Use Default) and the
/// operator's access token. Opened from "Shielded settings…" under Networks in the burger menu.
/// </summary>
public sealed partial class ShieldSettingsSheet : Sheet
{
    private readonly WalletViewModel model;
    private readonly ComboBox picker = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ToggleSwitch enabled = new() { Header = "Enable Shielded Transactions" };
    private readonly TextBlock status = new() { Style = (Style)Application.Current.Resources["CautionText"], TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel details = new() { Spacing = 16 };
    private bool syncing;

    public ShieldSettingsSheet(WalletViewModel model) : base("Shielded settings", 520)
    {
        this.model = model;
        foreach (var network in Network.All)
            picker.Items.Add(new ComboBoxItem { Content = network.Name + (network.Name == model.Network.Name ? "  (in use)" : ""), Tag = network.Name });
        // Starts on the network in use.
        picker.SelectedIndex = Math.Max(0, Network.All.ToList().FindIndex(n => n.Name == model.Network.Name));

        Form.Children.Add(new SectionHeader("Shielded Transactions")
        {
            Help = "With Shielded Transactions on, the dashboard shows the account's shielded tez on the TzEL rollup of the chosen layer 1 network, and the menu offers Shield tez, Unshield tez, Send shielded and Shielded address. The choice and the settings below are kept per network.",
        });
        Form.Children.Add(new TextBlock { Text = "Network" });
        Form.Children.Add(picker);
        Form.Children.Add(enabled);
        Form.Children.Add(status);
        Form.Children.Add(details);
        CloseButtonText = "Done";
        DefaultButton = ContentDialogButton.Close;

        picker.SelectionChanged += (_, _) => Sync();
        enabled.Toggled += (_, _) =>
        {
            if (syncing || EditingNetwork is not { } @base || enabled.IsOn == model.IsShieldedEnabled(@base)) return;
            model.SetShieldedEnabled(enabled.IsOn, @base);
            Sync();
        };
        Sync();
    }

    /// <summary>The layer 1 network whose shielded settings are being edited.</summary>
    private Network? EditingNetwork => picker.SelectedItem is ComboBoxItem { Tag: string name } ? Network.Named(name) : null;

    private void Sync()
    {
        if (EditingNetwork is not { } @base) return;
        syncing = true;
        var on = model.IsShieldedEnabled(@base);
        enabled.IsOn = on;
        syncing = false;
        status.Text = !on ? ""
            : @base.ShieldedDefault is null ? $"Signet ships no TzEL services for {@base.Name}. Fill in every item below to shield there."
            : model.Resolved(@base).Shielded is null ? "Shielding is off until every item below is filled in." : "";
        status.Visibility = status.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        details.Children.Clear();
        if (!on) return;
        details.Children.Add(new SectionHeader("TzEL services")
        {
            Help = "The rollup node serves the notes and nullifiers; the rollup and bridge ticketer are checked before any tez move; the proving service makes the proofs; the operator relays them to the rollup. The producer fee is the note every shielded operation pays the DAL slot producer, in mutez, and the producer address is that producer's payment address, the JSON it publishes. Plain http is accepted. Use Default restores what Signet ships, or clears an item where it ships nothing.",
        });
        foreach (var setting in ShieldedConfig.AllSettings)
            details.Children.Add(new SettingField(setting.Title(), () => model.ShieldedSetting(setting, @base),
                                                  @base.ShieldedDefault?.Text(setting) ?? "", setting.Prompt(), monospaced: true,
                                                  text => { var ok = model.SetShieldedSetting(text, setting, @base); if (ok) Refresh(); return ok; },
                                                  _ => setting.Problem(),
                                                  () => { model.UseDefaultShieldedSetting(setting, @base); Refresh(); },
                                                  multiline: setting == ShieldedConfig.Setting.ProducerAddress));

        details.Children.Add(new SectionHeader("Access")
        {
            Help = "The operator relays shielded operations only with its access token. It is kept with the shielded wallet in the wallet directory, never in Signet's settings, and shielding is refused until it is on file.",
        });
        var isSet = model.ShieldedOperatorTokenIsSet(@base);
        var token = new PasswordBox
        {
            Header = "Operator token",
            PlaceholderText = isSet ? "On file; paste a new one to replace it" : "Paste the operator's access token",
        };
        var error = new TextBlock { Style = (Style)Application.Current.Resources["ErrorText"], Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        var apply = new Button { Content = "Apply", IsEnabled = false };
        var remove = new Button { Content = "Remove", Visibility = isSet ? Visibility.Visible : Visibility.Collapsed };
        void Apply(string text)
        {
            error.Visibility = Visibility.Collapsed;
            try
            {
                model.SetShieldedOperatorToken(text, @base);
                Sync();
            }
            catch (Exception e)
            {
                error.Text = e.Message;
                error.Visibility = Visibility.Visible;
            }
        }
        token.PasswordChanged += (_, _) => apply.IsEnabled = token.Password.Trim().Length > 0;
        token.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter || token.Password.Trim().Length == 0) return;
            e.Handled = true;
            Apply(token.Password);
        };
        apply.Click += (_, _) => Apply(token.Password);
        remove.Click += (_, _) => Apply("");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(remove);
        buttons.Children.Add(apply);
        details.Children.Add(token);
        details.Children.Add(error);
        details.Children.Add(buttons);
    }

    /// <summary>Only the status line depends on the items; the fields keep their focus.</summary>
    private void Refresh()
    {
        if (EditingNetwork is not { } @base) return;
        status.Text = @base.ShieldedDefault is null && model.Resolved(@base).Shielded is null ? $"Signet ships no TzEL services for {@base.Name}. Fill in every item below to shield there."
            : model.Resolved(@base).Shielded is null ? "Shielding is off until every item below is filled in." : "";
        status.Visibility = status.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
