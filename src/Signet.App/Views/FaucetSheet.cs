using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Signet.Models;
using Signet.Services;
using Signet.ViewModels;

namespace Signet.App.Views;

/// <summary>Asks the current testnet's faucet to send tez to the selected wallet.</summary>
public sealed partial class FaucetSheet : Sheet
{
    private readonly WalletViewModel model;
    private readonly TextBox amountText = new() { Header = "Amount (tz)", Text = ((int)WalletViewModel.FaucetAmountTez).ToString(CultureInfo.CurrentCulture) };
    private readonly TextBlock limitsText = new() { Style = (Style)Application.Current.Resources["NoteText"] };
    private readonly StackPanel status = new() { Spacing = 8 };
    private (double Min, double Max)? limits;

    public FaucetSheet(WalletViewModel model) : base("Get test tez", 460)
    {
        this.model = model;
        if (model.SelectedWallet is { } wallet)
        {
            var to = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            to.Children.Add(new AccountAvatarView(wallet.Address, 24));
            to.Children.Add(new TextBlock { Text = wallet.Alias, VerticalAlignment = VerticalAlignment.Center });
            to.Children.Add(new TextBlock { Text = wallet.Address.Shortened(), Style = (Style)Application.Current.Resources["MonoText"], Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], VerticalAlignment = VerticalAlignment.Center });
            Form.Children.Add(LabeledContent("To", to));
        }
        Form.Children.Add(LabeledContent("Faucet", new TextBlock { Text = model.Network.FaucetURL?.Host ?? "" }));
        Form.Children.Add(amountText);
        Form.Children.Add(limitsText);
        Form.Children.Add(Note(model.Network.FaucetKind == Network.FaucetKindValue.Teztnets
            ? "The faucet asks for a small proof of work before it pays out; Signet solves it for you. Test tez have no value."
            : "The faucet pays out on request. Test tez have no value."));
        Form.Children.Add(status);

        CloseButtonText = "Done";
        Primary("Request", () =>
        {
            // The request runs on; the sheet stays open to show its progress.
            KeepOpen = true;
            if (Amount is { } amount) _ = model.RequestTestTez(amount);
            return Task.CompletedTask;
        });
        SecondaryButtonText = "";
        SecondaryButtonClick += (_, args) => { args.Cancel = true; model.CancelFaucetRequest(); };
        amountText.TextChanged += (_, _) => Validate();
        model.PropertyChanged += Changed;
        Closed += (_, _) => model.PropertyChanged -= Changed;
        Opened += async (_, _) => await LoadLimits();
        Update();
    }

    private double? Amount
    {
        get
        {
            if (!double.TryParse(amountText.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var value) || value <= 0) return null;
            if (limits is { } l && (value < l.Min || value > l.Max)) return null;
            return value;
        }
    }

    private bool IsBusy => model.Faucet is WalletViewModel.FaucetStatus.Solving or WalletViewModel.FaucetStatus.Sent;

    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WalletViewModel.Faucet)) Update();
    }

    protected override void Validate() =>
        IsPrimaryButtonEnabled = Amount is not null && !IsBusy && model.SelectedWallet is not null;

    private async Task LoadLimits()
    {
        if (FaucetService.For(model.Network) is not { } service) return;
        try
        {
            var info = await service.GetInfo();
            limits = (info.MinTez, info.MaxTez);
            limitsText.Text = $"{(int)info.MinTez} to {(int)info.MaxTez}";
            if (WalletViewModel.FaucetAmountTez > info.MaxTez) amountText.Text = ((int)info.MaxTez).ToString(CultureInfo.CurrentCulture);
        }
        catch (Exception) { }
        Validate();
    }

    private void Update()
    {
        status.Children.Clear();
        var secondary = (Style)Application.Current.Resources["SecondaryText"];
        switch (model.Faucet)
        {
            case WalletViewModel.FaucetStatus.Solving solving:
                var progress = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                progress.Children.Add(new ProgressBar { Width = 160, Maximum = Math.Max(solving.Total, 1), Value = solving.Done, VerticalAlignment = VerticalAlignment.Center });
                progress.Children.Add(new TextBlock { Text = $"Solving faucet challenge {Math.Min(solving.Done + 1, solving.Total)} of {solving.Total}…", Style = secondary });
                status.Children.Add(progress);
                break;
            case WalletViewModel.FaucetStatus.Sent sent:
                var waiting = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                waiting.Children.Add(new ProgressRing { Width = 16, Height = 16, IsActive = true });
                waiting.Children.Add(new TextBlock { Text = "Sent, waiting for the next block…", Style = secondary });
                status.Children.Add(waiting);
                status.Children.Add(new TextBlock { Text = sent.Hash, FontSize = 11, Style = (Style)Application.Current.Resources["MonoText"], IsTextSelectionEnabled = true, TextTrimming = TextTrimming.CharacterEllipsis });
                break;
            case WalletViewModel.FaucetStatus.Received received:
                status.Children.Add(Label("Received. Balance updated.", "", "SystemFillColorSuccessBrush"));
                status.Children.Add(new TextBlock { Text = received.Hash, FontSize = 11, Style = (Style)Application.Current.Resources["MonoText"], IsTextSelectionEnabled = true, TextTrimming = TextTrimming.CharacterEllipsis });
                break;
            case WalletViewModel.FaucetStatus.Failed failed:
                status.Children.Add(Label(failed.Message, "", "SystemFillColorCriticalBrush"));
                break;
        }
        // Cancel stops the proof of work; Done closes (the request carries on while it is in flight).
        SecondaryButtonText = model.Faucet is WalletViewModel.FaucetStatus.Solving ? "Cancel" : "";
        CloseButtonText = IsBusy ? "Close" : "Done";
        Validate();
    }
}
