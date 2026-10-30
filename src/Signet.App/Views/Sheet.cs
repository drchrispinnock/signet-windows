using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Signet.App.Views;

/// <summary>
/// Windows-only: what every sheet shares. A SwiftUI sheet becomes a ContentDialog whose primary
/// button runs the sheet's action and stays open with the error underneath when it fails, like
/// the Mac sheets that dismiss only on success. <c>Form</c> is the body (upstream's grouped Form).
/// </summary>
public abstract partial class Sheet : ContentDialog
{
    protected readonly StackPanel Form = new() { Spacing = 12 };
    protected readonly TextBlock ErrorLine = new() { Style = (Style)Application.Current.Resources["ErrorText"], Visibility = Visibility.Collapsed };
    protected readonly StackPanel Progress = new() { Orientation = Orientation.Horizontal, Spacing = 8, Visibility = Visibility.Collapsed };
    private readonly TextBlock progressText = new() { Style = (Style)Application.Current.Resources["SecondaryText"] };
    private Func<Task>? primaryAction;

    protected Sheet(string title, double width)
    {
        Title = title;
        DefaultButton = ContentDialogButton.Primary;
        Progress.Children.Add(new ProgressRing { Width = 16, Height = 16, IsActive = true });
        Progress.Children.Add(progressText);
        var body = new StackPanel { Spacing = 16, MaxWidth = width };
        body.Children.Add(Form);
        body.Children.Add(ErrorLine);
        body.Children.Add(Progress);
        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 12, 0) };
        PrimaryButtonClick += async (_, args) =>
        {
            if (primaryAction is null) return;
            var deferral = args.GetDeferral();
            ShowError(null);
            IsPrimaryButtonEnabled = false;
            try
            {
                KeepOpen = false;
                await primaryAction();
                args.Cancel = KeepOpen;
            }
            catch (Exception e)
            {
                args.Cancel = true;
                ShowError(e.Message);
            }
            finally
            {
                IsPrimaryButtonEnabled = true;
                Validate();
                deferral.Complete();
            }
        };
    }

    /// <summary>The primary button: its text and the work it does; the sheet closes when the work succeeds.</summary>
    protected void Primary(string text, Func<Task> action)
    {
        PrimaryButtonText = text;
        primaryAction = action;
    }

    /// <summary>Set by a primary action that moves the sheet to its next step instead of finishing it.</summary>
    protected bool KeepOpen { get; set; }

    protected void ShowError(string? message)
    {
        ErrorLine.Text = message ?? "";
        ErrorLine.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    protected void ShowProgress(string? text)
    {
        progressText.Text = text ?? "";
        Progress.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Recomputes what can be pressed; sheets override it and call it when a field changes.</summary>
    protected virtual void Validate() { }

    // ---- Building blocks -------------------------------------------------------------------------

    /// <summary>
    /// The wallet directory as the user should see it, where upstream writes <c>~/.signet</c>: Windows
    /// has no <c>~</c>, so the full path (<c>C:\Users\name\.signet</c>, or wherever it was moved).
    /// </summary>
    protected static string WalletFolder(ViewModels.WalletViewModel model) =>
        model.WalletDirectory ?? Signet.Services.TezosClientStore.DefaultDirectory;

    protected static TextBlock Note(string text) => new() { Text = text, Style = (Style)Application.Current.Resources["NoteText"] };

    protected static TextBlock Caution(string text) => new() { Text = text, Style = (Style)Application.Current.Resources["CautionText"] };

    /// <summary>An icon and a wrapped line, upstream's <c>Label(text, systemImage:)</c>.</summary>
    protected static Grid Label(string text, string glyph, string brush = "TextFillColorSecondaryBrush")
    {
        var foreground = (Brush)Application.Current.Resources[brush];
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14, Foreground = foreground, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
        var label = new TextBlock { Text = text, Foreground = foreground, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        return grid;
    }

    /// <summary>A title on the left, the value on the right, as upstream's <c>LabeledContent</c>.</summary>
    protected static Grid LabeledContent(string title, UIElement content)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
        if (content is FrameworkElement element) element.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn((FrameworkElement)content, 1);
        grid.Children.Add(content);
        return grid;
    }

    /// <summary>
    /// Upstream's <c>Menu("Choose…") { Section("My accounts") { … } … }</c>: a drop-down button whose
    /// flyout lists accounts under section headings. Each entry is a label, what it does, and
    /// whether it can be picked.
    /// </summary>
    protected static DropDownButton ChooseMenu(string text, params (string Section, IEnumerable<(string Label, Action Pick, bool Enabled)> Items)[] sections)
    {
        var flyout = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight };
        foreach (var (section, items) in sections)
        {
            var list = items.ToList();
            if (list.Count == 0) continue;
            if (flyout.Items.Count > 0) flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(new MenuFlyoutItem { Text = section, IsEnabled = false });
            foreach (var (label, pick, enabled) in list)
            {
                var item = new MenuFlyoutItem { Text = label, IsEnabled = enabled };
                item.Click += (_, _) => pick();
                flyout.Items.Add(item);
            }
        }
        return new DropDownButton { Content = text, Flyout = flyout, IsEnabled = flyout.Items.Count > 0 };
    }

    /// <summary>A line of monospaced text the user can select and copy, with a copy button.</summary>
    protected static Grid CopyableLine(string text, string what)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = text, Style = (Style)Application.Current.Resources["MonoText"], IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        var copy = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 }, Padding = new Thickness(6), VerticalAlignment = VerticalAlignment.Top };
        ToolTipService.SetToolTip(copy, $"Copy {what}");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(copy, $"Copy {what}");
        copy.Click += (_, _) => Services.Pasteboard.SetString(text);
        Grid.SetColumn(copy, 1);
        row.Children.Add(copy);
        return row;
    }

    protected static Brush Secondary => (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

    protected static Button MakeButton(string text, Action action, bool enabled = true)
    {
        var button = new Button { Content = text, IsEnabled = enabled };
        button.Click += (_, _) => action();
        return button;
    }

    protected static StackPanel Row(params Button[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var button in buttons) row.Children.Add(button);
        return row;
    }

    /// <summary>An operation's done step: "Included in block N" and its hash with an explorer link.</summary>
    protected void AddIncludedInBlock(Models.Network network, string hash, int level)
    {
        Form.Children.Add(Label($"Included in block {level.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)}", "", "SystemFillColorSuccessBrush"));
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = hash, Style = (Style)Application.Current.Resources["MonoText"], IsTextSelectionEnabled = true, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        if (network.ExplorerURLForOperation(hash) is { } url)
        {
            var link = new HyperlinkButton { Content = "View in explorer", NavigateUri = url };
            Grid.SetColumn(link, 1);
            row.Children.Add(link);
        }
        Form.Children.Add(row);
    }

    /// <summary>"Signing and waiting" with a spinner; on a Ledger, the prompt to confirm on the device.</summary>
    protected void AddWorkingRow(string title, bool onLedger)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new ProgressRing { Width = 16, Height = 16, IsActive = true });
        row.Children.Add(new TextBlock
        {
            Text = onLedger ? $"{title}: confirm on your Ledger, then we wait for the next block…" : $"{title}… waiting for the next block",
            Foreground = Secondary, TextWrapping = TextWrapping.Wrap,
        });
        Form.Children.Add(row);
    }

    /// <summary>A text field made fresh for one step and bound to a string the sheet keeps across steps.</summary>
    protected static TextBox BoundTextBox(Func<string> get, Action<string> set, string placeholder = "", bool monospaced = false, Action? changed = null)
    {
        var box = new TextBox { Text = get(), PlaceholderText = placeholder, IsSpellCheckEnabled = false };
        if (monospaced) box.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        box.TextChanged += (_, _) => { set(box.Text); changed?.Invoke(); };
        return box;
    }

    /// <summary>A password field made fresh for one step and bound to a string the sheet keeps.</summary>
    protected static PasswordBox BoundPasswordBox(Func<string> get, Action<string> set, Action? changed = null)
    {
        var box = new PasswordBox { Password = get() };
        box.PasswordChanged += (_, _) => { set(box.Password); changed?.Invoke(); };
        return box;
    }

    /// <summary>Upstream's <c>Label(…, systemImage: "checkmark.seal.fill").foregroundStyle(.green)</c>.</summary>
    protected static Grid Success(string text) => Label(text, "", "SystemFillColorSuccessBrush");

    /// <summary>A number of tez typed by the user (comma or point), or <c>null</c> when it is not one.</summary>
    protected static decimal? ParseTez(string text) =>
        decimal.TryParse(text.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>The avatar, the sheet's own title and the account under it, as Forget and Export show them.</summary>
    protected static StackPanel AccountHeading(Models.Wallet wallet)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(new AccountAvatarView(wallet.Address, 40));
        row.Children.Add(new TextBlock { Text = $"{wallet.Alias}  {wallet.Address.Shortened()}", Style = (Style)Application.Current.Resources["SecondaryText"], VerticalAlignment = VerticalAlignment.Center });
        return row;
    }
}
