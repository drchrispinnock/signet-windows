using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Signet.Models;

namespace Signet.Services;

/// <summary>
/// Where a network's shielded transactions (TzEL) live: the rollup node whose durable state
/// holds the notes and deposit pools, the rollup itself, the layer 1 bridge ticketer that
/// funds a pool, the operator that relays proved requests to the rollup (bearer token), the
/// proving service, and the DAL producer every shield pays a fee note to.
/// </summary>
/// <param name="ProducerFeeMutez">The producer-fee note's value, in mutez (the operator's inclusion policy).</param>
/// <param name="ProducerResource">The bundled resource holding the producer's payment address, or null when <c>ProducerFields</c> is given.</param>
public sealed record ShieldedConfig(Uri RollupNode, string RollupAddress, Address Ticketer, Uri OperatorURL, Uri ProvingURL, ulong ProducerFeeMutez,
                                    string? ProducerResource = null, IReadOnlyDictionary<string, string>? ProducerFields = null)
{
    /// <summary>
    /// Shadownet's deployment (verified October 2026, <c>docs/tzel/RESEARCH.md</c>). The producer
    /// address is the operator's published fee address.
    /// </summary>
    public static readonly ShieldedConfig Shadownet = new(
        new Uri("http://rollup.tzel.tezos.com:8932"),
        "sr1Psv7PdY533c4njAW4qg2acbMhi8goqxcK",
        new Address("KT1RCZzNe8G4VQ94YWqb6Xz4q9WGWVdMWSB4"),
        new Uri("http://operator.tzel.tezos.com:8787"),
        new Uri("https://proving.tzel.tezos.com"),
        100_000,
        ProducerResource: "tzel-shadownet-producer");

    /// <summary>
    /// The items the Shielded settings sheet shows per network. Where Signet ships a deployment
    /// each has a default; elsewhere the fields start blank and the network can shield once
    /// every one is filled in.
    /// </summary>
    public enum Setting { RollupNode, RollupAddress, Ticketer, OperatorURL, ProvingURL, ProducerFee, ProducerAddress }

    public static IReadOnlyList<Setting> AllSettings { get; } = Enum.GetValues<Setting>();

    /// <summary>A deployment from one text per item (each already normalised); null until every item is there.</summary>
    public static ShieldedConfig? From(IReadOnlyDictionary<Setting, string> fields)
    {
        if (!fields.TryGetValue(Setting.RollupNode, out var node) || !Uri.TryCreate(node, UriKind.Absolute, out var nodeURL) ||
            !fields.TryGetValue(Setting.RollupAddress, out var rollup) || !fields.TryGetValue(Setting.Ticketer, out var ticketer) ||
            !fields.TryGetValue(Setting.OperatorURL, out var op) || !Uri.TryCreate(op, UriKind.Absolute, out var operatorURL) ||
            !fields.TryGetValue(Setting.ProvingURL, out var proving) || !Uri.TryCreate(proving, UriKind.Absolute, out var provingURL) ||
            !fields.TryGetValue(Setting.ProducerFee, out var fee) || !ulong.TryParse(fee, NumberStyles.None, CultureInfo.InvariantCulture, out var feeMutez) ||
            !fields.TryGetValue(Setting.ProducerAddress, out var producer) || PaymentAddressFields(producer) is not { } producerFields)
            return null;
        return new ShieldedConfig(nodeURL, rollup, new Address(ticketer), operatorURL, provingURL, feeMutez, ProducerFields: producerFields);
    }

    public string Text(Setting setting) => setting switch
    {
        Setting.RollupNode => RollupNode.AbsoluteString(),
        Setting.RollupAddress => RollupAddress,
        Setting.Ticketer => Ticketer.Value,
        Setting.OperatorURL => OperatorURL.AbsoluteString(),
        Setting.ProvingURL => ProvingURL.AbsoluteString(),
        Setting.ProducerFee => ProducerFeeMutez.ToString(CultureInfo.InvariantCulture),
        _ => ProducerAddressJSONOrNull() is { } fields ? CompactJSON(fields) : "",
    };

    public static bool IsRollupAddress(string text) =>
        text.Length == 36 && text.StartsWith("sr1", StringComparison.Ordinal) && text[3..].All("123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz".Contains);

    /// <summary>
    /// A TzEL service URL: http or https (the Shadownet deployment serves plain HTTP), a host,
    /// no credentials.
    /// </summary>
    public static Uri? ServiceURL(string text)
    {
        var trimmed = text.Trim().TrimEnd('/');
        if (trimmed.Length == 0 || trimmed.Contains(' ')) return null;
        var lower = trimmed.ToLowerInvariant();
        if (!lower.StartsWith("http://", StringComparison.Ordinal) && !lower.StartsWith("https://", StringComparison.Ordinal))
        {
            if (trimmed.Contains("://")) return null;
            trimmed = "https://" + trimmed;
        }
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var url) || url.Host.Length == 0 || url.UserInfo.Length > 0) return null;
        return url;
    }

    /// <summary>
    /// The six hex fields of a payment address in the reference JSON form (also accepting the
    /// reference CLI's <c>{index, address}</c> wrapper), checked for their lengths.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? PaymentAddressFields(string text)
    {
        JsonObject? obj;
        try { obj = JsonNode.Parse(text) as JsonObject; }
        catch (JsonException) { return null; }
        if (obj?["address"] is JsonObject inner) obj = inner;
        if (obj is null) return null;
        var lengths = new Dictionary<string, int> { ["d_j"] = 32, ["auth_root"] = 32, ["auth_pub_seed"] = 32, ["nk_tag"] = 32, ["ek_v"] = 1184, ["ek_d"] = 1184 };
        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, length) in lengths)
        {
            if (obj[key] is not JsonValue v || !v.TryGetValue<string>(out var value) || HexBytes.Decode(value.ToLowerInvariant())?.Length != length) return null;
            fields[key] = value.ToLowerInvariant();
        }
        return fields;
    }

    public static string CompactJSON(IReadOnlyDictionary<string, string> fields) =>
        JsonSerializer.Serialize(new SortedDictionary<string, string>(fields.ToDictionary(), StringComparer.Ordinal));

    private IReadOnlyDictionary<string, string>? ProducerAddressJSONOrNull()
    {
        try { return ProducerAddressJSON(); }
        catch (ShieldedError) { return null; }
    }

    public IReadOnlyDictionary<string, string> ProducerAddressJSON()
    {
        if (ProducerFields is { } fields) return fields;
        var name = ProducerResource ?? "";
        using var stream = typeof(ShieldedConfig).Assembly.GetManifestResourceStream(name + ".json")
            ?? throw new ShieldedError.BadReply($"the producer fee address {name}.json is missing from the app");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new ShieldedError.BadReply($"the producer fee address {name}.json is missing from the app");
    }

    public bool Equals(ShieldedConfig? other) =>
        other is not null && RollupNode == other.RollupNode && RollupAddress == other.RollupAddress && Ticketer == other.Ticketer &&
        OperatorURL == other.OperatorURL && ProvingURL == other.ProvingURL && ProducerFeeMutez == other.ProducerFeeMutez &&
        ProducerResource == other.ProducerResource &&
        (ProducerFields ?? new Dictionary<string, string>()).OrderBy(p => p.Key).SequenceEqual((other.ProducerFields ?? new Dictionary<string, string>()).OrderBy(p => p.Key));
    public override int GetHashCode() => HashCode.Combine(RollupNode, RollupAddress, Ticketer, ProducerFeeMutez);
}

public static class ShieldedSettingExtensions
{
    public static string RawValue(this ShieldedConfig.Setting setting) => setting switch
    {
        ShieldedConfig.Setting.RollupNode => "rollupNode",
        ShieldedConfig.Setting.RollupAddress => "rollupAddress",
        ShieldedConfig.Setting.Ticketer => "ticketer",
        ShieldedConfig.Setting.OperatorURL => "operatorURL",
        ShieldedConfig.Setting.ProvingURL => "provingURL",
        ShieldedConfig.Setting.ProducerFee => "producerFee",
        _ => "producerAddress",
    };

    public static ShieldedConfig.Setting? SettingFromRawValue(string raw) => ShieldedConfig.AllSettings.Cast<ShieldedConfig.Setting?>().FirstOrDefault(s => s!.Value.RawValue() == raw);

    public static string Title(this ShieldedConfig.Setting setting) => setting switch
    {
        ShieldedConfig.Setting.RollupNode => "Rollup node",
        ShieldedConfig.Setting.RollupAddress => "Rollup",
        ShieldedConfig.Setting.Ticketer => "Bridge ticketer",
        ShieldedConfig.Setting.OperatorURL => "Operator",
        ShieldedConfig.Setting.ProvingURL => "Proving service",
        ShieldedConfig.Setting.ProducerFee => "Producer fee",
        _ => "Producer address",
    };

    public static string Prompt(this ShieldedConfig.Setting setting) => setting switch
    {
        ShieldedConfig.Setting.RollupNode or ShieldedConfig.Setting.OperatorURL or ShieldedConfig.Setting.ProvingURL => "http(s)://…",
        ShieldedConfig.Setting.RollupAddress => "sr1…",
        ShieldedConfig.Setting.Ticketer => "KT1…",
        ShieldedConfig.Setting.ProducerFee => "mutez",
        _ => "The producer's payment address (JSON)",
    };

    /// <summary>The text as kept, or null when it is not valid for this item.</summary>
    public static string? Normalised(this ShieldedConfig.Setting setting, string text)
    {
        var trimmed = text.Trim();
        return setting switch
        {
            ShieldedConfig.Setting.RollupNode or ShieldedConfig.Setting.OperatorURL or ShieldedConfig.Setting.ProvingURL => ShieldedConfig.ServiceURL(trimmed)?.AbsoluteString(),
            ShieldedConfig.Setting.RollupAddress => ShieldedConfig.IsRollupAddress(trimmed) ? trimmed : null,
            ShieldedConfig.Setting.Ticketer => trimmed.StartsWith("KT1", StringComparison.Ordinal) && new Address(trimmed).IsValidAccount ? trimmed : null,
            ShieldedConfig.Setting.ProducerFee => ulong.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var fee) && fee > 0
                ? fee.ToString(CultureInfo.InvariantCulture) : null,
            _ => ShieldedConfig.PaymentAddressFields(trimmed) is { } fields ? ShieldedConfig.CompactJSON(fields) : null,
        };
    }

    public static string Problem(this ShieldedConfig.Setting setting) => setting switch
    {
        ShieldedConfig.Setting.RollupNode or ShieldedConfig.Setting.OperatorURL or ShieldedConfig.Setting.ProvingURL => "Enter an http or https URL with a host name and no credentials.",
        ShieldedConfig.Setting.RollupAddress => "Enter a Smart Rollup address (sr1…).",
        ShieldedConfig.Setting.Ticketer => "Enter the bridge ticketer's contract address (KT1…).",
        ShieldedConfig.Setting.ProducerFee => "Enter a whole number of mutez above zero.",
        _ => "Paste a payment address: JSON with d_j, auth_root, auth_pub_seed, nk_tag (32-byte hex) and ek_v, ek_d (1184-byte hex).",
    };
}

public sealed record RollupHead(string Hash, int Level);

/// <summary>What the proving service returns for a finished job: the compressed proof and the public outputs, hex.</summary>
/// <param name="OutputPreimage">
/// As the service returns them: the bootloader header (task count, output size, program
/// hash, array length) and then the circuit's public outputs.
/// </param>
/// <param name="ProgramHash">The program the job was proved against.</param>
public sealed record ProofBundle(string ProofBytes, IReadOnlyList<string> OutputPreimage, string ProgramHash = "");

public abstract record ProofJob
{
    public sealed record Queued : ProofJob;
    public sealed record Running : ProofJob;
    public sealed record Done(ProofBundle Bundle) : ProofJob;
    public sealed record Failed(string Detail) : ProofJob;
    public sealed record Unknown(string Status) : ProofJob;
}

/// <summary>The operator's record of a relayed message.</summary>
/// <param name="Status"><c>pending_dal</c>, <c>commitment_included</c>, <c>attested</c>, <c>submitted_to_l1</c> or <c>failed</c>.</param>
public sealed record OperatorSubmission(string Id, string Status, string? Transport = null, string? OperationHash = null, string? Detail = null)
{
    public bool IsTerminal => Status is "submitted_to_l1" or "failed";
    public bool Succeeded => Status == "submitted_to_l1";
}

/// <summary>
/// A message the rollup has queued for layer 1 and not yet seen executed: for an unshield, the
/// ticketer's <c>burn</c> paying <c>AmountMutez</c> to the forged <c>Receiver</c>.
/// </summary>
public sealed record OutboxEntry(int OutboxLevel, int MessageIndex, string Destination, string Entrypoint, string Receiver, ulong AmountMutez);

/// <summary>
/// What executing an outbox message on layer 1 needs: the cemented commitment it is proved
/// against and the rollup node's proof, hex.
/// </summary>
public sealed record OutboxProof(string Commitment, string Proof);

/// <summary>
/// The three services a shield talks to, behind one interface so the flow can be tested
/// without a network (<c>MockShieldedRollup</c> in the tests).
/// </summary>
public interface IShieldedRollup
{
    Task<RollupHead> Head();
    Task<string> SmartRollupAddress();
    /// <summary>The byte length of a durable-state value, or null when the key is absent.</summary>
    Task<int?> DurableLength(string block, string key);
    /// <summary>A durable-state value, or null when the key is absent.</summary>
    Task<byte[]?> DurableValue(string block, string key);
    Task<string> ProgramHash(string program);
    /// <summary>Starts a proving job; returns its id.</summary>
    Task<string> StartProof(string programHash, IReadOnlyList<string> arguments);
    /// <summary>Long-polls a job once (the service waits up to 30 s for a change from <c>lastStatus</c>).</summary>
    Task<ProofJob> ProofJobFor(string id, string? lastStatus);
    Task<OperatorSubmission> Submit(string kind, string payloadHex, string rollupAddress, string token);
    Task<OperatorSubmission> SubmissionFor(string id, string token);
    /// <summary>The level of the last cemented commitment: outbox messages up to it can be executed.</summary>
    Task<int> CementedLevel();
    /// <summary>The outbox messages the rollup node has not seen executed.</summary>
    Task<IReadOnlyList<OutboxEntry>> PendingOutbox();
    /// <summary>The proof for executing one outbox message, against the last cemented commitment.</summary>
    Task<OutboxProof> OutboxProofFor(int outboxLevel, int messageIndex);
}

/// <summary>The kernel's durable-state layout (<c>apps/wallet</c> of trilitech/tzel, <c>/tzel/v1/state/…</c>).</summary>
public static class RollupState
{
    public const string AuthDomain = "/tzel/v1/state/auth_domain";
    public const string VerifierConfig = "/tzel/v1/state/verifier_config.bin";
    public const string BridgeTicketer = "/tzel/v1/state/bridge/ticketer";
    public const string LastInputLevel = "/tzel/v1/state/last_input_level";
    public const string PrivateTxFeeLevel = "/tzel/v1/state/fees/private_tx_level";
    public const string PrivateTxCountInLevel = "/tzel/v1/state/fees/private_tx_count_in_level";
    public const string TreeSize = "/tzel/v1/state/tree/size";
    public const string TreeRoot = "/tzel/v1/state/tree/root";
    public const string NotePrefix = "/tzel/v1/state/notes/";
    public const string NullifierCount = "/tzel/v1/state/nullifiers/count";
    public const string NullifierIndexPrefix = "/tzel/v1/state/nullifiers/index/";
    public const string DepositBalancePrefix = "/tzel/v1/state/deposits/balance/";
    /// <summary>A published note is <c>cm ‖ encrypted note</c>; large values are split into 1024-byte chunks.</summary>
    public const int NoteChunkBytes = 1024;
    public const int PublishedNoteBytes = 32 + 3455;

    /// <summary>
    /// The fee rule (<c>required_tx_fee_for_private_tx_count</c>): 0.1 tez, doubling for every
    /// private transaction beyond the second in the same level, up to 64 times.
    /// </summary>
    public const ulong MinTxFee = 100_000;
    public const ulong BasePrivateTxsPerLevel = 2;
    public const ulong MaxDynamicFeeStep = 6;

    public static string Indexed(string prefix, int index) => prefix + ((ulong)index).ToString("x16", CultureInfo.InvariantCulture);
    public static string DepositBalance(string pubkeyHash) => DepositBalancePrefix + pubkeyHash;

    public static ulong RequiredTxFee(ulong privateTxsInLevel)
    {
        var step = Math.Min(privateTxsInLevel > BasePrivateTxsPerLevel - 1 ? privateTxsInLevel - (BasePrivateTxsPerLevel - 1) : 0, MaxDynamicFeeStep);
        return MinTxFee << (int)step;
    }
}

public static class ShieldedRollupExtensions
{
    public static async Task<string> AuthDomain(this IShieldedRollup rollup, string block) =>
        await rollup.DurableValue(block, RollupState.AuthDomain) is { Length: 32 } bytes
            ? HexBytes.Encode(bytes)
            : throw new ShieldedError.RollupNotConfigured("its authentication domain is not set");

    public static async Task<bool> VerifierConfigured(this IShieldedRollup rollup, string block) =>
        await rollup.DurableLength(block, RollupState.VerifierConfig) is not null;

    public static async Task<string?> BridgeTicketer(this IShieldedRollup rollup, string block) =>
        await rollup.DurableValue(block, RollupState.BridgeTicketer) is { } bytes ? Encoding.UTF8.GetString(bytes) : null;

    private static async Task<ulong?> U64(this IShieldedRollup rollup, string block, string key)
    {
        if (await rollup.DurableValue(block, key) is not { } bytes) return null;
        if (bytes.Length != 8) throw new ShieldedError.BadReply($"{key} is {bytes.Length} bytes, not 8");
        return BitConverter.ToUInt64(bytes);
    }

    private static async Task<int?> I32(this IShieldedRollup rollup, string block, string key)
    {
        if (await rollup.DurableValue(block, key) is not { } bytes) return null;
        if (bytes.Length != 4) throw new ShieldedError.BadReply($"{key} is {bytes.Length} bytes, not 4");
        return BitConverter.ToInt32(bytes);
    }

    /// <summary>The fee the rollup will demand of the next inbox level, read at <c>head</c>.</summary>
    public static async Task<ulong> RequiredTxFee(this IShieldedRollup rollup, RollupHead head)
    {
        var nextInbox = (int)Math.Min((long)head.Level + 1, int.MaxValue);
        var lastInput = await rollup.I32(head.Hash, RollupState.LastInputLevel);
        var feeLevel = await rollup.I32(head.Hash, RollupState.PrivateTxFeeLevel);
        ulong count = 0;
        if (lastInput == nextInbox && feeLevel == nextInbox) count = await rollup.U64(head.Hash, RollupState.PrivateTxCountInLevel) ?? 0;
        return RollupState.RequiredTxFee(count);
    }

    /// <summary>A deposit pool's balance in mutez, null when the rollup has none for it.</summary>
    public static Task<ulong?> PoolBalance(this IShieldedRollup rollup, string block, string pubkeyHash) =>
        rollup.U64(block, RollupState.DepositBalance(pubkeyHash));

    /// <summary>The commitment tree's current root, hex.</summary>
    public static async Task<string> TreeRoot(this IShieldedRollup rollup, string block) =>
        await rollup.DurableValue(block, RollupState.TreeRoot) is { Length: 32 } bytes
            ? HexBytes.Encode(bytes)
            : throw new ShieldedError.BadReply("the commitment tree root is missing");

    public static async Task<int> TreeSize(this IShieldedRollup rollup, string block)
    {
        if (await rollup.U64(block, RollupState.TreeSize) is not { } size) return 0;
        if (size > int.MaxValue) throw new ShieldedError.BadReply($"tree size {size}");
        return (int)size;
    }

    /// <summary>The published note at <c>index</c>: the direct key, else its chunks.</summary>
    public static async Task<byte[]?> PublishedNote(this IShieldedRollup rollup, string block, int index)
    {
        var key = RollupState.Indexed(RollupState.NotePrefix, index);
        if (await rollup.DurableLength(block, key) is not null && await rollup.DurableValue(block, key) is { } direct) return direct;
        if (await rollup.U64(block, key + "/len") is not { } length) return null;
        if (length != RollupState.PublishedNoteBytes) throw new ShieldedError.BadReply($"note {index} is {length} bytes");
        var bytes = new List<byte>();
        var chunk = 0;
        while (bytes.Count < (int)length)
        {
            var part = await rollup.DurableValue(block, key + "/chunk/" + chunk.ToString("x8", CultureInfo.InvariantCulture))
                ?? throw new ShieldedError.BadReply($"note {index} is missing chunk {chunk}");
            bytes.AddRange(part);
            chunk++;
            if (chunk > 8) throw new ShieldedError.BadReply($"note {index} has too many chunks");
        }
        if (bytes.Count != (int)length) throw new ShieldedError.BadReply($"note {index} chunks make {bytes.Count} bytes");
        return bytes.ToArray();
    }

    public static async Task<int> NullifierCount(this IShieldedRollup rollup, string block)
    {
        if (await rollup.U64(block, RollupState.NullifierCount) is not { } count) return 0;
        if (count > int.MaxValue) throw new ShieldedError.BadReply($"nullifier count {count}");
        return (int)count;
    }

    public static async Task<string> Nullifier(this IShieldedRollup rollup, string block, int index) =>
        await rollup.DurableValue(block, RollupState.Indexed(RollupState.NullifierIndexPrefix, index)) is { Length: 32 } bytes
            ? HexBytes.Encode(bytes)
            : throw new ShieldedError.BadReply($"nullifier {index} is missing");

    /// <summary>Submits a proving job and waits for it: up to <c>polls</c> long-polls of 30 s each.</summary>
    public static async Task<ProofBundle> Prove(this IShieldedRollup rollup, string program, IReadOnlyList<string> arguments, int polls = 60, CancellationToken cancel = default)
    {
        var hash = await rollup.ProgramHash(program);
        var job = await rollup.StartProof(hash, arguments);
        string? last = null;
        var unknown = 0;
        for (var i = 0; i < polls; i++)
        {
            cancel.ThrowIfCancellationRequested();
            switch (await rollup.ProofJobFor(job, last))
            {
                case ProofJob.Queued: last = "queued"; unknown = 0; break;
                case ProofJob.Running: last = "running"; unknown = 0; break;
                case ProofJob.Done done: return done.Bundle with { ProgramHash = hash };
                case ProofJob.Failed failed: throw new ShieldedError.Proving(failed.Detail);
                case ProofJob.Unknown other:
                    if (++unknown >= 3) throw new ShieldedError.Proving($"job {job} keeps reporting status {other.Status}");
                    break;
            }
        }
        throw new ShieldedError.Proving($"job {job} did not finish in time");
    }

    /// <summary>Hands a message to the operator and waits until it is on layer 1 (or has failed).</summary>
    public static async Task<OperatorSubmission> Deliver(this IShieldedRollup rollup, string kind, string payloadHex, string rollupAddress, string token, TimeSpan? timeout = null) =>
        await rollup.AwaitDelivery(await rollup.Submit(kind, payloadHex, rollupAddress, token), token, timeout);

    /// <summary>How often a submission is polled; tests shorten it.</summary>
    public static TimeSpan DeliveryPollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Polls a submission until it is on layer 1 (or has failed).</summary>
    public static async Task<OperatorSubmission> AwaitDelivery(this IShieldedRollup rollup, OperatorSubmission submitted, string token, TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(600);
        var current = submitted;
        var deadline = DateTimeOffset.UtcNow + limit;
        while (!current.IsTerminal)
        {
            if (DateTimeOffset.UtcNow >= deadline)
                throw new ShieldedError.SubmissionPending($"submission {current.Id} is still {current.Status} after {(int)limit.TotalSeconds} seconds");
            await Task.Delay(DeliveryPollInterval);
            current = await rollup.SubmissionFor(current.Id, token);
        }
        if (!current.Succeeded) throw new ShieldedError.SubmissionFailed(current.Detail ?? $"the operator marked submission {current.Id} {current.Status}");
        return current;
    }
}

/// <summary>
/// The live services, read with byte and JSON budgets (S19). The rollup node and the operator
/// speak plain HTTP on Shadownet.
/// </summary>
public sealed class RollupNodeService(ShieldedConfig config, HttpClient? http = null) : IShieldedRollup
{
    private readonly HttpClient http = http ?? Http.Shared;

    private const int SmallReply = 16 << 10;
    private const int NoteReply = 64 << 10;
    private const int PageReply = 4 << 20;
    /// <summary>A proof bundle is a few hundred KB of hex; the kernel takes up to 8 MB of proof.</summary>
    private const int ProofReply = 24 << 20;

    private static Uri Url(Uri baseURL, string path, params (string Name, string Value)[] query)
    {
        var text = baseURL.AbsoluteString().TrimEnd('/') + path;
        if (query.Length > 0) text += "?" + string.Join("&", query.Select(q => $"{Uri.EscapeDataString(q.Name)}={Uri.EscapeDataString(q.Value)}"));
        return new Uri(text);
    }

    private async Task<JsonNode?> Send(HttpRequestMessage request, int limit, TimeSpan timeout)
    {
        using var cancel = new CancellationTokenSource(timeout);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel.Token);
        if (response.Content.Headers.ContentLength > limit) throw new ShieldedError.BadReply($"the reply from {request.RequestUri?.Host} is too large");
        await using var stream = await response.Content.ReadAsStreamAsync(cancel.Token);
        using var body = new MemoryStream();
        var buffer = new byte[32 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancel.Token)) > 0)
        {
            if (body.Length + read > limit) throw new ShieldedError.BadReply($"the reply from {request.RequestUri?.Host} is too large");
            body.Write(buffer, 0, read);
        }
        var bytes = body.ToArray();
        var status = (int)response.StatusCode;
        if (status is < 200 or >= 300)
        {
            var text = Encoding.UTF8.GetString(bytes.AsSpan(0, Math.Min(300, bytes.Length))).Trim();
            var where = request.RequestUri?.Host ?? "";
            if (status is 401 or 403) throw new ShieldedError.OperatorRefused($"{where} did not accept the access token (HTTP {status})");
            throw new ShieldedError.BadReply($"HTTP {status} from {where}{(text.Length == 0 ? "" : $": {text}")}");
        }
        try { return JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { MaxDepth = 128 }); }
        catch (JsonException) { throw new ShieldedError.BadReply($"unreadable JSON from {request.RequestUri?.Host}"); }
    }

    private Task<JsonNode?> GetJSON(Uri url, int limit, TimeSpan? timeout = null, string? token = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return Send(request, limit, timeout ?? TimeSpan.FromSeconds(20));
    }

    private Task<JsonNode?> PostJSON(Uri url, JsonObject body, int limit, string? token = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return Send(request, limit, TimeSpan.FromSeconds(60));
    }

    private static int? Int(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<long>(out var n) && n is >= int.MinValue and <= int.MaxValue => (int)n,
        JsonValue v when v.TryGetValue<string>(out var s) && int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) => n,
        _ => null,
    };

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    // ---- Rollup node -----------------------------------------------------------------------------

    public async Task<RollupHead> Head()
    {
        if (Str(await GetJSON(Url(config.RollupNode, "/global/block/head/hash"), SmallReply)) is not { Length: > 0 } hash) throw new ShieldedError.BadReply("no head hash");
        var level = Int(await GetJSON(Url(config.RollupNode, $"/global/block/{hash}/level"), SmallReply)) ?? throw new ShieldedError.BadReply("no head level");
        return new RollupHead(hash, level);
    }

    public async Task<string> SmartRollupAddress() =>
        Str(await GetJSON(Url(config.RollupNode, "/global/smart_rollup_address"), SmallReply)) ?? throw new ShieldedError.BadReply("no rollup address");

    public async Task<int?> DurableLength(string block, string key)
    {
        var value = await GetJSON(Url(config.RollupNode, $"/global/block/{block}/durable/wasm_2_0_0/length", ("key", key)), SmallReply);
        if (value is null) return null;
        return Int(value) ?? throw new ShieldedError.BadReply($"length of {key}");
    }

    public async Task<byte[]?> DurableValue(string block, string key)
    {
        var value = await GetJSON(Url(config.RollupNode, $"/global/block/{block}/durable/wasm_2_0_0/value", ("key", key)), NoteReply);
        if (value is null) return null;
        if (Str(value) is not { } text) throw new ShieldedError.BadReply($"value of {key}");
        var hex = text.StartsWith("0x", StringComparison.Ordinal) ? text[2..] : text;
        if (hex.Length == 0) return [];
        // The node answers a text value (the ticketer address) as the text itself.
        return HexBytes.Decode(hex) ?? Encoding.UTF8.GetBytes(text);
    }

    // ---- Proving service -------------------------------------------------------------------------

    public async Task<string> ProgramHash(string program)
    {
        var reply = await GetJSON(Url(config.ProvingURL, "/v1/programs"), SmallReply);
        var hash = (reply?["programs"] as JsonArray)?.FirstOrDefault(p => Str(p?["name"]) == program)?["program_hash"];
        return Str(hash) is { } text && HexBytes.Decode(text)?.Length == 32 ? text : throw new ShieldedError.Proving($"the proving service has no program named {program}");
    }

    public async Task<string> StartProof(string programHash, IReadOnlyList<string> arguments)
    {
        var body = new JsonObject { ["program_hash"] = programHash, ["arguments"] = new JsonArray(arguments.Select(a => (JsonNode?)a).ToArray()) };
        var reply = await PostJSON(Url(config.ProvingURL, "/v1/jobs"), body, SmallReply);
        return Str(reply?["job_id"]) is { Length: > 0 and < 200 } id ? id : throw new ShieldedError.Proving("the proving service returned no job id");
    }

    public async Task<ProofJob> ProofJobFor(string id, string? lastStatus)
    {
        var query = new List<(string, string)> { ("wait", "30") };
        if (lastStatus is not null) query.Add(("if_state_changed_from", lastStatus));
        var reply = await GetJSON(Url(config.ProvingURL, $"/v1/jobs/{id}", [.. query]), ProofReply, TimeSpan.FromSeconds(45));
        if (reply is not JsonObject obj) throw new ShieldedError.Proving("unexpected job reply");
        switch (Str(obj["status"]))
        {
            case "queued": return new ProofJob.Queued();
            case "running": return new ProofJob.Running();
            case "done":
                if (obj["proof_bundle"] is not JsonObject bundle || Str(bundle["proof_bytes"]) is not { } proof || HexBytes.Decode(proof) is null ||
                    bundle["output_preimage"] is not JsonArray outputs || outputs.Select(Str).ToList() is not { } texts || texts.Any(t => t is null || HexBytes.Decode(t)?.Length != 32))
                    throw new ShieldedError.Proving("the finished job carries no proof bundle");
                return new ProofJob.Done(new ProofBundle(proof, texts!));
            case "failed":
                var message = Str(obj["error"]?["message"]) ?? "unknown";
                return new ProofJob.Failed(message[..Math.Min(500, message.Length)]);
            case var other:
                return new ProofJob.Unknown(other ?? "none");
        }
    }

    // ---- Operator --------------------------------------------------------------------------------

    private static OperatorSubmission ParseSubmission(JsonNode? reply)
    {
        if (reply?["submission"] is not JsonObject submission || Str(submission["id"]) is not { } id || Str(submission["status"]) is not { } status)
            throw new ShieldedError.BadReply("the operator returned no submission");
        var detail = Str(submission["detail"]);
        return new OperatorSubmission(id, status, Str(submission["transport"]), Str(submission["operation_hash"]), detail?[..Math.Min(500, detail.Length)]);
    }

    public async Task<OperatorSubmission> Submit(string kind, string payloadHex, string rollupAddress, string token) =>
        ParseSubmission(await PostJSON(Url(config.OperatorURL, "/v1/rollup/submissions"),
                                       new JsonObject { ["kind"] = kind, ["rollup_address"] = rollupAddress, ["payload"] = payloadHex }, NoteReply, token));

    public async Task<OperatorSubmission> SubmissionFor(string id, string token) =>
        ParseSubmission(await GetJSON(Url(config.OperatorURL, $"/v1/rollup/submissions/{id}"), NoteReply, token: token));

    // ---- Outbox ----------------------------------------------------------------------------------

    public async Task<int> CementedLevel() =>
        Int(await GetJSON(Url(config.RollupNode, "/global/block/cemented/level"), SmallReply)) ?? throw new ShieldedError.BadReply("no cemented level");

    /// <summary><c>/local/outbox/pending</c>: <c>[{outbox_level, messages: [{message_index, message: {transactions: [{destination, entrypoint, parameters}]}}]}]</c>.</summary>
    public async Task<IReadOnlyList<OutboxEntry>> PendingOutbox()
    {
        if (await GetJSON(Url(config.RollupNode, "/local/outbox/pending"), PageReply) is not JsonArray levels) throw new ShieldedError.BadReply("pending outbox");
        var entries = new List<OutboxEntry>();
        foreach (var level in levels)
        {
            if (Int(level?["outbox_level"]) is not { } outboxLevel || level?["messages"] is not JsonArray messages) continue;
            foreach (var message in messages)
            {
                if (Int(message?["message_index"]) is not { } index || message?["message"]?["transactions"] is not JsonArray { Count: 1 } transactions ||
                    Str(transactions[0]?["destination"]) is not { } destination || Str(transactions[0]?["entrypoint"]) is not { } entrypoint ||
                    BurnEntry(outboxLevel, index, destination, entrypoint, transactions[0]?["parameters"]) is not { } entry) continue;
                entries.Add(entry);
            }
        }
        return entries;
    }

    /// <summary>The ticketer's <c>burn</c>: <c>Pair receiver (Pair ticketer (Pair (Pair 0 None) amount))</c>.</summary>
    public static OutboxEntry? BurnEntry(int outboxLevel, int index, string destination, string entrypoint, JsonNode? parameters)
    {
        static JsonArray? Args(JsonNode? node) => node is JsonObject o ? o["args"] as JsonArray : null;
        if (Args(parameters) is not { Count: 2 } top || Str(top[0]?["bytes"]) is not { } receiver ||
            Args(top[1]) is not { Count: 2 } second || Args(second[1]) is not { Count: 2 } third ||
            Str(third[1]?["int"]) is not { } amountText || !ulong.TryParse(amountText, NumberStyles.None, CultureInfo.InvariantCulture, out var amount))
            return null;
        return new OutboxEntry(outboxLevel, index, destination, entrypoint, receiver.ToLowerInvariant(), amount);
    }

    public async Task<OutboxProof> OutboxProofFor(int outboxLevel, int messageIndex)
    {
        JsonNode? reply;
        try
        {
            reply = await GetJSON(Url(config.RollupNode, $"/global/block/cemented/helpers/proofs/outbox/{outboxLevel}/messages", ("index", messageIndex.ToString(CultureInfo.InvariantCulture))),
                                  PageReply, TimeSpan.FromSeconds(60));
        }
        catch (ShieldedError.BadReply e) when (e.Detail.Contains("not_cemented"))
        {
            throw new ShieldedError.WithdrawalNotReady("the rollup commitment covering it is not cemented yet (about 14 days after the unshield)");
        }
        if (Str(reply?["commitment"]) is not { } commitment || !commitment.StartsWith("src1", StringComparison.Ordinal) ||
            Str(reply?["proof"]) is not { Length: > 0 } proof || HexBytes.Decode(proof) is null)
            throw new ShieldedError.BadReply("outbox proof");
        return new OutboxProof(commitment, proof);
    }
}
