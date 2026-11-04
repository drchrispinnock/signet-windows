using Signet.Services;

namespace Signet.Tests;

public class QRCodeTests
{
    /// <summary>
    /// Upstream also scans the image back with Core Image's detector; Windows has no QR decoder in
    /// the box, so this checks QRCoder makes a square PNG close to the asked size (modules are whole
    /// pixels, so it rounds down to a multiple of the module count).
    /// </summary>
    [Fact]
    public void EncodesAnAddressThatScansBack()
    {
        var png = QRCode.Png("tz1dCaMnnMJk76UodjewCK67ABiXWjcKj73N", 240);
        var (width, height) = ImageLoader.PNGSize(png)!.Value;
        Assert.True(width == height && width is > 200 and <= 240, $"{width}x{height}");
    }
}

/// <summary>
/// Upstream suites with nothing to run on Windows, kept by name so the skipped list explains them.
/// </summary>
public class UpdaterTests
{
    private const string Sparkle = "Sparkle's Info.plist keys (SUFeedURL, SUPublicEDKey); Windows updates through Velopack, configured in release.yml";

    [Fact(Skip = Sparkle)] public void FeedURLIsHTTPSOnGitHubReleases() { }
    [Fact(Skip = Sparkle)] public void PublicKeyIs32BytesOfBase64() { }
    [Fact(Skip = Sparkle)] public void AutomaticChecksAreOnByDefault() { }
}

public class TestHostIsolationTests
{
    [Fact(Skip = "Upstream's tests run inside the app; Windows tests are a separate host that never builds the app's model (Signet.Core only)")]
    public void TheTestHostModelTouchesNoRealState() { }
}

public class DAppStartupTests
{
    [Fact(Skip = "Live, and starts from the user's real octez-connect.json (upstream: .realWalletState)")]
    public void StartsFromPersistedState() { }
}

public class WalletLockMeasurementTests
{
    [Fact(Skip = "An opt-in benchmark upstream (SIGNET_LOCK_BENCHMARK=1), not a test")]
    public void MeasureSyntheticWalletInitializationUnderProcessLock() { }
}
