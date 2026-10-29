using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using Signet.Models;

namespace Signet.Services;

/// <summary>Health of the configured node, as seen from <c>GET /chains/main/blocks/head/header</c>.</summary>
public abstract record NodeStatus
{
    public sealed record Unknown : NodeStatus;
    /// <summary>Reachable, head is fresh. Ages and latencies in seconds.</summary>
    public sealed record Healthy(int BlockLevel, double HeadAge, double Latency) : NodeStatus;
    /// <summary>Reachable, but something is off (stale head, slow, odd response).</summary>
    public sealed record Degraded(string Reason, int? BlockLevel) : NodeStatus;
    /// <summary>Could not connect at all.</summary>
    public sealed record Unreachable(string Reason) : NodeStatus;

    public enum LightColor { Grey, Green, Yellow, Red }

    public LightColor Light => this switch
    {
        Healthy => LightColor.Green,
        Degraded => LightColor.Yellow,
        Unreachable => LightColor.Red,
        _ => LightColor.Grey,
    };

    public bool IsHealthy => this is Healthy;

    public int? Level => this switch
    {
        Healthy h => h.BlockLevel,
        Degraded d => d.BlockLevel,
        _ => null,
    };

    public string Summary => this switch
    {
        Healthy h => $"Synced, head {NodeMonitor.Describe(h.HeadAge)} ago",
        Degraded d => d.Reason,
        Unreachable => "Node down",
        _ => "Checking…",
    };

    /// <summary>Longer explanation for tooltips; same as <c>Summary</c> unless there is more to say.</summary>
    public string Detail => this is Unreachable u ? u.Reason : Summary;
}

/// <summary>
/// Polls the node periodically and publishes a <see cref="NodeStatus"/>. Create and use it on the UI
/// thread: awaits resume there, so <c>Status</c> changes are raised where bindings expect them.
/// </summary>
public sealed partial class NodeMonitor : ObservableObject
{
    /// <summary>Reply slower than this (seconds) is "degraded" even if correct.</summary>
    public const double SlowLatency = 4;
    /// <summary>A head older than this (seconds) means the node is behind the chain.</summary>
    public const double StaleHead = 3 * 60;
    public const double Interval = 30;

    public sealed record ProbeReply(byte[] Data, int Status, double Latency);
    public delegate Task<ProbeReply> Probe(Uri url);

    [ObservableProperty] private NodeStatus status = new NodeStatus.Unknown();
    [ObservableProperty] private DateTimeOffset? lastChecked;

    /// <summary>Called when a check sees a higher head level than the last one: something may have changed.</summary>
    public Action<int>? OnNewHead { get; set; }
    private int? lastLevel;

    public Network Network
    {
        get => network;
        set
        {
            if (value == network) return;
            network = value;
            Status = new NodeStatus.Unknown();
            lastLevel = null;
            if (loop is not null) Restart();
        }
    }
    private Network network;

    private readonly Probe probe;
    private CancellationTokenSource? loop;

    public NodeMonitor(Network network, Probe? probe = null)
    {
        this.network = network;
        this.probe = probe ?? HttpProbe;
    }

    /// <summary>Starts periodic polling (idempotent).</summary>
    public void Start()
    {
        if (loop is null) Restart();
    }

    public void Stop()
    {
        loop?.Cancel();
        loop = null;
    }

    private void Restart()
    {
        loop?.Cancel();
        var cancel = loop = new CancellationTokenSource();
        _ = Run(cancel.Token);
    }

    private async Task Run(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await CheckNow();
            try { await Task.Delay(TimeSpan.FromSeconds(Interval), token); }
            catch (TaskCanceledException) { return; }
        }
    }

    /// <summary>One probe, outcome published in <c>Status</c>.</summary>
    public async Task CheckNow()
    {
        var url = Network.RpcURL.AppendingPathComponent("chains/main/blocks/head/header");
        ProbeReply? reply = null;
        Exception? failure = null;
        try { reply = await probe(url); }
        catch (Exception e) { failure = e; }
        Status = Evaluate(reply, failure, DateTimeOffset.UtcNow, Network.ExpectedChainID);
        LastChecked = DateTimeOffset.Now;
        if (Status.Level is { } level && level != lastLevel)
        {
            var isFirst = lastLevel is null;
            lastLevel = level;
            if (!isFirst) OnNewHead?.Invoke(level);
        }
    }

    /// <summary>Pure classification so it can be tested without a network. Exactly one of <c>reply</c>/<c>failure</c> is set.</summary>
    public static NodeStatus Evaluate(ProbeReply? reply, Exception? failure, DateTimeOffset now, string? expectedChainID = null)
    {
        if (reply is null) return new NodeStatus.Unreachable($"Cannot reach node: {failure?.Message ?? "no reply"}");
        if (reply.Status is < 200 or >= 300) return new NodeStatus.Degraded($"Node returned HTTP {reply.Status}", null);

        JsonObject? obj = null;
        try { obj = JsonNode.Parse(reply.Data) as JsonObject; } catch (JsonException) { }
        if (obj?["level"] is not JsonValue lv || !lv.TryGetValue<int>(out var level) ||
            obj["timestamp"] is not JsonValue ts || !ts.TryGetValue<string>(out var stamp) ||
            ParseTimestamp(stamp) is not { } time)
            return new NodeStatus.Degraded("Unexpected reply from node", null);
        // The header names its chain: a node on another network is as wrong as no node (S12).
        if (expectedChainID is not null && obj["chain_id"] is JsonValue cv && cv.TryGetValue<string>(out var found) && found != expectedChainID)
            return new NodeStatus.Unreachable($"Node serves chain {found}, not {expectedChainID}");

        var age = (now - time).TotalSeconds;
        if (age > StaleHead) return new NodeStatus.Degraded($"Node is behind: head is {Describe(age)} old", level);
        if (reply.Latency > SlowLatency)
            return new NodeStatus.Degraded($"Node is slow ({reply.Latency.ToString("0.0", CultureInfo.InvariantCulture)} s)", level);
        return new NodeStatus.Healthy(level, Math.Max(0, age), reply.Latency);
    }

    public static DateTimeOffset? ParseTimestamp(string text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;

    public static string Describe(double seconds)
    {
        var s = (int)Math.Round(seconds, MidpointRounding.AwayFromZero);
        if (s < 60) return $"{s} s";
        if (s < 3600) return $"{s / 60} min";
        if (s < 86400) return $"{s / 3600} h";
        return $"{s / 86400} d";
    }

    public static async Task<ProbeReply> HttpProbe(Uri url)
    {
        var clock = Stopwatch.StartNew();
        var reply = await Http.Get(Http.Shared, url, TimeSpan.FromSeconds(8));
        return new ProbeReply(reply.Body, reply.Status, clock.Elapsed.TotalSeconds);
    }
}
