using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Signet.Models;

namespace Signet.Services;

/// <summary>
/// UI state remembered between launches: which wallet was showing, which network was chosen
/// and any custom nodes. Serialised exactly as upstream's <c>Codable</c> struct: camelCase keys,
/// sorted, absent values omitted.
///
/// Etherlink and shielded transactions are not ported yet; their maps (<c>l2Enabled</c>,
/// <c>l2NodeURLs</c>, <c>shieldedEnabled</c>, <c>shieldedSettings</c>) are carried through unread, so a
/// wallet directory shared with the Mac app keeps them.
/// </summary>
public sealed record AppState
{
    [JsonPropertyName("l2Enabled"), JsonPropertyOrder(1)]
    public JsonNode? L2Enabled { get; init; }

    [JsonPropertyName("l2NodeURLs"), JsonPropertyOrder(2)]
    public JsonNode? L2NodeURLs { get; init; }

    [JsonPropertyName("networkName"), JsonPropertyOrder(3)]
    public string? NetworkName { get; init; }

    /// <summary>Custom RPC nodes by network name, for networks where the user replaced the default.</summary>
    [JsonPropertyName("nodeURLs"), JsonPropertyOrder(4)]
    public IReadOnlyDictionary<string, string>? NodeURLs { get; init; }

    [JsonPropertyName("selectedWalletAlias"), JsonPropertyOrder(5)]
    public string? SelectedWalletAlias { get; init; }

    /// <summary>
    /// TzKT API, explorer and faucet links by network name, then by <c>Network.Service</c> raw
    /// value, only where the user replaced the default; an empty string means the link is off.
    /// </summary>
    [JsonPropertyName("serviceURLs"), JsonPropertyOrder(6)]
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? ServiceURLs { get; init; }

    [JsonPropertyName("shieldedEnabled"), JsonPropertyOrder(7)]
    public JsonNode? ShieldedEnabled { get; init; }

    [JsonPropertyName("shieldedSettings"), JsonPropertyOrder(8)]
    public JsonNode? ShieldedSettings { get; init; }

    /// <summary>
    /// <c>base</c> with everything saved for it applied: its node and its links. Saved text that is
    /// not a URL is ignored.
    /// </summary>
    public Network Resolved(Network @base)
    {
        var network = @base;
        if (NodeURLs?.GetValueOrDefault(@base.Name) is { } saved && Network.NodeURL(saved) is { } url) network = network.UsingNode(url);
        foreach (var service in Enum.GetValues<Network.Service>())
        {
            if (ServiceURLs?.GetValueOrDefault(@base.Name)?.GetValueOrDefault(service.RawValue()) is not { } link) continue;
            // "" is a link the user turned off; anything that is not a URL is ignored.
            if (link.Length == 0) network = network.Using(null, service);
            else if (Network.NodeURL(link) is { } linkURL) network = network.Using(linkURL, service);
        }
        return network;
    }

    /// <summary>
    /// Records <c>net</c>'s node and links, each only where it differs from the defaults, so the
    /// maps stay sparse and <c>null</c> when nothing is custom.
    /// </summary>
    public AppState Remember(Network net) => RememberNode(net).RememberServices(net);

    public AppState RememberNode(Network net)
    {
        var nodes = new SortedDictionary<string, string>(NodeURLs?.ToDictionary() ?? [], StringComparer.Ordinal);
        if (net.IsUsingDefaultNode) nodes.Remove(net.Name);
        else nodes[net.Name] = net.RpcURL.AbsoluteString();
        return this with { NodeURLs = nodes.Count == 0 ? null : nodes };
    }

    /// <summary>TzKT, explorer and faucet links ("" for a link turned off).</summary>
    public AppState RememberServices(Network net)
    {
        var all = new SortedDictionary<string, IReadOnlyDictionary<string, string>>(ServiceURLs?.ToDictionary() ?? [], StringComparer.Ordinal);
        var links = new SortedDictionary<string, string>(all.GetValueOrDefault(net.Name)?.ToDictionary() ?? [], StringComparer.Ordinal);
        foreach (var service in Enum.GetValues<Network.Service>())
        {
            if (net.IsUsingDefault(service)) links.Remove(service.RawValue());
            else links[service.RawValue()] = net.UrlFor(service)?.AbsoluteString() ?? "";
        }
        if (links.Count == 0) all.Remove(net.Name);
        else all[net.Name] = links;
        return this with { ServiceURLs = all.Count == 0 ? null : all };
    }

    public bool Equals(AppState? other) =>
        other is not null && NetworkName == other.NetworkName && SelectedWalletAlias == other.SelectedWalletAlias &&
        SameMap(NodeURLs, other.NodeURLs) &&
        (ServiceURLs is null) == (other.ServiceURLs is null) &&
        (ServiceURLs ?? new Dictionary<string, IReadOnlyDictionary<string, string>>()).Count == (other.ServiceURLs?.Count ?? 0) &&
        (ServiceURLs ?? new Dictionary<string, IReadOnlyDictionary<string, string>>()).All(p => other.ServiceURLs!.TryGetValue(p.Key, out var o) && SameMap(p.Value, o)) &&
        JsonNode.DeepEquals(L2Enabled, other.L2Enabled) && JsonNode.DeepEquals(L2NodeURLs, other.L2NodeURLs) &&
        JsonNode.DeepEquals(ShieldedEnabled, other.ShieldedEnabled) && JsonNode.DeepEquals(ShieldedSettings, other.ShieldedSettings);

    private static bool SameMap(IReadOnlyDictionary<string, string>? a, IReadOnlyDictionary<string, string>? b) =>
        (a is null) == (b is null) &&
        (a ?? new Dictionary<string, string>()).OrderBy(p => p.Key, StringComparer.Ordinal).SequenceEqual(
            (b ?? new Dictionary<string, string>()).OrderBy(p => p.Key, StringComparer.Ordinal));

    public override int GetHashCode() => HashCode.Combine(NetworkName, SelectedWalletAlias, NodeURLs?.Count, ServiceURLs?.Count);
}

public static class NetworkServiceExtensions
{
    /// <summary>The key upstream's <c>Network.Service</c> raw values use in <c>serviceURLs</c>.</summary>
    public static string RawValue(this Network.Service service) => service switch
    {
        Network.Service.Tzkt => "tzkt",
        Network.Service.Explorer => "explorer",
        _ => "faucet",
    };

    public static string Title(this Network.Service service) => service switch
    {
        Network.Service.Tzkt => "TzKT API",
        Network.Service.Explorer => "Block explorer",
        _ => "Faucet",
    };
}

public interface IAppStateStore
{
    AppState Load();
    void Save(AppState state);
}

/// <summary><c>.signet\state</c>, a small JSON file next to the wallet files.</summary>
public sealed class FileAppStateStore(string? directory = null) : IAppStateStore
{
    public string Path { get; } = System.IO.Path.Combine(directory ?? TezosClientStore.DefaultDirectory, "state");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public AppState Load()
    {
        try { return JsonSerializer.Deserialize<AppState>(File.ReadAllText(Path), Options) ?? new AppState(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new AppState(); }
    }

    public void Save(AppState state)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        AtomicFile.WriteAllText(Path, JsonSerializer.Serialize(state, Options));
    }
}

/// <summary>For previews and tests. Share one instance between two models to simulate a relaunch.</summary>
public sealed class InMemoryAppStateStore(AppState? state = null) : IAppStateStore
{
    private readonly Lock gate = new();
    private AppState state = state ?? new AppState();

    public AppState Load() { lock (gate) return state; }
    public void Save(AppState state) { lock (gate) this.state = state; }
}
