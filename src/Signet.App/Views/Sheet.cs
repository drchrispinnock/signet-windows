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

    /// <summary>The avatar, the sheet's own title and the account under it, as Forget and Export show them.</summary>
    protected static StackPanel AccountHeading(Models.Wallet wallet)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(new AccountAvatarView(wallet.Address, 40));
        row.Children.Add(new TextBlock { Text = $"{wallet.Alias}  {wallet.Address.Shortened()}", Style = (Style)Application.Current.Resources["SecondaryText"], VerticalAlignment = VerticalAlignment.Center });
        return row;
    }
}
