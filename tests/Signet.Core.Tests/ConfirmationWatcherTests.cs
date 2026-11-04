using System.Collections.Concurrent;
using System.Net;
using Signet.Models;
using Signet.Services;

namespace Signet.Tests;

/// <summary>A node for <c>*.confirmation.invalid</c>, one fixture per host (upstream's <c>ConfirmationProtocol</c>).</summary>
internal sealed class ConfirmationHandler : HttpMessageHandler
{
    public enum Mode { Included, Malformed, Held, Exhausted }

    public sealed class Fixture(Mode mode)
    {
        public Mode Mode { get; } = mode;
        private readonly List<string> requests = [];
        private int headers;
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int HeadLevel() { lock (requests) { headers++; return Mode == Mode.Exhausted ? 109 + headers : 102; } }
        public void Record(string path) { lock (requests) requests.Add(path); Started.TrySetResult(); }
        public IReadOnlyList<string> Paths { get { lock (requests) return requests.ToList(); } }
    }

    private static readonly ConcurrentDictionary<string, Fixture> Fixtures = new();
    public static void Register(Fixture fixture, string host) => Fixtures[host] = fixture;
    public static void Remove(string host) => Fixtures.TryRemove(host, out _);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!;
        if (!Fixtures.TryGetValue(url.Host, out var fixture)) return new HttpResponseMessage(HttpStatusCode.NotFound);
        fixture.Record(url.AbsolutePath);
        if (fixture.Mode == Mode.Held)
        {
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { fixture.Stopped.TrySetResult(); throw; }
        }
        string body;
        if (url.AbsolutePath.EndsWith("/header", StringComparison.Ordinal)) body = $"{{\"level\":{fixture.HeadLevel()}}}";
        else if (fixture.Mode == Mode.Malformed) body = "{\"operations\":42}";
        else body = fixture.Mode == Mode.Included && url.AbsolutePath.EndsWith("/101", StringComparison.Ordinal)
            ? "{\"operations\":[[],[],[],[{\"hash\":\"ooFixture\"}]]}" : "{\"operations\":[[],[],[],[]]}";
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
    }
}

/// <summary>F17: the shipped watcher through V8 and the native read-call boundary.</summary>
public class ConfirmationWatcherTests
{
    private static (ConfirmationHandler.Fixture Fixture, TaquitoChainService Chain, TaquitoBridge Bridge, string Host) Make(ConfirmationHandler.Mode mode)
    {
        var host = $"{Guid.NewGuid():N}.confirmation.invalid";
        var fixture = new ConfirmationHandler.Fixture(mode);
        ConfirmationHandler.Register(fixture, host);
        var bridge = new TaquitoBridge(new HttpClient(new ConfirmationHandler()) { Timeout = Timeout.InfiniteTimeSpan }, systemLog: _ => { });
        var url = new Uri($"https://{host}/selected-node");
        bridge.OperationJournal.Save($"{{\"hash\":\"ooFixture\",\"signedBytes\":\"fixture\",\"rpcURL\":\"{url.AbsoluteString()}\",\"headLevel\":100}}");
        return (fixture, new TaquitoChainService(Network.Mainnet.UsingNode(url), bridge), bridge, host);
    }

    [Fact]
    public async Task NativeConfirmationUsesSelectedNodeHashAndRemovesJournalRecord()
    {
        var (f, chain, bridge, host) = Make(ConfirmationHandler.Mode.Included);
        try
        {
            Assert.Equal(101, await chain.WaitForConfirmation("ooFixture"));
            Assert.Equal(["/selected-node/chains/main/blocks/head/header", "/selected-node/chains/main/blocks/99", "/selected-node/chains/main/blocks/100", "/selected-node/chains/main/blocks/101"], f.Paths);
            Assert.Equal("[]", bridge.OperationJournal.Read());
        }
        finally { ConfirmationHandler.Remove(host); }
    }

    [Fact]
    public async Task NativeWatcherUsesFifteenBlockBudgetAndRetainsCursorOnExhaustion()
    {
        var (f, chain, bridge, host) = Make(ConfirmationHandler.Mode.Exhausted);
        try
        {
            var error = await Assert.ThrowsAsync<TaquitoBridge.BridgeError.JavaScript>(() => chain.WaitForConfirmation("ooFixture"));
            Assert.Equal("operation ooFixture not seen in 15 blocks", error.Message);
            Assert.Equal(4, f.Paths.Count(p => p.EndsWith("/header", StringComparison.Ordinal)));
            Assert.Contains("\"lastCheckedLevel\":113", bridge.OperationJournal.Read());
        }
        finally { ConfirmationHandler.Remove(host); }
    }

    [Fact]
    public async Task MalformedBlockRetainsRecoveryRecord()
    {
        var (_, chain, bridge, host) = Make(ConfirmationHandler.Mode.Malformed);
        try
        {
            await Assert.ThrowsAnyAsync<TaquitoBridge.BridgeError>(() => chain.WaitForConfirmation("ooFixture"));
            Assert.Contains("ooFixture", bridge.OperationJournal.Read());
        }
        finally { ConfirmationHandler.Remove(host); }
    }

    [Fact(Skip = "Windows' WaitForConfirmation takes no cancellation; the watcher's deadline (below) is what stops it")]
    public void CancellationStopsHTTPAndKeepsRecoveryRecord() { }

    [Fact]
    public async Task WatcherDeadlineStopsHTTPAndKeepsRecoveryRecord()
    {
        var (f, chain, bridge, host) = Make(ConfirmationHandler.Mode.Held);
        try
        {
            var error = await Assert.ThrowsAsync<TaquitoBridge.BridgeError.TimedOut>(() =>
                bridge.CallWithin(TimeSpan.FromSeconds(0.1), "waitForRawOperation", chain.Network.RpcURL.AbsoluteString(), "ooFixture", 15));
            Assert.Contains("waitForRawOperation", error.Message);
            await f.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Contains("ooFixture", bridge.OperationJournal.Read());
        }
        finally { ConfirmationHandler.Remove(host); }
    }
}
