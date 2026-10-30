using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Signet.Models;
using Signet.Services;
using Signet.ViewModels;

namespace Signet.App.Views;

/// <summary>
/// Buy tez through an on-ramp picked from the dropdown at the top of the sheet (the choice is
/// remembered). With a widget provider Signet pre-fills (and, when it can sign, pre-validates)
/// the account's address, then shows the provider's widget right here; the purchase, identity
/// checks and payment are all the provider's, and the tez land in the account. With an exchange
/// (Coinbase, Kraken) or Transak's site it shows the address to paste and opens the page in the
/// browser.
/// </summary>
public sealed partial class BuySheet : Sheet
{
    private abstract record Step
    {
        public sealed record Prepare : Step;
        public sealed record Widget(Uri Url) : Step;
    }

    private readonly WalletViewModel model;
    private readonly Wallet? wallet;
    private BuyProvider provider = BuyProviderExtensions.Current;
    private Step step = new Step.Prepare();
    private string fiat;
    private string passphrase = "";
    private bool isWorking;
    /// <summary>What the embedded page is showing, so the user can see whose site they are on.</summary>
    private Uri? currentURL;
    private TextBlock? hostText;
    private FontIcon? lockIcon;
    private ProgressRing? loadingRing;
    private TextBlock? loadErrorText;

    public BuySheet(WalletViewModel model) : base("Buy tez", 560)
    {
        this.model = model;
        wallet = model.SelectedWallet;
        fiat = provider.DefaultFiat();
        PrimaryButtonClick += async (_, args) =>
        {
            if (step is Step.Widget && !isWorking) return;   // Done closes the widget step
            args.Cancel = true;
            if (isWorking) return;
            var deferral = args.GetDeferral();
            try
            {
                if (!provider.UsesWidget())
                {
                    if (provider.BrowserPage() is { } page) await Windows.System.Launcher.LaunchUriAsync(page);
                }
                else
                {
                    await Start();
                }
            }
            finally { deferral.Complete(); }
        };
        SecondaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            // The same session as a direct link; the signed proof is reused so nothing is asked twice.
            if (step is Step.Widget widget) await Windows.System.Launcher.LaunchUriAsync(provider.BrowserURL(widget.Url));
        };
        // A message being signed (on a Ledger, perhaps) must not be abandoned with Esc.
        Closing += (_, args) => { if (isWorking) args.Cancel = true; };
        Render();
    }

    private bool CanSign => wallet?.KeyKind.CanSign() == true;
    private bool NeedsPassphrase => wallet?.KeyKind == KeyKind.Encrypted;
    private bool SignsOnLedger => wallet?.KeyKind == KeyKind.Ledger;

    private void Render()
    {
        Form.Children.Clear();
        hostText = null;
        lockIcon = null;
        loadingRing = null;
        loadErrorText = null;
        if (wallet is not null) Form.Children.Add(AccountHeading(wallet));
        if (!provider.UsesWidget()) Exchange();
        else if (step is Step.Widget widget) WidgetStep(widget.Url);
        else PrepareStep();

        (PrimaryButtonText, SecondaryButtonText, CloseButtonText) = provider.UsesWidget()
            ? step is Step.Widget ? ("Done", "Open in browser instead", "") : ("Continue", "", "Cancel")
            : ($"Open {provider.Title()}", "", "Done");
        DefaultButton = ContentDialogButton.Primary;
        PrimaryButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"];
        Validate();
    }

    protected override void Validate() =>
        IsPrimaryButtonEnabled = !isWorking && !(provider.UsesWidget() && step is Step.Prepare && NeedsPassphrase && passphrase.Length == 0);

    /// <summary>The dropdown that switches providers; the choice is kept for next time.</summary>
    private void ProviderPicker()
    {
        var providers = Enum.GetValues<BuyProvider>();
        var picker = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var p in providers) picker.Items.Add(p.Title());
        picker.SelectedIndex = Array.IndexOf(providers, provider);
        // Only a choice the user makes in the open drop-down counts; a selection event raised while
        // the sheet redraws must neither switch providers nor touch the saved preference.
        var picking = false;
        picker.DropDownOpened += (_, _) => picking = true;
        picker.SelectionChanged += (_, _) =>
        {
            if (!picking || picker.SelectedIndex < 0 || providers[picker.SelectedIndex] == provider) return;
            provider = providers[picker.SelectedIndex];
            UserDefaults.Standard.Set(BuyProviderExtensions.Key, provider.RawValue());
            // A new provider starts over: its own currencies, no widget, no stale error.
            step = new Step.Prepare();
            fiat = provider.DefaultFiat();
            currentURL = null;
            ShowError(null);
            // Redraw once the drop-down has closed, not from inside its own event.
            DispatcherQueue.TryEnqueue(Render);
        };
        Form.Children.Add(new TextBlock { Text = "Provider" });
        Form.Children.Add(picker);
    }

    // ---- Exchange (no widget) ------------------------------------------------------------------

    /// <summary>The address to paste as the withdrawal destination, and the exchange's page in the browser.</summary>
    private void Exchange()
    {
        ProviderPicker();
        if (wallet is not null)
        {
            Form.Children.Add(new TextBlock { Text = "Send to" });
            Form.Children.Add(CopyableLine(wallet.Address.Value, "address"));
            var bitmap = new BitmapImage();
            using (var stream = new MemoryStream(QRCode.Png(wallet.Address.Value, 160)))
                bitmap.SetSource(stream.AsRandomAccessStream());
            Form.Children.Add(new Border
            {
                Child = new Image { Source = bitmap, Width = 160, Height = 160 },
                Background = new SolidColorBrush(Microsoft.UI.Colors.White), Padding = new Thickness(8), CornerRadius = new CornerRadius(8),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        }
        var steps = new StackPanel { Spacing = 6 };
        steps.Children.Add(Note($"1. Open {provider.Title()} and buy tez (XTZ)."));
        steps.Children.Add(Note($"2. {provider.DeliveryStep()}"));
        steps.Children.Add(Note("3. The balance here refreshes once the transfer is in a block."));
        Form.Children.Add(steps);
        Form.Children.Add(Note($"{provider.Summary()} Send tez only; tokens on other networks cannot reach a Tezos address."));
    }

    // ---- Step 1 ----------------------------------------------------------------------------------

    private void PrepareStep()
    {
        ProviderPicker();
        var currencies = provider.FiatCurrencies();
        var pay = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var currency in currencies) pay.Items.Add(currency);
        pay.SelectedIndex = Math.Max(0, currencies.ToList().IndexOf(fiat));
        pay.SelectionChanged += (_, _) => { if (pay.SelectedIndex >= 0) fiat = currencies[pay.SelectedIndex]; };
        Form.Children.Add(new TextBlock { Text = "Pay in" });
        Form.Children.Add(pay);
        if (wallet is not null)
            Form.Children.Add(LabeledContent("Deliver to", new TextBlock { Text = wallet.Address.Value, Style = (Style)Application.Current.Resources["MonoText"], IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap }));
        if (NeedsPassphrase && wallet is not null)
        {
            Form.Children.Add(new TextBlock { Text = $"Password for “{wallet.Alias}”" });
            Form.Children.Add(BoundPasswordBox(() => passphrase, value => passphrase = value, Validate));
            Form.Children.Add(Note($"Signet signs a short message with this key so {provider.Title()} knows the address is yours."));
        }
        else if (SignsOnLedger)
        {
            Form.Children.Add(Note("Your Ledger will ask you to sign a short message proving the address is yours."));
        }
        else if (!CanSign)
        {
            Form.Children.Add(Note($"Signet holds no key for this address, so {provider.Title()} will ask you to confirm it in the widget."));
        }
        Form.Children.Add(Note($"{provider.Summary()} Everything in the next step runs on {provider.Title()}'s site, shown inside Signet."));
    }

    private async Task Start()
    {
        ShowError(null);
        isWorking = true;
        Validate();
        ShowProgress(SignsOnLedger ? "Sign the message on your Ledger…" : "Preparing…");
        try
        {
            var url = await model.BuyURL(provider, fiat, NeedsPassphrase ? passphrase : null, embedded: true, dark: ActualTheme == ElementTheme.Dark);
            step = new Step.Widget(url);
        }
        catch (Exception e)
        {
            ShowError(e.Message);
        }
        finally
        {
            isWorking = false;
            ShowProgress(null);
        }
        Render();
    }

    // ---- Step 2 ----------------------------------------------------------------------------------

    private void WidgetStep(Uri url)
    {
        var origin = new Grid { ColumnSpacing = 6 };
        origin.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        origin.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        origin.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        origin.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        lockIcon = new FontIcon { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        hostText = new TextBlock { Style = (Style)Application.Current.Resources["MonoText"], Foreground = Secondary, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        loadingRing = new ProgressRing { Width = 14, Height = 14, IsActive = false, VerticalAlignment = VerticalAlignment.Center };
        var who = new TextBlock { Text = provider.Title(), Foreground = Secondary, VerticalAlignment = VerticalAlignment.Center };
        origin.Children.Add(lockIcon);
        Grid.SetColumn(hostText, 1);
        origin.Children.Add(hostText);
        Grid.SetColumn(loadingRing, 2);
        origin.Children.Add(loadingRing);
        Grid.SetColumn(who, 3);
        origin.Children.Add(who);
        ToolTipService.SetToolTip(origin, $"Only {provider.Title()}'s own HTTPS pages can show here; other sites open in your browser.");
        Form.Children.Add(origin);
        ShowOrigin(url);

        var web = new BuyWebView(url, provider) { Height = 560 };
        web.CurrentURLChanged += current => { currentURL = current; ShowOrigin(url); };
        web.LoadingChanged += loading => { if (loadingRing is not null) loadingRing.IsActive = loading; };
        web.LoadErrorChanged += error =>
        {
            if (loadErrorText is null) return;
            loadErrorText.Text = error is null ? "" : $"The widget could not load: {error}";
            loadErrorText.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        };
        Form.Children.Add(new Border
        {
            Child = web, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
        });
        loadErrorText = new TextBlock { Style = (Style)Application.Current.Resources["CautionText"], TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        Form.Children.Add(loadErrorText);
        Form.Children.Add(new TextBlock { Text = "When the purchase is done the balance refreshes on the next block.", Style = (Style)Application.Current.Resources["CaptionSecondaryText"] });
    }

    /// <summary>The host the page is on, with a closed lock only when it is one of the provider's own HTTPS pages.</summary>
    private void ShowOrigin(Uri url)
    {
        if (hostText is null || lockIcon is null) return;
        var shown = currentURL ?? url;
        var trusted = BuyWebViewPolicy.IsTrusted(shown, provider);
        hostText.Text = shown.Host;
        lockIcon.Glyph = trusted ? "" : "";
        lockIcon.Foreground = trusted ? Secondary : (Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
    }
}
