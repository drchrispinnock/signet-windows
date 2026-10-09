using Signet.Models;

namespace Signet.Tests;

public class DAppPairingLinkTests
{
    [Fact]
    public void ParsesTheBeaconDesktopLinkShape()
    {
        // What the dApp's wallet list opens: deepLink + "?type=tzip10&data=" + payload.
        Assert.Equal("3NDiBB3dPSCeiHnjoaHprF", DAppPairingLink.Code(new Uri("signet://?type=tzip10&data=3NDiBB3dPSCeiHnjoaHprF")));
        Assert.Equal("abc", DAppPairingLink.Code(new Uri("signet://pair?data=abc&type=TZIP10")));
        Assert.Equal("a+b=", DAppPairingLink.Code(new Uri("signet://?type=tzip10&data=a%2Bb%3D")));
        Assert.Null(DAppPairingLink.Code(new Uri("signet://?type=other&data=abc")));
        Assert.Null(DAppPairingLink.Code(new Uri("signet://?type=tzip10")));
        Assert.Null(DAppPairingLink.Code(new Uri("https://example.com/?type=tzip10&data=abc")));
        var roundTrip = DAppPairingLink.Url("xyz+1");
        Assert.Equal("signet", roundTrip.Scheme);
        Assert.Equal("xyz+1", DAppPairingLink.Code(roundTrip));
    }

    [Fact(Skip = Pending.ViewModel)]
    public void IncomingLinkOpensTheConnectSheetWithTheCode() { }
}
