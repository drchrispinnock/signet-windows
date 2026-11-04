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
/// Shielded transactions: <c>shieldedEnabled</c> and <c>shieldedSettings</c>, as upstream.
/// </summary>
public sealed record AppState
{
    /// <summary>
    /// Etherlink governance contracts by network name, then by <c>EtherlinkGovernance.Process</c>
    /// raw value, only where the user replaced what we ship; an empty string turns the process off.
    /// </summary>
    [JsonPropertyName("etherlinkGovernance"), JsonPropertyOrder(0)]
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? EtherlinkGovernance { get; init; }

    /// <summary>Etherlink on or off by network name, only where the user turned it on (or saved off on the Mac).</summary>
    [JsonPropertyName("l2Enabled"), JsonPropertyOrder(1)]
    public IReadOnlyDictionary<string, bool>? L2Enabled { get; init; }

    /// <summary>Etherlink RPC nodes by network name, where the user replaced (or supplied) one.</summary>
    [JsonPropertyName("l2NodeURLs"), JsonPropertyOrder(2)]
    public IReadOnlyDictionary<string, string>? L2NodeURLs { get; init; }

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

    /// <summary>Shielded transactions on by network name, only where the user turned them on.</summary>
    [JsonPropertyName("shieldedEnabled"), JsonPropertyOrder(7)]
    public IReadOnlyDictionary<string, bool>? ShieldedEnabled { get; init; }

    /// <summary>
    /// TzEL services by network name, then by <c>ShieldedConfig.Setting</c> raw value: where the
    /// user replaced what we ship, or filled in a network we ship nothing for. Written by the
    /// model as fields are applied (<c>Remember</c> leaves it alone), read back by <c>Resolved</c>.
    /// </summary>
    [JsonPropertyName("shieldedSettings"), JsonPropertyOrder(8)]
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? ShieldedSettings { get; init; }

    /// <summary>
    /// <c>base</c> with everything saved for it applied: its node, its Layer 2 switch and node, and
    /// its links. Saved text that is not a URL is ignored; Layer 2 is off unless saved on.
    /// </summary>
    public Network Resolved(Network @base)
    {
        var network = @base;
        if (NodeURLs?.GetValueOrDefault(@base.Name) is { } saved && Network.NodeURL(saved) is { } url) network = network.UsingNode(url);
        var l2Node = (L2NodeURLs?.GetValueOrDefault(@base.Name) is { } l2Saved ? Network.NodeURL(l2Saved) : null) ?? @base.L2DefaultRPCURL;
        network = network.UsingL2(L2Enabled?.GetValueOrDefault(@base.Name) ?? false, l2Node);
        network = network.UsingShielded(ShieldedEnabled?.GetValueOrDefault(@base.Name) ?? false);
        var fields = ShieldedFields(@base);
        var shielded = ShieldedConfig.From(fields);
        // The shipped producer address stays the bundled resource unless it was replaced.
        if (@base.ShieldedDefault is { } shipped && shielded is not null &&
            fields.GetValueOrDefault(ShieldedConfig.Setting.ProducerAddress) == shipped.Text(ShieldedConfig.Setting.ProducerAddress))
            shielded = shielded with { ProducerResource = shipped.ProducerResource, ProducerFields = shipped.ProducerFields };
        network = network.UsingShielded(shielded);
        foreach (var service in Enum.GetValues<Network.Service>())
        {
            if (ServiceURLs?.GetValueOrDefault(@base.Name)?.GetValueOrDefault(service.RawValue()) is not { } link) continue;
            // "" is a link the user turned off; anything that is not a URL is ignored.
            if (link.Length == 0) network = network.Using(null, service);
            else if (Network.NodeURL(link) is { } linkURL) network = network.Using(linkURL, service);
        }
        foreach (var process in Models.EtherlinkGovernance.AllProcesses)
        {
            if (EtherlinkGovernance?.GetValueOrDefault(@base.Name)?.GetValueOrDefault(process.RawValue()) is not { } savedContract) continue;
            // "" is a process the user turned off; anything that is not a contract address is ignored.
            if (savedContract.Length == 0) network = network.UsingEtherlinkGovernance(null, process);
            else if (Models.EtherlinkGovernance.ContractAddress(savedContract) is { } contract) network = network.UsingEtherlinkGovernance(contract, process);
        }
        return network;
    }

    /// <summary>
    /// Records <c>net</c>'s node, Layer 2 choices and links, each only where it differs from the
    /// defaults, so the maps stay sparse and <c>null</c> when nothing is custom.
    /// </summary>
    public AppState Remember(Network net) => RememberNode(net).RememberL2(net).RememberShielded(net).RememberServices(net).RememberEtherlinkGovernance(net);

    /// <summary>Shielded transactions are off by default everywhere; only "on" is saved.</summary>
    public AppState RememberShielded(Network net)
    {
        var flags = new SortedDictionary<string, bool>(ShieldedEnabled?.ToDictionary() ?? [], StringComparer.Ordinal);
        if (net.ShieldedEnabled) flags[net.Name] = true;
        else flags.Remove(net.Name);
        return this with { ShieldedEnabled = flags.Count == 0 ? null : flags };
    }

    /// <summary>Every TzEL item of <c>base</c> as text: what we ship, overlaid with what the user saved (when valid).</summary>
    public IReadOnlyDictionary<ShieldedConfig.Setting, string> ShieldedFields(Network @base)
    {
        var fields = new Dictionary<ShieldedConfig.Setting, string>();
        foreach (var setting in ShieldedConfig.AllSettings)
        {
            if (@base.ShieldedDefault?.Text(setting) is { Length: > 0 } shipped) fields[setting] = shipped;
            if (ShieldedSettings?.GetValueOrDefault(@base.Name)?.GetValueOrDefault(setting.RawValue()) is { } saved && setting.Normalised(saved) is { } valid)
                fields[setting] = valid;
        }
        return fields;
    }

    /// <summary>Saves one item for <c>base</c> (null or what we ship: forget the saved text).</summary>
    public AppState SettingShieldedField(string? text, ShieldedConfig.Setting setting, Network @base)
    {
        var all = new SortedDictionary<string, IReadOnlyDictionary<string, string>>(ShieldedSettings?.ToDictionary() ?? [], StringComparer.Ordinal);
        var fields = new SortedDictionary<string, string>(all.GetValueOrDefault(@base.Name)?.ToDictionary() ?? [], StringComparer.Ordinal);
        if (text is null || text == @base.ShieldedDefault?.Text(setting)) fields.Remove(setting.RawValue());
        else fields[setting.RawValue()] = text;
        if (fields.Count == 0) all.Remove(@base.Name);
        else all[@base.Name] = fields;
        return this with { ShieldedSettings = all.Count == 0 ? null : all };
    }

    public AppState RememberNode(Network net)
    {
        var nodes = new SortedDictionary<string, string>(NodeURLs?.ToDictionary() ?? [], StringComparer.Ordinal);
        if (net.IsUsingDefaultNode) nodes.Remove(net.Name);
        else nodes[net.Name] = net.RpcURL.AbsoluteString();
        return this with { NodeURLs = nodes.Count == 0 ? null : nodes };
    }

    /// <summary>Etherlink is off by default everywhere, with our endpoint where we ship one.</summary>
    public AppState RememberL2(Network net)
    {
        var nodes = new SortedDictionary<string, string>(L2NodeURLs?.ToDictionary() ?? [], StringComparer.Ordinal);
        if (net.IsUsingDefaultL2Node || net.L2RPCURL is null) nodes.Remove(net.Name);
        else nodes[net.Name] = net.L2RPCURL.AbsoluteString();
        var flags = new SortedDictionary<string, bool>(L2Enabled?.ToDictionary() ?? [], StringComparer.Ordinal);
        if (net.L2Enabled) flags[net.Name] = true;
        else flags.Remove(net.Name);
        return this with { L2NodeURLs = nodes.Count == 0 ? null : nodes, L2Enabled = flags.Count == 0 ? null : flags };
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

    /// <summary>Etherlink governance contracts ("" for a process turned off).</summary>
    public AppState RememberEtherlinkGovernance(Network net)
    {
        var all = new SortedDictionary<string, IReadOnlyDictionary<string, string>>(EtherlinkGovernance?.ToDictionary() ?? [], StringComparer.Ordinal);
        var contracts = new SortedDictionary<string, string>(all.GetValueOrDefault(net.Name)?.ToDictionary() ?? [], StringComparer.Ordinal);
        foreach (var process in Models.EtherlinkGovernance.AllProcesses)
        {
            if (net.IsUsingDefaultEtherlinkGovernance(process)) contracts.Remove(process.RawValue());
            else contracts[process.RawValue()] = net.EtherlinkGovernanceContracts.Get(process)?.Value ?? "";
        }
        if (contracts.Count == 0) all.Remove(net.Name);
        else all[net.Name] = contracts;
        return this with { EtherlinkGovernance = all.Count == 0 ? null : all };
    }

    public bool Equals(AppState? other) =>
        other is not null && NetworkName == other.NetworkName && SelectedWalletAlias == other.SelectedWalletAlias &&
        SameMap(NodeURLs, other.NodeURLs) && SameMap(L2NodeURLs, other.L2NodeURLs) &&
        (L2Enabled is null) == (other.L2Enabled is null) &&
        (L2Enabled ?? new Dictionary<string, bool>()).OrderBy(p => p.Key, StringComparer.Ordinal).SequenceEqual(
            (other.L2Enabled ?? new Dictionary<string, bool>()).OrderBy(p => p.Key, StringComparer.Ordinal)) &&
        (ServiceURLs is null) == (other.ServiceURLs is null) &&
        (ServiceURLs ?? new Dictionary<string, IReadOnlyDictionary<string, string>>()).Count == (other.ServiceURLs?.Count ?? 0) &&
        (ServiceURLs ?? new Dictionary<string, IReadOnlyDictionary<string, string>>()).All(p => other.ServiceURLs!.TryGetValue(p.Key, out var o) && SameMap(p.Value, o)) &&
        SameNestedMap(EtherlinkGovernance, other.EtherlinkGovernance) &&
        (ShieldedEnabled ?? new Dictionary<string, bool>()).OrderBy(p => p.Key, StringComparer.Ordinal).SequenceEqual(
            (other.ShieldedEnabled ?? new Dictionary<string, bool>()).OrderBy(p => p.Key, StringComparer.Ordinal)) &&
        (ShieldedEnabled is null) == (other.ShieldedEnabled is null) &&
        SameNestedMap(ShieldedSettings, other.ShieldedSettings);

    private static bool SameNestedMap(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? a, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? b) =>
        (a is null) == (b is null) && (a?.Count ?? 0) == (b?.Count ?? 0) &&
        (a ?? new Dictionary<string, IReadOnlyDictionary<string, string>>()).All(p => b!.TryGetValue(p.Key, out var o) && SameMap(p.Value, o));

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
