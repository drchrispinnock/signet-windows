using Signet.Services;

namespace Signet.Tests;

/// <summary>Windows-only: the verbose dApp log is armed for exactly one launch (S11).</summary>
public class DeveloperSettingsTests
{
    [Fact]
    public void DAppDiagnosticsAreConsumedByOneLaunch()
    {
        var defaults = UserDefaults.Temporary();
        Assert.False(DeveloperSettings.Consume(defaults));
        defaults.Set(DeveloperSettings.DAppDiagnosticsKey, true);
        Assert.True(DeveloperSettings.Consume(defaults));
        Assert.False(DeveloperSettings.Consume(defaults));
        Assert.Null(defaults.Object(DeveloperSettings.DAppDiagnosticsKey));
    }
}
