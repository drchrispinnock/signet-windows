using System.Text.Json;
using System.Text.Json.Serialization;

namespace Signet.Services;

/// <summary>
/// UI state remembered between launches: which wallet was showing, which network was chosen
/// and any custom nodes. Serialised exactly as upstream's <c>Codable</c> struct: camelCase keys,
/// sorted, absent values omitted.
/// </summary>
public sealed record AppState
{
    [JsonPropertyName("networkName"), JsonPropertyOrder(1)]
    public string? NetworkName { get; init; }

    /// <summary>Custom RPC nodes by network name, for networks where the user replaced the default.</summary>
    [JsonPropertyName("nodeURLs"), JsonPropertyOrder(2)]
    public IReadOnlyDictionary<string, string>? NodeURLs { get; init; }

    [JsonPropertyName("selectedWalletAlias"), JsonPropertyOrder(3)]
    public string? SelectedWalletAlias { get; init; }

    public bool Equals(AppState? other) =>
        other is not null && NetworkName == other.NetworkName && SelectedWalletAlias == other.SelectedWalletAlias &&
        (NodeURLs ?? new Dictionary<string, string>()).OrderBy(p => p.Key).SequenceEqual(
            (other.NodeURLs ?? new Dictionary<string, string>()).OrderBy(p => p.Key)) &&
        (NodeURLs is null) == (other.NodeURLs is null);

    public override int GetHashCode() => HashCode.Combine(NetworkName, SelectedWalletAlias, NodeURLs?.Count);
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
