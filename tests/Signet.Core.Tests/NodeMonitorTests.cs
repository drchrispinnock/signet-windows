using System.Globalization;
using System.Text;
using Signet.Models;
using Signet.Services;

namespace Signet.Tests;

public class NodeMonitorTests
{
    private static byte[] Header(int level, double ageSeconds, DateTimeOffset now)
    {
        var stamp = now.AddSeconds(-ageSeconds).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        return Encoding.UTF8.GetBytes($"{{\"level\": {level}, \"timestamp\": \"{stamp}\", \"hash\": \"BL1\"}}");
    }

    private static NodeStatus Evaluate(byte[] data, int status, double latency, DateTimeOffset now) =>
        NodeMonitor.Evaluate(new NodeMonitor.ProbeReply(data, status, latency), null, now);

    [Fact]
    public void FreshHeadIsGreen()
    {
        var now = DateTimeOffset.UtcNow;
        var status = Evaluate(Header(100, 12, now), 200, 0.3, now);
        var healthy = Assert.IsType<NodeStatus.Healthy>(status);
        Assert.Equal(100, healthy.BlockLevel);
        Assert.True(Math.Abs(healthy.HeadAge - 12) < 1);
        Assert.Equal(0.3, healthy.Latency);
        Assert.Equal(NodeStatus.LightColor.Green, status.Light);
    }

    [Fact]
    public void StaleHeadSlowReplyAndBadPayloadAreYellow()
    {
        var now = DateTimeOffset.UtcNow;
        var stale = Evaluate(Header(5, 600, now), 200, 0.2, now);
        Assert.Equal(NodeStatus.LightColor.Yellow, stale.Light);
        Assert.Equal(5, stale.Level);
        Assert.Contains("behind", stale.Summary);

        var slow = Evaluate(Header(7, 5, now), 200, 6.0, now);
        Assert.Equal(NodeStatus.LightColor.Yellow, slow.Light);
        Assert.Contains("slow", slow.Summary);

        var http = Evaluate([], 503, 0.1, now);
        Assert.Equal(NodeStatus.LightColor.Yellow, http.Light);
        Assert.Contains("503", http.Summary);

        var junk = Evaluate(Encoding.UTF8.GetBytes("<html>"), 200, 0.1, now);
        Assert.Equal(NodeStatus.LightColor.Yellow, junk.Light);
    }

    [Fact]
    public void ConnectionFailureIsRed()
    {
        var status = NodeMonitor.Evaluate(null, new HttpRequestException("No such host is known."), DateTimeOffset.UtcNow);
        Assert.Equal(NodeStatus.LightColor.Red, status.Light);
        Assert.Equal("Node down", status.Summary);
        Assert.StartsWith("Cannot reach node", status.Detail);
    }

    [Fact]
    public async Task MonitorPublishesProbeResultsAndFollowsNetworkChanges()
    {
        var monitor = new NodeMonitor(Network.Mainnet, url =>
        {
            if (url.Host == Network.Mainnet.RpcURL.Host)
            {
                var stamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
                return Task.FromResult(new NodeMonitor.ProbeReply(Encoding.UTF8.GetBytes($"{{\"level\": 1, \"timestamp\": \"{stamp}\"}}"), 200, 0.1));
            }
            throw new TimeoutException("The request timed out.");
        });
        await monitor.CheckNow();
        Assert.Equal(NodeStatus.LightColor.Green, monitor.Status.Light);

        monitor.Network = Network.Shadownet;
        Assert.Equal(new NodeStatus.Unknown(), monitor.Status);
        await monitor.CheckNow();
        Assert.Equal(NodeStatus.LightColor.Red, monitor.Status.Light);
        monitor.Stop();
    }

    [Fact, Trait(Category.Name, Category.Network)]
    public async Task MainnetIsReachable()
    {
        var reply = await NodeMonitor.HttpProbe(Network.Mainnet.RpcURL.AppendingPathComponent("chains/main/blocks/head/header"));
        var status = NodeMonitor.Evaluate(reply, null, DateTimeOffset.UtcNow);
        Assert.NotEqual(NodeStatus.LightColor.Red, status.Light);
        Assert.True((status.Level ?? 0) > 0);
    }
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
    [Fact]
    public void EditsAnotherNetworksNodeWithoutSwitching()
    {
        var state = new InMemoryAppStateStore();
        var model = new Signet.ViewModels.WalletViewModel(Signet.ViewModels.WalletViewModel.SampleWallets, new MockChainService(), stateStore: state);
        Assert.Equal("Mainnet", model.Network.Name);

        Assert.True(model.SetNode("rpc.shadownet.example.org", Network.Shadownet));
        Assert.Equal("Mainnet", model.Network.Name);                       // still on mainnet
        Assert.Equal("rpc.shadownet.example.org", model.NodeURL(Network.Shadownet).Host);
        Assert.Equal("https://rpc.shadownet.example.org", state.Load().NodeURLs?["Shadownet"]);

        model.SwitchNetwork(Network.Shadownet);                              // the saved node comes along
        Assert.Equal("rpc.shadownet.example.org", model.Network.RpcURL.Host);

        model.UseDefaultNode(Network.Shadownet);
        Assert.Equal(Network.Shadownet.DefaultRPCURL, model.Network.RpcURL);
        Assert.Null(state.Load().NodeURLs?.GetValueOrDefault("Shadownet"));

        Assert.Contains(Network.All, n => n.Name == "Custom");
        Assert.True(model.SetNode("node.mine.example:8732", Network.Custom));
        Assert.Equal("https://node.mine.example:8732", model.NodeURL(Network.Custom).AbsoluteString());
        Assert.False(model.SetNode("not a host", Network.Custom));
    }
}

public class NewHeadRefreshTests
{
    [Fact]
    public async Task MonitorReportsNewHeadsAfterTheFirst()
    {
        var level = 100;
        var monitor = new NodeMonitor(Network.Mainnet, _ =>
        {
            var stamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
            return Task.FromResult(new NodeMonitor.ProbeReply(Encoding.UTF8.GetBytes($$"""{"level": {{level}}, "timestamp": "{{stamp}}"}"""), 200, 0.1));
        });
        var seen = new List<int>();
        monitor.OnNewHead = seen.Add;
        await monitor.CheckNow();          // first sighting: no callback
        await monitor.CheckNow();          // same level: no callback
        level = 101;
        await monitor.CheckNow();
        level = 103;
        await monitor.CheckNow();
        Assert.Equal([101, 103], seen);
    }
}
