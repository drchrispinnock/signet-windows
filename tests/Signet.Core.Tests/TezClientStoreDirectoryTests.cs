using Signet.Services;

namespace Signet.Tests;

public class WalletDirectoryTests
{
    [Fact(Skip = Pending.ViewModel)]
    public void SwitchingDirectoriesSwapsWalletsAndRestoresThatDirectorysSelection() { }

    [Fact]
    public void DefaultDirectoryClearsTheOverride()
    {
        var defaults = UserDefaults.Temporary();
        var settings = new WalletDirectorySettings(defaults);
        Assert.Equal(settings.DefaultDirectory, settings.Current);

        var elsewhere = Path.Combine(Path.GetTempPath(), "elsewhere");
        settings.Set(elsewhere);
        Assert.True(PathUtility.SamePath(elsewhere, settings.Current));
        settings.Set(settings.DefaultDirectory);
        Assert.Null(defaults.String(WalletDirectorySettings.Key));
        Assert.Equal(settings.DefaultDirectory, settings.Current);
    }
}
