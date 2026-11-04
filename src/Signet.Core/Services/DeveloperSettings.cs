namespace Signet.Services;

/// <summary>
/// Developer switches that must never stay on by accident (S11). Verbose dApp diagnostics make the
/// SDK log decrypted relay traffic, so the switch is armed for one launch only: Settings sets it,
/// the next launch consumes it, and it is off again after that.
/// </summary>
public static class DeveloperSettings
{
    public const string DAppDiagnosticsKey = "dappDiagnosticsNextLaunch";

    /// <summary>Read once at launch (the first time this is asked), and cleared so it does not outlive this run.</summary>
    public static bool DAppDiagnostics => dappDiagnostics.Value;

    private static readonly Lazy<bool> dappDiagnostics = new(() => Consume(UserDefaults.Standard));

    /// <summary>Whether the switch was armed in <c>defaults</c>; disarms it.</summary>
    public static bool Consume(UserDefaults defaults)
    {
        var armed = defaults.Bool(DAppDiagnosticsKey);
        if (armed) defaults.RemoveObject(DAppDiagnosticsKey);
        return armed;
    }

    public static bool DAppDiagnosticsArmed
    {
        get => UserDefaults.Standard.Bool(DAppDiagnosticsKey);
        set
        {
            if (value) UserDefaults.Standard.Set(DAppDiagnosticsKey, true);
            else UserDefaults.Standard.RemoveObject(DAppDiagnosticsKey);
        }
    }
}
