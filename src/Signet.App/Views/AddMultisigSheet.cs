using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Signet.Models;
using Signet.ViewModels;

namespace Signet.App.Views;

/// <summary>
/// Records an existing multisig by address, once the chain confirms it is the generic multisig
/// and one of our accounts is a signer.
/// </summary>
public sealed partial class AddMultisigSheet : Sheet
{
    private readonly WalletViewModel model;
    private readonly TextBox alias = new() { PlaceholderText = "e.g. treasury" };
    private readonly TextBox address = new() { PlaceholderText = "KT1…", FontFamily = new FontFamily("Cascadia Mono, Consolas"), IsSpellCheckEnabled = false };
    private readonly StackPanel contract = new() { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right };
    private MultisigInfo? info;
    private string? lookupError;
    private int lookupVersion;

    public AddMultisigSheet(WalletViewModel model) : base("Add multisig", 520)
    {
        this.model = model;
        Form.Children.Add(new TextBlock { Text = "Name" });
        Form.Children.Add(alias);
        Form.Children.Add(new TextBlock { Text = "Contract address" });
        Form.Children.Add(address);
        Form.Children.Add(LabeledContent("Contract", contract));
        Form.Children.Add(Note("Only multisigs that one of your accounts can sign for can be added."));

        CloseButtonText = "Cancel";
        Primary("Add", async () => await model.AddMultisig(alias.Text, address.Text));
        alias.TextChanged += (_, _) => Validate();
        address.TextChanged += async (_, _) => await LookUp();
        DrawContract();
        Validate();
    }

    private Address TrimmedAddress => new(address.Text.Trim());
    private bool AddressLooksValid => TrimmedAddress.IsContract && TrimmedAddress.IsValidAccount;
    private IReadOnlyList<Wallet> Members => info?.Members(model.Wallets) ?? [];

    protected override void Validate() =>
        IsPrimaryButtonEnabled = alias.Text.Trim().Length > 0 && info?.IsGenericMultisig == true && Members.Count > 0;

    /// <summary>Reads the contract a moment after typing stops (upstream's <c>.task(id: address)</c> with a 300 ms pause).</summary>
    private async Task LookUp()
    {
        var version = ++lookupVersion;
        info = null;
        lookupError = null;
        DrawContract();
        Validate();
        if (!AddressLooksValid) return;
        await Task.Delay(300);
        if (version != lookupVersion) return;
        try
        {
            var result = await model.MultisigInfo(new MultisigContract("", TrimmedAddress));
            if (version != lookupVersion) return;
            info = result;
        }
        catch (Exception e)
        {
            if (version != lookupVersion) return;
            lookupError = e.Message;
        }
        DrawContract();
        Validate();
    }

    private void DrawContract()
    {
        contract.Children.Clear();
        TextBlock Line(string text, string style = "SecondaryText") => new() { Text = text, Style = (Style)Application.Current.Resources[style], TextWrapping = TextWrapping.Wrap };
        if (info is { } i)
        {
            if (!i.IsGenericMultisig)
            {
                contract.Children.Add(Label("Not an octez-client multisig", "", "SystemFillColorCautionBrush"));
                return;
            }
            contract.Children.Add(new TextBlock { Text = $"{i.Threshold} of {i.Keys.Count} signatures · {AssetBalance.Format(i.Balance, "tz")}" });
            foreach (var key in i.Keys)
            {
                if (model.Wallets.FirstOrDefault(w => w.PublicKey == key) is { } mine)
                    contract.Children.Add(Label($"{mine.Alias} (yours)", "", "TextFillColorPrimaryBrush"));
                else
                    contract.Children.Add(Line(key[..Math.Min(14, key.Length)] + "…", "MonoText"));
            }
            if (Members.Count == 0) contract.Children.Add(Label("None of your accounts is a signer", "", "SystemFillColorCautionBrush"));
        }
        else if (lookupError is not null)
        {
            contract.Children.Add(Line(lookupError));
        }
        else if (AddressLooksValid)
        {
            contract.Children.Add(new ProgressRing { Width = 16, Height = 16, IsActive = true });
        }
        else
        {
            var hint = Line(address.Text.Length == 0 ? "Paste the multisig's address" : "Not a contract address");
            hint.Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
            contract.Children.Add(hint);
        }
    }
}
