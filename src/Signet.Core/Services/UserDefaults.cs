using System.Text.Json;
using System.Text.Json.Nodes;

namespace Signet.Services;

/// <summary>
/// Windows stand-in for Foundation's <c>UserDefaults</c>, so ported code reads the same: a small JSON
/// object in <c>%LOCALAPPDATA%\Signet\preferences.json</c>. Holds only app preferences (wallet and
/// backup directories, appearance, …), never wallet data. Tests pass their own file, as upstream
/// tests use a fresh <c>UserDefaults(suiteName:)</c>.
/// </summary>
public sealed class UserDefaults
{
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Signet", "preferences.json");

    public static UserDefaults Standard { get; } = new(DefaultPath);

    public string FilePath { get; }
    private readonly Lock gate = new();
    private readonly JsonObject values;

    public UserDefaults(string filePath)
    {
        FilePath = filePath;
        try { values = JsonNode.Parse(File.ReadAllText(filePath)) as JsonObject ?? []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { values = []; }
    }

    /// <summary>A throwaway store in the temp folder, for tests.</summary>
    public static UserDefaults Temporary() =>
        new(Path.Combine(Path.GetTempPath(), $"signet-defaults-{Guid.NewGuid():N}.json"));

    public object? Object(string key)
    {
        lock (gate) return values[key]?.DeepClone();
    }

    public string? String(string key)
    {
        lock (gate) return values[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    }

    /// <summary>0 when missing or not a number, like <c>integer(forKey:)</c>.</summary>
    public int Integer(string key)
    {
        lock (gate) return values[key] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;
    }

    public bool Bool(string key)
    {
        lock (gate) return values[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    }

    public void Set(string key, string value) => Store(key, JsonValue.Create(value));
    public void Set(string key, int value) => Store(key, JsonValue.Create(value));
    public void Set(string key, bool value) => Store(key, JsonValue.Create(value));

    public void RemoveObject(string key) => Store(key, null);

    private void Store(string key, JsonNode? value)
    {
        lock (gate)
        {
            if (value is null) values.Remove(key);
            else values[key] = value;
            try
            {
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                AtomicFile.WriteAllText(FilePath, values.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Trace.WriteLine($"Signet: could not save preferences: {e.Message}");
            }
        }
    }
}

/// <summary>Path helpers standing in for <c>NSString.expandingTildeInPath</c> and <c>URL.standardizedFileURL</c>.</summary>
public static class PathUtility
{
    /// <summary>Expands a leading <c>~</c> to the user profile.</summary>
    public static string ExpandTilde(string path)
    {
        if (path == "~") return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.StartsWith("~/") || path.StartsWith("~\\"))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        return path;
    }

    /// <summary>Absolute, with <c>.</c>/<c>..</c> resolved and no trailing separator.</summary>
    public static string Standardized(string path)
    {
        var full = Path.GetFullPath(ExpandTilde(path));
        var root = Path.GetPathRoot(full) ?? "";
        return full.Length > root.Length ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
    }

    /// <summary>Windows paths are case-insensitive.</summary>
    public static bool SamePath(string a, string b) =>
        string.Equals(Standardized(a), Standardized(b), StringComparison.OrdinalIgnoreCase);
}
