using System.Text.Json.Nodes;

namespace Signet.Models;

/// <summary>A dApp as it describes itself in a TZIP-10 request.</summary>
public sealed record DAppInfo(string Name, Uri? Icon, Uri? Url, string SenderId);

/// <summary>What a connected dApp is asking the wallet to do.</summary>
public abstract record DAppRequest(string Id, DAppInfo App)
{
    public sealed record Permission(string Id, DAppInfo App, string NetworkType, Uri? RpcURL, IReadOnlyList<string> Scopes) : DAppRequest(Id, App);
    public sealed record Operation(string Id, DAppInfo App, string NetworkType, Uri? RpcURL, Address Source, string OperationsJSON) : DAppRequest(Id, App);
    public sealed record SignPayload(string Id, DAppInfo App, Address Source, string SigningType, string Payload) : DAppRequest(Id, App);
    public sealed record Unsupported(string Id, DAppInfo App, string Type) : DAppRequest(Id, App);

    /// <summary>Parses the <c>{kind: "request", message: {...}}</c> event the bridge emits.</summary>
    public static DAppRequest? Parse(string eventJSON)
    {
        JsonObject? message;
        try
        {
            if (JsonNode.Parse(eventJSON) is not JsonObject @event || String(@event["kind"]) != "request") return null;
            message = @event["message"] as JsonObject;
        }
        catch (System.Text.Json.JsonException) { return null; }
        if (message is null || String(message["id"]) is not { } id || String(message["type"]) is not { } type) return null;

        var meta = message["appMetadata"] as JsonObject ?? [];
        var app = new DAppInfo(
            Name: String(meta["name"]) ?? "Unknown dApp",
            Icon: Url(String(meta["icon"])),
            Url: Url(String(meta["appUrl"])),
            SenderId: String(meta["senderId"]) ?? String(message["senderId"]) ?? "");
        var network = message["network"] as JsonObject;
        var networkType = String(network?["type"]) ?? "mainnet";
        var rpcURL = Url(String(network?["rpcUrl"]));

        switch (type)
        {
            case "permission_request":
                var scopes = (message["scopes"] as JsonArray)?.Select(String).OfType<string>().ToList() ?? [];
                return new Permission(id, app, networkType, rpcURL, scopes);
            case "operation_request":
                if (String(message["sourceAddress"]) is not { } source || message["operationDetails"] is not { } details)
                    return new Unsupported(id, app, type);
                return new Operation(id, app, networkType, rpcURL, new Address(source), details.ToJsonString());
            case "sign_payload_request":
                if (String(message["sourceAddress"]) is not { } signer || String(message["payload"]) is not { } payload)
                    return new Unsupported(id, app, type);
                return new SignPayload(id, app, new Address(signer), String(message["signingType"]) ?? "raw", payload);
            default:
                return new Unsupported(id, app, type);
        }
    }

    private static string? String(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;

    private static Uri? Url(string? text) =>
        text is not null && Uri.TryCreate(text, UriKind.Absolute, out var url) ? url : null;

    /// <summary>Why a sign_payload request must be refused, or <c>null</c> when it is safe to put in front of the user.</summary>
    public string? SignRefusal => this is SignPayload s ? DAppSignPayload.Refusal(s.SigningType, s.Payload) : null;

    /// <summary>Which of our networks a request's network refers to, or <c>null</c> if we cannot serve it.</summary>
    public static Network? NetworkFor(string type, Uri? rpcURL)
    {
        var lower = type.ToLowerInvariant();
        // A dApp's "custom" network is identified by its RPC, never by our own Custom entry.
        if (Network.All.FirstOrDefault(n => n.Chain == lower && n.Chain != "custom") is { } match) return match;
        if (lower == "custom" && rpcURL is not null)
        {
            if (Network.All.FirstOrDefault(n => n.RpcURL.Host == rpcURL.Host) is { } known) return known;
            var label = rpcURL.Host.Length > 0 ? rpcURL.Host : rpcURL.AbsoluteUri;
            return new Network($"Custom ({label})", "custom", rpcURL, tezosDomainsURL: null, tzktURL: null);
        }
        return null;
    }
}

/// <summary>A line in the operation approval sheet.</summary>
public sealed record DAppOperationSummary(string Kind, string? Destination, decimal? Amount, string? Entrypoint, string? Delegate);

/// <summary>A permission a dApp holds, as stored by the SDK.</summary>
public sealed record DAppPermission(
    string AccountIdentifier,
    string SenderId,
    string Address,
    string AppName,
    Uri? AppIcon,
    string NetworkType,
    IReadOnlyList<string> Scopes,
    DateTimeOffset? ConnectedAt)
{
    public string Id => AccountIdentifier + ":" + SenderId;
}

/// <summary>
/// What Signet is willing to sign for a dApp's sign_payload request.
///
/// A signature is over the Blake2b hash of the raw bytes, and the chain signs operations the same
/// way with a <c>03</c> watermark in front, so a "message" whose hex starts with <c>03</c> is an operation
/// signature a dApp could inject. Only the <c>micheline</c> type is accepted, and only when the bytes
/// start with <c>05</c>, the packed-data prefix no chain operation uses, and decode as exactly one
/// Micheline expression with nothing left over; that is the form every dApp produces for
/// "Tezos Signed Message" and TZIP-17 permits. The <c>operation</c> and <c>raw</c> types are
/// refused outright. The bridge applies the same rule before signing.
/// </summary>
public static class DAppSignPayload
{
    public const int MaxHexLength = 64 * 1024;

    public static string? Refusal(string signingType, string payload)
    {
        if (signingType != "micheline")
            return $"Signet only signs “micheline” messages, not “{signingType}”, because other types could be operations.";
        if (payload.Length % 2 != 0 || payload.Length == 0 || !payload.All(Uri.IsHexDigit))
            return "The message is not valid hex.";
        if (payload.Length > MaxHexLength) return "The message is too long to sign.";
        if (!payload.StartsWith("05", StringComparison.Ordinal))
            return "The message is not packed Michelson data (it does not start with 05), so it could be an operation.";
        if (!MichelineBinary.IsPackedExpression(payload))
            return "The message is not one complete packed Michelson expression.";
        return null;
    }
}
