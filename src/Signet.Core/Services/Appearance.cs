namespace Signet.Services;

/// <summary>Light, dark, or follow the OS. Stored in the app's preferences.</summary>
public enum Appearance { System, Light, Dark }

public static class AppearanceExtensions
{
    public const string Key = "appearance";

    public static IReadOnlyList<Appearance> AllCases { get; } = Enum.GetValues<Appearance>();

    public static string RawValue(this Appearance appearance) => appearance.ToString().ToLowerInvariant();

    public static Appearance FromRawValue(string? raw) =>
        AllCases.FirstOrDefault(a => a.RawValue() == raw, Appearance.System);

    public static string Title(this Appearance appearance) => appearance switch
    {
        Appearance.System => "OS",
        Appearance.Light => "Light",
        _ => "Dark",
    };

    /// <summary>The stored choice, <c>System</c> when there is none.</summary>
    public static Appearance Stored(UserDefaults? defaults = null) => FromRawValue((defaults ?? UserDefaults.Standard).String(Key));
}
