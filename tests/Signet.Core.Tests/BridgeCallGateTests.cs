using Signet.Services;

namespace Signet.Tests;

/// <summary>Actual V8 calls must keep their bridge turn until the asynchronous outcome settles.</summary>
public class BridgeCallGateTests
{
    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "signet-call-gate-" + Guid.NewGuid().ToString("N"));
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaquitoBridge Bridge { get; }
        public Fixture() => Bridge = new(logPath: Path.Combine(directory, "bridge.log"), systemLog: line =>
        {
            if (line == "[log] first started") Started.TrySetResult();
        });
        public void Dispose()
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static Task Install(TaquitoBridge bridge) => bridge.ExecuteForTesting("""
        globalThis.entries = [];
        globalThis.active = 0;
        globalThis.peak = 0;
        globalThis.releaseFirst = null;
        globalThis.TaquitoBridge = {
          signPayload: async function (label, reject) {
            entries.push(label);
            active++;
            peak = Math.max(peak, active);
            try {
              if (label === "first") {
                var wait = new Promise(function (resolve) { releaseFirst = resolve; });
                console.log("first started");
                await wait;
              }
              if (reject) throw new Error("synthetic rejection");
              return { label: label, peak: peak };
            } finally { active--; }
          },
          version: function () { entries.push("read"); return active; }
        };
        """);

    [Theory(Timeout = 15_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SigningAndReadsWaitForPreviousOutcome(bool reject)
    {
        using var fixture = new Fixture();
        var bridge = fixture.Bridge;
        await Install(bridge);
        // A signing call has no read deadline, even when one is explicitly supplied.
        var first = bridge.CallWithin(TimeSpan.FromMilliseconds(1), "signPayload", "first", reject);
        await fixture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var second = bridge.Sign("signPayload", "second", false);
        var read = bridge.CallWithin(TimeSpan.FromMilliseconds(30), "version");
        try
        {
            // Let the queued read's nominal duration pass while the first signature is pending.
            await Task.Delay(80, TestContext.Current.CancellationToken);
            await bridge.ExecuteForTesting("if (entries.join(',') !== 'first' || peak !== 1) throw new Error('foreground calls overlapped');");
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            Assert.False(read.IsCompleted);
        }
        finally { await bridge.ExecuteForTesting("releaseFirst();"); }
        if (reject)
            await Assert.ThrowsAsync<TaquitoBridge.BridgeError.JavaScript>(() => first);
        else
            Assert.Equal(1, (await first)["peak"]?.IntValue);
        Assert.Equal(1, (await second)["peak"]?.IntValue);
        Assert.Equal(0, (await read).IntValue);
        await bridge.ExecuteForTesting("if (entries.join(',') !== 'first,second,read') throw new Error('calls ran out of order');");
    }

    [Fact(Timeout = 15_000)]
    public async Task TimedOutReadReleasesItsTurnAfterResettingContext()
    {
        using var fixture = new Fixture();
        var bridge = fixture.Bridge;
        await bridge.ExecuteForTesting("globalThis.TaquitoBridge = { hang: function () { return new Promise(function () {}); }, version: function () { return 'stale context'; } };");
        var hanging = bridge.CallWithin(TimeSpan.FromMilliseconds(50), "hang");
        var next = bridge.Call("version");
        await Assert.ThrowsAsync<TaquitoBridge.BridgeError.TimedOut>(() => hanging);
        Assert.Equal("0.15.0", (await next.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).StringValue);
    }

    [Fact(Timeout = 15_000)]
    public async Task SynchronousFailureReleasesItsTurn()
    {
        using var fixture = new Fixture();
        var bridge = fixture.Bridge;
        await bridge.ExecuteForTesting("globalThis.TaquitoBridge = { signPayload: function () { throw new Error('synthetic throw'); }, version: function () { return 'ready'; } };");
        var failed = bridge.Sign("signPayload");
        var next = bridge.Call("version");
        await Assert.ThrowsAsync<TaquitoBridge.BridgeError.JavaScript>(() => failed);
        Assert.Equal("ready", (await next.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).StringValue);
        await Assert.ThrowsAsync<TaquitoBridge.BridgeError.InputTooLarge>(() => bridge.Call("version", new string('x', TaquitoBridge.MaxInputBytes + 1)));
        Assert.Equal("ready", (await bridge.Call("version")).StringValue);
    }

    [Fact(Timeout = 15_000)]
    public async Task AbandoningTheCallerWaitDoesNotReleaseAPendingSignature()
    {
        using var fixture = new Fixture();
        var bridge = fixture.Bridge;
        await Install(bridge);
        var first = bridge.Sign("signPayload", "first", false);
        await fixture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var next = bridge.Call("version");
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(cancel.Token));
            await bridge.ExecuteForTesting("if (entries.join(',') !== 'first') throw new Error('signature turn was released early');");
            Assert.False(next.IsCompleted);
        }
        finally { await bridge.ExecuteForTesting("releaseFirst();"); }
        await first;
        Assert.Equal(0, (await next).IntValue);
    }

    [Fact(Timeout = 15_000)]
    public async Task WaitingQueueIsBoundedAndRecoversAfterSaturation()
    {
        using var fixture = new Fixture();
        var bridge = fixture.Bridge;
        await Install(bridge);
        var first = bridge.Sign("signPayload", "first", false);
        await fixture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var waiting = Enumerable.Range(0, 64).Select(_ => bridge.Call("version")).ToArray();
        try
        {
            var full = await Assert.ThrowsAnyAsync<TaquitoBridge.BridgeError>(() => bridge.Call("version"));
            Assert.Contains("waiting", full.Message, StringComparison.Ordinal);
        }
        finally { await bridge.ExecuteForTesting("releaseFirst();"); }
        await first;
        var results = await Task.WhenAll(waiting).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.All(results, result => Assert.Equal(0, result.IntValue));
        Assert.Equal(0, (await bridge.Call("version")).IntValue);
    }

    [Fact(Timeout = 15_000)]
    public async Task SeparateBridgesDoNotBlockEachOther()
    {
        using var firstFixture = new Fixture();
        using var otherFixture = new Fixture();
        var bridge = firstFixture.Bridge;
        await Install(bridge);
        await Install(otherFixture.Bridge);
        var first = bridge.Sign("signPayload", "first", false);
        await firstFixture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        try
        {
            Assert.Equal(0, (await otherFixture.Bridge.Call("version").WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).IntValue);
            Assert.False(first.IsCompleted);
        }
        finally { await bridge.ExecuteForTesting("releaseFirst();"); }
        await first;
    }
}
