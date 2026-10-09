using Signet.Models;
using Signet.Services;

namespace Signet.Tests;

/// <summary>
/// Exercises the V8 host. The first two tests are offline; the rest hit Shadownet and are tagged
/// so they can be excluded with <c>--filter-not-trait "Category=Network"</c> when offline.
/// </summary>
public class TaquitoBridgeTests
{
    [Fact]
    public async Task BundleLoadsAndReportsVersion()
    {
        var version = await TaquitoBridge.Shared.Call("version");
        Assert.Equal(new JSONValue.String("0.15.0"), version);
    }

    [Theory]
    [InlineData("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb", true)]
    [InlineData("tz2BFTyPeYRzxd5aiBchbXN3WCZhx7BqbMBq", true)]
    [InlineData("tz4HVR6aty9KwsQFHh81C1G7gBdhxT8kuytm", true)]
    [InlineData("KT1BRd2ka5q2cPRdXALtXD1QZ38CPam2j1ye", true)]
    [InlineData("tz1junk", false)]
    [InlineData("", false)]
    public async Task ValidatesAddresses(string address, bool expected)
    {
        var valid = await TaquitoBridge.Shared.Call("isValidAddress", address);
        Assert.Equal(new JSONValue.Bool(expected), valid);
    }

    [Fact(Timeout = 60_000), Trait(Category.Name, Category.Network)]
    public async Task FetchesHeadFromShadownet()
    {
        var head = await TaquitoBridge.Shared.Call("getHead", Network.Shadownet.RpcURL.AbsoluteString())
            .WaitAsync(TestContext.Current.CancellationToken);
        var level = head["level"]?.DoubleValue;
        Assert.NotNull(level);
        Assert.True(level > 0);
        Assert.StartsWith("B", head["hash"]?.StringValue);
    }

    [Fact(Skip = Pending.ChainService), Trait(Category.Name, Category.Network)]
    public void FetchesBalanceThroughChainService() { }

    // Not upstream: failures must come back as exceptions, not hang the caller.
    [Fact]
    public async Task UnknownFunctionsAndRejectionsSurfaceAsErrors()
    {
        var missing = await Assert.ThrowsAsync<TaquitoBridge.BridgeError.JavaScript>(() => TaquitoBridge.Shared.Call("noSuchFunction"));
        Assert.Equal("TaquitoBridge.noSuchFunction is not a function", missing.Message);
        await Assert.ThrowsAnyAsync<TaquitoBridge.BridgeError>(() => TaquitoBridge.Shared.Call("decryptSecretKey", "edeskNotAKey", "pw"));
    }
}

public class BridgeConsoleTests
{
    [Fact]
    public async Task JavascriptConsoleReachesTheHostLog()
    {
        var result = await TaquitoBridge.Shared.Call("bridgeEcho", $"ping-{Random.Shared.Next(1000, 10000)}");
        var echoed = result["echoed"]?.StringValue ?? "";
        var lines = TaquitoBridge.Shared.ConsoleLines;
        Assert.True(lines.Contains($"[log] echo: {echoed}"),
            $"console types: {result}\nlast lines:\n{string.Join("\n", lines.TakeLast(8))}");
        Assert.Contains($"[warn] echo-warn: {echoed}", lines);
    }
}

public class OctezConnectCryptoTests
{
    /// <summary>The SDK swallows decryption errors; this exercises its sealed-box and session-key paths in V8.</summary>
    [Fact]
    public async Task SdkCryptoRoundTripsInsideTheBridge()
    {
        var r = await TaquitoBridge.Shared.Call("octezConnectCryptoSelfTest");
        Assert.True(r["sealedRoundTrip"] == new JSONValue.Bool(true), r.ToString());
        Assert.True(r["sessionRoundTrip"] == new JSONValue.Bool(true), r.ToString());
    }
}
