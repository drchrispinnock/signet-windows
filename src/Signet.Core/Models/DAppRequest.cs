using System.Text.Json.Nodes;
using Signet.Services;

namespace Signet.Models;

/// <summary>A dApp as it describes itself in a TZIP-10 request.</summary>
public sealed record DAppInfo(string Name, Uri? Icon, Uri? Url, string SenderId);

/// <summary>What a connected dApp is asking the wallet to do.</summary>
public abstract record DAppRequest(string Id, DAppInfo App)
{
    public sealed record Permission(string Id, DAppInfo App, string NetworkType, Uri? RpcURL, IReadOnlyList<string> Scopes) : DAppRequest(Id, App)
    {
        public bool Equals(Permission? other) =>
            other is not null && Id == other.Id && App == other.App && NetworkType == other.NetworkType && RpcURL == other.RpcURL && Scopes.SequenceEqual(other.Scopes);
        public override int GetHashCode() => HashCode.Combine(Id, App, NetworkType);
    }
    public sealed record Operation(string Id, DAppInfo App, string NetworkType, Uri? RpcURL, Address Source, string OperationsJSON) : DAppRequest(Id, App);
    public sealed record SignPayload(string Id, DAppInfo App, Address Source, string SigningType, string Payload) : DAppRequest(Id, App);
    public sealed record Unsupported(string Id, DAppInfo App, string Type) : DAppRequest(Id, App);

    public bool IsPermission => this is Permission;
    public bool IsOperation => this is Operation;

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

        // The bridge rebuilt this message from the peer the transport authenticated (S04): the
        // sender id is the message's own, never the self-asserted one inside `appMetadata`.
        var meta = message["appMetadata"] as JsonObject ?? [];
        var app = new DAppInfo(
            Name: String(meta["name"]) ?? "Unknown dApp",
            Icon: Url(String(meta["icon"])),
            Url: Url(String(meta["appUrl"])),
            SenderId: String(message["senderId"]) ?? "");
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

    /// <summary>
    /// Which of our networks a request's network refers to, or <c>null</c> if we cannot serve it.
    ///
    /// A dApp's "custom" network names an RPC endpoint of the dApp's choosing (S12): it is
    /// accepted only as a public HTTPS endpoint without credentials, or when it is exactly the
    /// node the user configured for one of our networks (scheme, host, port and path). A dApp
    /// cannot point the wallet at this machine or the local network, and a user-configured
    /// local node is never reachable through a dApp's request unless the dApp names it exactly.
    /// </summary>
    public static Network? NetworkFor(string type, Uri? rpcURL)
    {
        var kind = type.ToLowerInvariant();
        // A dApp's "custom" network is identified by its RPC, never by our own Custom entry.
        if (Network.All.FirstOrDefault(n => n.Chain == kind && n.Chain != "custom") is { } match) return match;
        if (kind == "custom" && rpcURL is not null)
        {
            if (Network.All.FirstOrDefault(n => n.Chain != "custom" && SameEndpoint(n.DefaultRPCURL, rpcURL)) is { } known) return known;
            if (!EndpointPolicy.IsPublicHTTPS(rpcURL)) return null;
            return new Network($"Custom ({EndpointDescription(rpcURL)})", "custom", rpcURL, tezosDomainsURL: null, tzktURL: null);
        }
        return null;
    }

    /// <summary>Two endpoints are the same only when scheme, host, port and path all agree (S12).</summary>
    public static bool SameEndpoint(Uri? a, Uri? b)
    {
        if (a is null || b is null) return a is null && b is null;
        static string Key(Uri url) =>
            $"{url.Scheme.ToLowerInvariant()}://{url.IdnHost.ToLowerInvariant()}:{url.Port}{url.AbsolutePath.TrimEnd('/')}?{url.Query.TrimStart('?')}";
        return Key(a) == Key(b);
    }

    /// <summary>Host, port and path of an endpoint, for showing where a custom network points.</summary>
    public static string EndpointDescription(Uri url)
    {
        var text = url.Host.Length > 0 ? url.Host : url.AbsoluteUri;
        if (!url.IsDefaultPort) text += $":{url.Port}";
        var path = Uri.UnescapeDataString(url.AbsolutePath);
        if (path.Length > 0 && path != "/") text += path;
        return text;
    }
}

/// <summary>One operation of a dApp batch as the approval sheet shows it: what it does, what it costs.</summary>
/// <param name="Parameters">The contract call's parameters (or an origination's script) as Micheline JSON, verbatim.</param>
/// <param name="Effects">Decoded effects for standard contract calls (FA2/FA1.2 transfers, operators, allowances).</param>
/// <param name="Opaque">A contract call Signet could not decode: the user approves on the raw parameters alone.</param>
/// <param name="Fee">Exact fee from the node's simulation; <c>null</c> before preparation.</param>
/// <param name="RequestedFee">The dApp asked for a higher fee than the node's estimate; Signet pays the estimate.</param>
public sealed record DAppOperationSummary(
    string Kind, string? Destination, decimal? Amount, string? Entrypoint, string? Delegate,
    string? Parameters = null, IReadOnlyList<DAppOperationSummary.Effect>? Effects = null, bool Opaque = false,
    decimal? Fee = null, decimal? Burn = null, decimal? RequestedFee = null)
{
    /// <param name="Warning">Moves or exposes assets (token transfers, operator approvals, allowances, originations).</param>
    public sealed record Effect(string Text, bool Warning);

    public IReadOnlyList<Effect> EffectList => Effects ?? [];
}

/// <summary>A dApp batch simulated by the node and ready to sign exactly as shown.</summary>
/// <param name="RevealFee">Fee and burn of the reveal the batch will start with, when the account has not revealed its key.</param>
/// <param name="TotalDebit">Everything that leaves the account: amounts + fees + burn.</param>
/// <param name="PreparedJSON">The exact Taquito params that will be executed, fee/gas/storage included.</param>
public sealed record DAppPreparedBatch(
    string RequestID, IReadOnlyList<DAppOperationSummary> Operations, decimal? RevealFee, decimal? RevealBurn,
    decimal TotalAmount, decimal TotalFee, decimal TotalBurn, decimal TotalDebit, string PreparedJSON)
{
    public bool HasWarnings => Operations.Any(o => o.Opaque || o.EffectList.Any(e => e.Warning));
}

/// <summary>A permission a dApp holds, as stored by the SDK.</summary>
/// <param name="NetworkRPCURL">The endpoint a custom-network grant was made for; <c>null</c> for named networks.</param>
public sealed record DAppPermission(
    string AccountIdentifier,
    string SenderId,
    string Address,
    string AppName,
    Uri? AppIcon,
    string NetworkType,
    IReadOnlyList<string> Scopes,
    DateTimeOffset? ConnectedAt,
    Uri? NetworkRPCURL = null)
{
    public string Id => AccountIdentifier + ":" + SenderId;

    public bool Equals(DAppPermission? other) =>
        other is not null && Id == other.Id && Address == other.Address && NetworkType == other.NetworkType &&
        NetworkRPCURL == other.NetworkRPCURL && Scopes.SequenceEqual(other.Scopes);
    public override int GetHashCode() => HashCode.Combine(Id, Address, NetworkType);
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
