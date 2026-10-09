using System.Diagnostics;
using System.Text.Json;

namespace Signet.Services;

/// <summary>
/// Key/value store the Octez Connect SDK persists its peers, permissions and seed in.
/// Synchronous because the bridge calls it from JavaScript on the bridge thread.
/// </summary>
public interface IBridgeStorage
{
    string? Get(string key);
    void Set(string key, string value);
    void Delete(string key);
}

/// <summary>
/// A single JSON object in <c>&lt;wallet directory&gt;\octez-connect.json</c>. It contains the SDK's secret
/// seed; files under the user profile are readable only by that user by default on Windows.
/// </summary>
public sealed class OctezConnectStorage : IBridgeStorage
{
    public string Path { get; }
    private readonly Lock gate = new();
    private readonly SortedDictionary<string, string> values;

    public OctezConnectStorage(string? directory = null)
    {
        Path = System.IO.Path.Combine(directory ?? TezosClientStore.DefaultDirectory, "octez-connect.json");
        try
        {
            var stored = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path));
            values = new SortedDictionary<string, string>(stored ?? [], StringComparer.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        }
    }

    public string? Get(string key)
    {
        lock (gate) return values.TryGetValue(key, out var v) ? v : null;
    }

    public void Set(string key, string value)
    {
        lock (gate)
        {
            values[key] = value;
            Persist();
        }
    }

    public void Delete(string key)
    {
        lock (gate)
        {
            values.Remove(key);
            Persist();
        }
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            AtomicFile.WriteAllText(Path, JsonSerializer.Serialize(values));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"Signet: could not save Octez Connect state: {e.Message}");
        }
    }
}

/// <summary>For tests and previews.</summary>
public sealed class InMemoryBridgeStorage : IBridgeStorage
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, string> values = [];
    public string? Get(string key) { lock (gate) return values.TryGetValue(key, out var v) ? v : null; }
    public void Set(string key, string value) { lock (gate) values[key] = value; }
    public void Delete(string key) { lock (gate) values.Remove(key); }
}
