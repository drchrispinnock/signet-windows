using System.Text.Json;
using System.Text.Json.Nodes;
using Signet.Models;

namespace Signet.Services;

/// <summary>
/// Reverse lookups against the Tezos Domains GraphQL API. An address maps to at most one
/// reverse record (the name its owner chose to publish), so this returns zero or one name.
/// </summary>
public sealed class TezosDomainsService(Uri endpoint, HttpClient? http = null)
{
    public sealed class DomainsError(string detail) : Exception($"Tezos Domains returned an unexpected response: {detail}");

    public Uri Endpoint { get; } = endpoint;
    private readonly HttpClient http = http ?? Http.Shared;

    private async Task<JsonObject> Query(string query, JsonObject variables)
    {
        var body = new JsonObject { ["query"] = query, ["variables"] = variables }.ToJsonString();
        var reply = await Http.PostJSON(http, Endpoint, body, TimeSpan.FromSeconds(15));
        if (!reply.IsSuccess) throw new DomainsError($"HTTP {reply.Status}");
        try { return JsonNode.Parse(reply.Body) as JsonObject ?? throw new DomainsError("not a JSON object"); }
        catch (JsonException e) { throw new DomainsError(e.Message); }
    }

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>The published <c>.tez</c> name for <c>address</c>, or <c>null</c> when it has none.</summary>
    public async Task<string?> ReverseName(Address address)
    {
        var response = await Query("query($address: String!) { reverseRecord(address: $address) { domain { name } } }",
                                   new JsonObject { ["address"] = address.Value });
        if (response["errors"] is JsonArray { Count: > 0 } errors)
            throw new DomainsError(string.Join("; ", errors.Select(e => Str(e?["message"]))));
        return Str(response["data"]?["reverseRecord"]?["domain"]?["name"]);
    }

    /// <summary>The address a <c>.tez</c> name points at, or <c>null</c> if the name is unregistered or has no address.</summary>
    public async Task<Address?> Resolve(string name)
    {
        var response = await Query("query($name: String!) { domain(name: $name) { address } }",
                                   new JsonObject { ["name"] = name.ToLowerInvariant() });
        return Str(response["data"]?["domain"]?["address"]) is { } address ? new Address(address) : null;
    }
}
