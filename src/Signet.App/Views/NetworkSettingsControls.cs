using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Signet.Models;

namespace Signet.App.Views;

/// <summary>
/// A section title with an ⓘ that shows the explanation on hover, instead of a paragraph
/// under the section.
/// </summary>
public sealed partial class SectionHeader : UserControl
{
    private readonly TextBlock title = new() { Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };
    private readonly FontIcon info = new() { Glyph = "", FontSize = 13, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };

    public SectionHeader(string text, string help = "")
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
        row.Children.Add(title);
        // The icon needs a hit-testable background for its tooltip.
        row.Children.Add(new Border { Child = info, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), VerticalAlignment = VerticalAlignment.Center });
        title.Text = text;
        AutomationProperties.SetName(info, $"About {text}");
        Help = help;
        Content = row;
    }

    public string Help
    {
        set
        {
            ToolTipService.SetToolTip((FrameworkElement)info.Parent ?? info, new ToolTip { Content = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, MaxWidth = 400 } });
            info.Visibility = value.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}

/// <summary>One URL field with its error line and Use Default / Apply buttons.</summary>
public sealed partial class LinkField : UserControl
{
    private readonly TextBox text;
    private readonly TextBlock error = new() { Style = (Style)Application.Current.Resources["ErrorText"], Visibility = Visibility.Collapsed };
    private readonly Button useDefault = new() { Content = "Use Default" };
    private readonly Button apply = new() { Content = "Apply" };
    private readonly Func<Uri?> current;
    private readonly Uri? defaultURL;
    /// <summary>Applies the text; false when it was refused.</summary>
    private readonly Func<string, bool> applyText;

    public LinkField(string title, Func<Uri?> current, Uri? defaultURL, Func<string, bool> apply, Action useDefault)
    {
        this.current = current;
        this.defaultURL = defaultURL;
        applyText = apply;
        text = new TextBox { PlaceholderText = "https://…", FontFamily = new FontFamily("Cascadia Mono, Consolas"), IsSpellCheckEnabled = false };
        text.TextChanged += (_, _) => Update();
        text.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;
            e.Handled = true;
            Submit();
        };
        this.useDefault.Click += (_, _) => { useDefault(); Sync(); };
        this.apply.Click += (_, _) => Submit();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(this.useDefault);
        buttons.Children.Add(this.apply);
        var stack = new StackPanel { Spacing = 8 };
        // A label of its own: a TextBox header would take the field's monospaced font.
        stack.Children.Add(new TextBlock { Text = title });
        stack.Children.Add(text);
        stack.Children.Add(error);
        stack.Children.Add(buttons);
        Content = stack;
        Sync();
    }

    private Uri? Typed => text.Text.Trim() is { Length: > 0 } trimmed ? Network.NodeURL(trimmed) : null;

    private bool IsDirty => text.Text.Trim().Length == 0 ? current() is not null : Typed != current();

    private void Update()
    {
        apply.IsEnabled = IsDirty;
        useDefault.IsEnabled = defaultURL is not null && current() != defaultURL;
    }

    private void Sync()
    {
        text.Text = current() is { } url ? Network.NodeDisplayTextStandalone(url) : "";
        error.Visibility = Visibility.Collapsed;
        Update();
    }

    private void Submit()
    {
        if (applyText(text.Text))
        {
            Sync();
        }
        else
        {
            error.Text = Network.NodeURLProblem(text.Text);
            error.Visibility = Visibility.Visible;
        }
    }
}
