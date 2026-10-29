using Microsoft.UI.Xaml;
using Signet.Services;

namespace Signet.App.Services;

/// <summary>
/// Upstream's <c>appliesStoredAppearance()</c>: the stored <c>Appearance</c> applied to the window's
/// content and to every dialog (each ContentDialog is its own popup and takes its theme from itself).
/// </summary>
public static class AppearanceApplier
{
    public static ElementTheme Theme(Appearance appearance) => appearance switch
    {
        Appearance.Light => ElementTheme.Light,
        Appearance.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    public static ElementTheme Current => Theme(AppearanceExtensions.Stored());

    /// <summary>Stores <c>appearance</c> and applies it to the window behind <c>root</c>.</summary>
    public static void Set(Appearance appearance, XamlRoot? root)
    {
        UserDefaults.Standard.Set(AppearanceExtensions.Key, appearance.RawValue());
        if (root?.Content is FrameworkElement content) content.RequestedTheme = Theme(appearance);
    }
}
