using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Signet.Models;

namespace Signet.Services;

using Metadata = TzKTService.TokenBalance.TokenInfo.MetadataInfo;

/// <summary>
/// Token metadata Signet reads for itself when the indexer has none. TzKT normally resolves a
/// token's TZIP-21 document within minutes of its mint, but an indexer without a metadata
/// resolver (Etherlink's TzKT, October 2026) or a very fresh mint leaves <c>metadata</c> null, and a
/// token without metadata is neither an asset nor an NFT to Signet. So for such rows the
/// contract's <c>token_metadata</c> big map is read through the same indexer (TZIP-12: a
/// <c>token_info</c> map of bytes; its <c>""</c> entry is the URI of the document, the others override
/// fields of it), the document is fetched from IPFS gateways or a public HTTPS address with
/// the same bounds as artwork (S13, S19), and the result is classified as if TzKT had sent it.
/// Resolved metadata is cached for the process; a miss is retried after a while.
/// </summary>
/// <param name="Scope">Keeps one indexer's tokens apart from another's in the cache.</param>
/// <param name="TokenInfo">The hex-decoded <c>token_info</c> of the contract's <c>token_metadata</c> entry, or null without one.</param>
/// <param name="Document">The document at a URL from <c>DocumentURLs</c>, or null when it cannot be read.</param>
public sealed record TokenMetadataResolver(
    string Scope,
    Func<string, string, Task<IReadOnlyDictionary<string, string>?>> TokenInfo,
    Func<Uri, Task<byte[]?>> Document)
{
    /// <summary>Rows resolved per portfolio load beyond what the cache answers; the rest wait for the next refresh.</summary>
    public const int MaxPerLoad = 12;
    /// <summary>A TZIP-21 document is a few hundred bytes; a page of attributes still fits comfortably.</summary>
    public const int MaxDocumentBytes = 256 << 10;
    public static readonly TimeSpan MissLifetime = TimeSpan.FromMinutes(10);

    public static TokenMetadataResolver Live(TzKTService tzkt) =>
        new(tzkt.BaseURL.AbsoluteString(), tzkt.TokenInfo, FetchDocument);

    /// <summary><c>rows</c> with metadata filled in where the indexer had none and it could be read.</summary>
    public async Task<IReadOnlyList<TzKTService.TokenBalance>> Fill(IReadOnlyList<TzKTService.TokenBalance> rows)
    {
        var result = rows.ToList();
        var budget = MaxPerLoad;
        for (var i = 0; i < result.Count; i++)
        {
            if (result[i].Token.Metadata is not null) continue;
            var token = result[i].Token;
            var key = $"{Scope}|{token.Contract.Address}:{token.TokenId}";
            if (TokenMetadataCache.Shared.Lookup(key) is { } cached)
            {
                result[i] = result[i] with { Token = token with { Metadata = cached.Metadata } };
                continue;
            }
            if (budget <= 0) continue;
            budget--;
            var metadata = await Resolve(token.Contract.Address, token.TokenId);
            TokenMetadataCache.Shared.Store(metadata, key);
            result[i] = result[i] with { Token = token with { Metadata = metadata } };
        }
        return result;
    }

    /// <summary>Reads the big map entry and its document; null when either the entry is missing or nothing in it describes the token.</summary>
    public async Task<Metadata?> Resolve(string contract, string tokenId)
    {
        if (!IsContract(contract) || !IsTokenID(tokenId)) return null;
        IReadOnlyDictionary<string, string>? info;
        try { info = await TokenInfo(contract, tokenId); }
        catch (Exception) { return null; }
        if (info is null) return null;
        byte[]? document = null;
        if (info.TryGetValue("", out var uri))
            foreach (var url in DocumentURLs(uri))
            {
                try { if (await Document(url) is { } data) { document = data; break; } }
                catch (Exception) { }
            }
        return MetadataFrom(info, document);
    }

    /// <summary>
    /// Where a <c>token_info</c> URI can be fetched from: every gateway for <c>ipfs://</c>, the address
    /// itself for public HTTPS, nothing for anything else (<c>tezos-storage:</c> is not followed).
    /// </summary>
    public static IReadOnlyList<Uri> DocumentURLs(string uri)
    {
        var trimmed = uri.Trim();
        if (trimmed.StartsWith("ipfs://", StringComparison.OrdinalIgnoreCase)) return IPFS.CandidateURLs(trimmed);
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var url) && EndpointPolicy.IsPublicHTTPS(url) ? [url] : [];
    }

    /// <summary>
    /// The document's fields, overridden by any the big map states directly (TZIP-12 lets a
    /// contract put <c>name</c>, <c>symbol</c>, <c>decimals</c> and the URIs straight into <c>token_info</c>).
    /// null when nothing usable results.
    /// </summary>
    public static Metadata? MetadataFrom(IReadOnlyDictionary<string, string> info, byte[]? document)
    {
        var metadata = (document is null ? null : DecodeDocument(document)) ?? new Metadata(null, null, null, null, null, null, null, null);
        string? Direct(string key) => info.TryGetValue(key, out var v) && v.Trim() is { Length: > 0 } t ? t : null;
        metadata = metadata with
        {
            Name = Direct("name") ?? metadata.Name,
            Symbol = Direct("symbol") ?? metadata.Symbol,
            Decimals = Direct("decimals") ?? metadata.Decimals,
            ThumbnailUri = Direct("thumbnailUri") ?? metadata.ThumbnailUri,
            DisplayUri = Direct("displayUri") ?? metadata.DisplayUri,
            ArtifactUri = Direct("artifactUri") ?? metadata.ArtifactUri,
            Icon = Direct("icon") ?? metadata.Icon,
        };
        return metadata.IsEmpty ? null : metadata;
    }

    /// <summary>A TZIP-21 document as the indexer would have reported it; null unless it is a JSON object within the usual budget.</summary>
    public static Metadata? DecodeDocument(byte[] data)
    {
        if (data.Length > MaxDocumentBytes || !JSONBudget.Accepts(data, 32, 4_000, 64 << 10)) return null;
        try { return JsonNode.Parse(data) is JsonObject ? JsonSerializer.Deserialize<Metadata>(data, TzKTService.DecodeOptions) : null; }
        catch (JsonException) { return null; }
    }

    /// <summary>Fetches a document through the pinned public client, as artwork is fetched (S13).</summary>
    public static async Task<byte[]?> FetchDocument(Uri url)
    {
        if (!EndpointPolicy.IsPublicHTTPS(url)) return null;
        var reply = await PublicHTTPClient.Data(url, MaxDocumentBytes, followRedirects: true);
        return reply.Status is >= 200 and < 300 && reply.Data.Length <= MaxDocumentBytes ? reply.Data : null;
    }

    public static bool IsContract(string address) =>
        address.StartsWith("KT1", StringComparison.Ordinal) && address.Length == 36 && address.All(char.IsAsciiLetterOrDigit);

    public static bool IsTokenID(string id) => id.Length is > 0 and <= 80 && id.All(char.IsAsciiDigit);
}

/// <summary>Metadata resolved this process, hits and misses, so a refresh asks for each token once.</summary>
public sealed class TokenMetadataCache
{
    public static readonly TokenMetadataCache Shared = new();

    public sealed record Entry(Metadata? Metadata, DateTimeOffset Date);

    private readonly Dictionary<string, Entry> entries = [];
    private readonly Lock gate = new();
    private const int Limit = 2_000;

    public Entry? Lookup(string key)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(key, out var entry)) return null;
            if (entry.Metadata is null && DateTimeOffset.UtcNow - entry.Date > TokenMetadataResolver.MissLifetime)
            {
                entries.Remove(key);
                return null;
            }
            return entry;
        }
    }

    public void Store(Metadata? metadata, string key)
    {
        lock (gate)
        {
            if (entries.Count >= Limit) entries.Clear();
            entries[key] = new Entry(metadata, DateTimeOffset.UtcNow);
        }
    }
}

/// <summary>
/// Validate resource bounds before JSON parsing and recursive schema processing (S19).
/// Syntax remains the parser's responsibility; scanning bytes also bounds long strings without
/// allocating a decoded object.
/// </summary>
public static class JSONBudget
{
    public static bool Accepts(ReadOnlySpan<byte> data, int depthLimit = 128, int nodeLimit = 250_000, int stringLimit = 1 << 20)
    {
        int depth = 0, nodes = 0, stringBytes = 0;
        bool quoted = false, escaped = false;
        foreach (var b in data)
        {
            if (quoted)
            {
                if (++stringBytes > stringLimit) return false;
                if (escaped) escaped = false;
                else if (b == 92) escaped = true;
                else if (b == 34) quoted = false;
            }
            else
            {
                switch (b)
                {
                    case 34: quoted = true; stringBytes = 0; break;
                    case 91 or 123:
                        depth++; nodes++;
                        if (depth > depthLimit || nodes > nodeLimit) return false;
                        break;
                    case 93 or 125: depth--; break;
                    case 44:
                        if (++nodes > nodeLimit) return false;
                        break;
                }
            }
        }
        return true;
    }
}
