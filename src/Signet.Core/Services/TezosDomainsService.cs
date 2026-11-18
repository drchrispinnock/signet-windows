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
        var reply = await Http.PostJSON(http, Endpoint, body, TimeSpan.FromSeconds(15), 16 << 10, BoundedHTTP.JSONLimits.Small);
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

    /// <summary>The API answers at most 50 names a request, so the list is read in pages of 50.</summary>
    public const int PageSize = 50;
    /// <summary>Pages read before stopping: 500 names of each kind, more than any account has.</summary>
    public const int MaximumPages = 10;
    /// <summary>One page of up to 100 names with their owner, address and expiry.</summary>
    public const int ListBytes = 256 << 10;

    /// <summary>Every name <c>address</c> owns or that points at it, its reverse record first, then by name.</summary>
    public async Task<IReadOnlyList<TezosDomain>> Domains(Address address)
    {
        var items = new List<ListItem>();
        string? reverse = null, ownedAfter = null, pointingAfter = null;
        bool ownedDone = false, pointingDone = false;
        for (var i = 0; i < MaximumPages && !(ownedDone && pointingDone); i++)
        {
            var page = ParsePage(await FetchPage(address, ownedAfter, pointingAfter, owned: !ownedDone, pointing: !pointingDone));
            items.AddRange(page.Items);
            reverse ??= page.Reverse;
            ownedDone = ownedDone || page.OwnedNext is null;
            pointingDone = pointingDone || page.PointingNext is null;
            ownedAfter = page.OwnedNext ?? ownedAfter;
            pointingAfter = page.PointingNext ?? pointingAfter;
        }
        return Merge(items, reverse);
    }

    private async Task<string> FetchPage(Address address, string? ownedAfter, string? pointingAfter, bool owned, bool pointing)
    {
        const string fields = "items { name address owner expiresAtUtc } pageInfo { hasNextPage endCursor }";
        const string order = "order: { field: NAME, direction: ASC }";
        var query = $$"""
            query($a: Address!, $s: String!, $ownedAfter: String, $pointingAfter: String, $owned: Boolean!, $pointing: Boolean!) {
              owned: domains(where: { owner: { equalTo: $a } }, {{order}}, first: {{PageSize}}, after: $ownedAfter) @include(if: $owned) { {{fields}} }
              pointing: domains(where: { address: { equalTo: $a } }, {{order}}, first: {{PageSize}}, after: $pointingAfter) @include(if: $pointing) { {{fields}} }
              reverseRecord(address: $s) { domain { name } }
            }
            """;
        var variables = new JsonObject
        {
            ["a"] = address.Value, ["s"] = address.Value, ["ownedAfter"] = ownedAfter, ["pointingAfter"] = pointingAfter,
            ["owned"] = owned, ["pointing"] = pointing,
        };
        var body = new JsonObject { ["query"] = query, ["variables"] = variables }.ToJsonString();
        var reply = await Http.PostJSON(http, Endpoint, body, TimeSpan.FromSeconds(20), ListBytes, BoundedHTTP.JSONLimits.Page);
        if (!reply.IsSuccess) throw new DomainsError($"HTTP {reply.Status}");
        return System.Text.Encoding.UTF8.GetString(reply.Body);
    }

    /// <summary>One name in the API's reply.</summary>
    public sealed record ListItem(string? Name, string? Address, string? Owner, string? ExpiresAtUtc);

    /// <summary>
    /// One page of the API's reply: its names, the cursor to each list's next page (<c>null</c> when
    /// there is none) and the account's reverse record.
    /// </summary>
    public sealed record Page(IReadOnlyList<ListItem> Items, string? OwnedNext, string? PointingNext, string? Reverse);

    public static Page ParsePage(string data)
    {
        JsonObject response;
        try { response = JsonNode.Parse(data) as JsonObject ?? throw new DomainsError("not a JSON object"); }
        catch (JsonException e) { throw new DomainsError(e.Message); }
        if (response["errors"] is JsonArray { Count: > 0 } errors)
            throw new DomainsError(string.Join("; ", errors.Select(e => Str(e?["message"]))));
        var payload = response["data"] as JsonObject;
        static IEnumerable<ListItem> ItemsOf(JsonNode? list) =>
            (list?["items"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(i => new ListItem(Str(i["name"]), Str(i["address"]), Str(i["owner"]), Str(i["expiresAtUtc"])));
        static string? Next(JsonNode? list) =>
            list?["pageInfo"]?["hasNextPage"] is JsonValue v && v.TryGetValue<bool>(out var more) && more ? Str(list["pageInfo"]?["endCursor"]) : null;
        return new Page([.. ItemsOf(payload?["owned"]), .. ItemsOf(payload?["pointing"])],
                        Next(payload?["owned"]), Next(payload?["pointing"]),
                        Str(payload?["reverseRecord"]?["domain"]?["name"]));
    }

    /// <summary>The Domains tab's list from one reply (tests and single pages).</summary>
    public static IReadOnlyList<TezosDomain> ParseDomains(string data)
    {
        var page = ParsePage(data);
        return Merge(page.Items, page.Reverse);
    }

    /// <summary>Owned and pointing names as one list, each name once, the reverse record first.</summary>
    public static IReadOnlyList<TezosDomain> Merge(IEnumerable<ListItem> items, string? reverse)
    {
        var byName = new Dictionary<string, TezosDomain>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item.Name is not { Length: > 0 } name || byName.ContainsKey(name)) continue;
            DateTimeOffset? expires = DateTimeOffset.TryParse(item.ExpiresAtUtc, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var date) ? date : null;
            byName[name] = new TezosDomain(name, item.Address is { } a ? new Address(a) : null, item.Owner is { } o ? new Address(o) : null,
                                           expires, name == reverse);
        }
        return byName.Values.OrderBy(d => d.IsReverseRecord ? 0 : 1).ThenBy(d => d.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>The address a <c>.tez</c> name points at, or <c>null</c> if the name is unregistered or has no address.</summary>
    public async Task<Address?> Resolve(string name)
    {
        var response = await Query("query($name: String!) { domain(name: $name) { address } }",
                                   new JsonObject { ["name"] = name.ToLowerInvariant() });
        return Str(response["data"]?["domain"]?["address"]) is { } address ? new Address(address) : null;
    }
}
