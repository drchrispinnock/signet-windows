using Signet.Models;

namespace Signet.Tests;

public class NodeMonitorTests
{
    private const string Reason = "Pending port: NodeMonitor";

    [Fact(Skip = Reason)] public void FreshHeadIsGreen() { }
    [Fact(Skip = Reason)] public void StaleHeadSlowReplyAndBadPayloadAreYellow() { }
    [Fact(Skip = Reason)] public void ConnectionFailureIsRed() { }
    [Fact(Skip = Reason)] public void MonitorPublishesProbeResultsAndFollowsNetworkChanges() { }
    [Fact(Skip = Reason), Trait(Category.Name, Category.Network)] public void MainnetIsReachable() { }
}

public class FaucetTests
{
    [Fact]
    public void TestnetsHaveFaucetsAndMainnetDoesNot()
    {
        Assert.Null(Network.Mainnet.FaucetURL);
        foreach (var network in Network.All.Where(n => !n.IsMainnet && n.Chain != "custom"))
            Assert.True(network.FaucetURL?.Host.Contains("faucet") == true, network.Name);
    }
}

public class NodeTextTests
{
    [Fact]
    public void BareHostsBecomeHTTPS()
    {
        Assert.Equal("https://rpc.tzbeta.net", Network.NodeURL("rpc.tzbeta.net")?.AbsoluteString());
        Assert.Equal("https://rpc.tzbeta.net", Network.NodeURL(" rpc.tzbeta.net/ ")?.AbsoluteString());
        Assert.Equal("rpc.shadownet.teztnets.com", Network.NodeURL("https://rpc.shadownet.teztnets.com")?.Host);
        Assert.Equal("http://localhost:8732", Network.NodeURL("http://localhost:8732")?.AbsoluteString());
        Assert.Equal("https://node.example.org:8732/tezos", Network.NodeURL("node.example.org:8732/tezos")?.AbsoluteString());
        Assert.Null(Network.NodeURL(""));
        Assert.Null(Network.NodeURL("not a host"));
    }

    [Fact]
    public void DisplayTextIsTheFullURL()
    {
        Assert.Equal("https://rpc.tzbeta.net", Network.Mainnet.NodeDisplayText);
        Assert.Equal("http://localhost:8732", Network.Custom.NodeDisplayText);
        Assert.Equal("http://localhost:8732", Network.Mainnet.UsingNode(new Uri("http://localhost:8732")).NodeDisplayText);
    }
}

public class PerNetworkNodeTests
{
    [Fact(Skip = Pending.ViewModel)] public void EditsAnotherNetworksNodeWithoutSwitching() { }
}

public class NewHeadRefreshTests
{
    [Fact(Skip = Pending.ViewModel)] public void MonitorReportsNewHeadsAfterTheFirst() { }
}
