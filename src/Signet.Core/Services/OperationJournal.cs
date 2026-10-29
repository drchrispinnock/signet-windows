using System.Text.Json;
using System.Text.Json.Nodes;

namespace Signet.Services;

/// <summary>
/// Signed operations are persisted before injection (S15). This contains no secret keys, but retains
/// spendable signed bytes, so it uses the same atomic-write and wallet-lock policy as key state.
/// Without a directory the records live in memory (tests).
/// </summary>
public sealed class OperationJournal(string? directory = null)
{
    public sealed class JournalError(string message) : Exception(message);

    public const string FileName = "pending_operations.json";
    private readonly Lock gate = new();
    private Dictionary<string, string> memory = [];

    /// <summary>Every record, as a JSON array of the objects the bridge saved.</summary>
    public string Read() => Transaction(records =>
        new JsonArray(records.Values.Select(json => JsonNode.Parse(json)).ToArray()).ToJsonString());

    public void Save(string json)
    {
        if (json.Length > 1 << 20 || JsonNode.Parse(json) is not JsonObject record ||
            record["hash"] is not JsonValue h || !h.TryGetValue<string>(out var hash) || hash.Length == 0 ||
            record["signedBytes"] is not JsonValue b || !b.TryGetValue<string>(out var bytes) || bytes.Length > 1 << 20 ||
            record["rpcURL"] is not JsonValue r || !r.TryGetValue<string>(out _))
            throw new JournalError("Malformed operation record");
        Transaction(records =>
        {
            if (!records.ContainsKey(hash) && records.Count >= 128) throw new JournalError("Too many pending operations");
            var source = Str(record["source"]);
            var chain = Str(record["chainID"]);
            foreach (var (otherHash, otherJSON) in records)
            {
                if (otherHash == hash) continue;
                var other = JsonNode.Parse(otherJSON) as JsonObject;
                // One outstanding operation per account and chain: a replacement is refused until the first settles.
                if (source is not null && Str(other?["source"]) == source &&
                    (Str(other?["chainID"]) is null || chain is null || Str(other?["chainID"]) == chain))
                    throw new JournalError("Another operation from this account is still pending");
            }
            records[hash] = json;
            return 0;
        });
    }

    public void Remove(string hash) => Transaction(records => records.Remove(hash));

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private T Transaction<T>(Func<Dictionary<string, string>, T> body)
    {
        lock (gate)
        {
            if (directory is null) return body(memory);
            Directory.CreateDirectory(directory);
            return TezosClientStore.WithWalletLock(directory, () =>
            {
                var path = Path.Combine(directory, FileName);
                var records = new Dictionary<string, string>();
                if (File.Exists(path))
                {
                    if (new FileInfo(path).Length > 16 << 20) throw new JournalError("Operation recovery storage is too large");
                    records = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(path)) ?? [];
                }
                var result = body(records);
                AtomicFile.WriteAllText(path, JsonSerializer.Serialize(new SortedDictionary<string, string>(records, StringComparer.Ordinal)));
                return result;
            });
        }
    }
}
