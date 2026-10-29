using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Signet.Models;

namespace Signet.Services;

/// <summary>Token balances from the TzKT indexer, and the NFTs picked out of them.</summary>
public sealed class TzKTService(Uri baseURL, HttpClient? http = null)
{
    public Uri BaseURL { get; } = baseURL;
    private readonly HttpClient http = http ?? Http.Shared;

    /// <summary>One row of <c>/v1/tokens/balances</c>.</summary>
    public sealed record TokenBalance(string Balance, TokenBalance.TokenInfo Token)
    {
        public sealed record TokenInfo(TokenInfo.ContractInfo Contract, string TokenId, string? Standard, TokenInfo.MetadataInfo? Metadata)
        {
            public sealed record ContractInfo(string Address);

            /// <param name="Icon">Fungible tokens often put their logo here instead of thumbnailUri.</param>
            public sealed record MetadataInfo(
                string? Name, string? Symbol, string? Decimals, string? ThumbnailUri, string? DisplayUri, string? ArtifactUri,
                string? Icon, IReadOnlyList<MetadataInfo.Format>? Formats)
            {
                public sealed record Format(string? Uri, string? MimeType);

                /// <summary>
                /// Decimals come back as a string in TZIP-21 metadata. Only a whole number in
                /// 0...36 counts; anything else is treated as missing, so a token cannot claim
                /// nonsensical scaling (S26).
                /// </summary>
                public int? DecimalPlaces =>
                    int.TryParse(Decimals?.Trim(' '), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var d) && d is >= 0 and <= 36 ? d : null;
            }
        }
    }

    private static readonly JsonSerializerOptions DecodeOptions = new(JsonSerializerDefaults.Web);

    private async Task<byte[]> GetJSON(Uri url, TimeSpan timeout)
    {
        var reply = await Http.Get(http, url, timeout);
        if (!reply.IsSuccess) throw new HttpRequestException($"TzKT returned HTTP {reply.Status}");
        return reply.Body;
    }

    public const int PageSize = 200;
    /// <summary>At most this many pages per snapshot; beyond that the portfolio is reported as partial.</summary>
    public const int MaxPages = 5;

    /// <summary>One page of the tokens the account holds a positive balance of, most recently changed first.</summary>
    public async Task<IReadOnlyList<TokenBalance>> TokenBalancesFor(Address address, int limit = PageSize, int offset = 0)
    {
        var url = BaseURL.AppendingPathComponent("v1/tokens/balances").WithQuery(
            ("account", address.Value), ("balance.gt", "0"), ("limit", limit.ToString(CultureInfo.InvariantCulture)),
            ("offset", offset.ToString(CultureInfo.InvariantCulture)), ("sort.desc", "lastLevel"));
        return Decode(await GetJSON(url, TimeSpan.FromSeconds(20))).Take(limit).ToList();
    }

    public static IReadOnlyList<TokenBalance> Decode(byte[] data) =>
        JsonSerializer.Deserialize<List<TokenBalance>>(data, DecodeOptions) ?? [];

    /// <summary>Every page up to <c>MaxPages</c>, and whether that was all of them (S26).</summary>
    public async Task<(IReadOnlyList<TokenBalance> Rows, bool Complete)> AllTokenBalancesFor(Address address)
    {
        var rows = new List<TokenBalance>();
        for (var page = 0; page < MaxPages; page++)
        {
            var batch = await TokenBalancesFor(address, PageSize, page * PageSize);
            rows.AddRange(batch);
            if (batch.Count < PageSize) return (rows, true);
        }
        return (rows, false);
    }

    /// <summary>Tokens and NFTs from one snapshot, marked partial when the account holds more than was fetched.</summary>
    public async Task<Portfolio> PortfolioFor(Address address)
    {
        var (rows, complete) = await AllTokenBalancesFor(address);
        return new Portfolio(FungibleTokens(rows), Nfts(rows),
                             complete ? new Portfolio.PortfolioState.Complete() : new Portfolio.PortfolioState.Partial(rows.Count));
    }

    /// <summary>NFTs are tokens with no decimal places and some image to show.</summary>
    public async Task<IReadOnlyList<NFT>> NftsFor(Address address) => Nfts((await AllTokenBalancesFor(address)).Rows);

    /// <summary>
    /// Fungible (DeFi) tokens: anything with decimal places, or an FA1.2 token (always fungible).
    /// Tokens with zero decimals and no image, such as Tezos Domains, are neither NFT nor asset.
    /// </summary>
    public async Task<IReadOnlyList<AssetBalance>> FungibleTokensFor(Address address) => FungibleTokens((await AllTokenBalancesFor(address)).Rows);

    public static IReadOnlyList<AssetBalance> FungibleTokens(IReadOnlyList<TokenBalance> balances) =>
        balances.Select(row =>
        {
            var metadata = row.Token.Metadata;
            var isFA12 = row.Token.Standard?.ToLowerInvariant() == "fa1.2";
            // A token whose decimals are unreadable is not scaled by a guess: FA1.2 is taken as 0, anything else is skipped.
            if ((metadata?.DecimalPlaces ?? (isFA12 ? 0 : null)) is not { } decimals) return null;
            if (decimals <= 0 && !isFA12) return null;
            if (Scaled(row.Balance, decimals) is not { } amount) return null;
            var contract = row.Token.Contract.Address;
            var symbol = NonEmpty(metadata?.Symbol?.Trim());
            var name = NonEmpty(metadata?.Name?.Trim());
            var icons = new[] { metadata?.ThumbnailUri, metadata?.Icon, metadata?.DisplayUri }.OfType<string>().SelectMany(IPFS.CandidateURLs).ToList();
            return new AssetBalance(
                $"{contract}:{row.Token.TokenId}",
                new AssetBalance.AssetKind.Token(contract, row.Token.TokenId),
                name ?? symbol ?? new Address(contract).Shortened(),
                symbol ?? name ?? "",
                amount)
            {
                IconURLs = icons,
                Standard = row.Token.Standard,
            };
        })
        .OfType<AssetBalance>()
        .OrderByDescending(a => a.Amount)
        .ToList();

    /// <summary>
    /// <c>raw / 10^decimals</c> exactly, by moving the decimal point in the integer string
    /// (C# <c>decimal</c> cannot hold 10^decimals for large token decimals).
    /// </summary>
    private static decimal? Scaled(string raw, int decimals)
    {
        if (raw.Length == 0 || !raw.All(char.IsAsciiDigit)) return null;
        var text = raw;
        if (decimals > 0)
        {
            text = text.PadLeft(decimals + 1, '0');
            text = $"{text[..^decimals]}.{text[^decimals..]}";
        }
        return decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static string? NonEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    public static IReadOnlyList<NFT> Nfts(IReadOnlyList<TokenBalance> balances) =>
        balances.Select(row =>
        {
            if (row.Token.Metadata is not { DecimalPlaces: 0 } metadata) return null;
            var images = BestImageURLs(metadata);
            if (images.Count == 0) return null;
            return new NFT(
                $"{row.Token.Contract.Address}:{row.Token.TokenId}",
                row.Token.Contract.Address,
                row.Token.TokenId,
                NonEmpty(metadata.Name?.Trim()) ?? $"#{row.Token.TokenId}",
                decimal.TryParse(row.Balance, NumberStyles.Number, CultureInfo.InvariantCulture, out var b) ? b : 1,
                images);
        })
        .OfType<NFT>()
        .ToList();

    /// <summary>
    /// Every URL worth trying for the tile, best first: displayUri is a sized-down render meant
    /// for galleries, so it comes first (one URL per gateway); then thumbnailUri, often a
    /// marketplace's generic placeholder but small, so it is the fallback when the display
    /// render is too large to download (<c>ImageLoader.MaxBytes</c>) or missing from every gateway;
    /// then artifactUri, the full asset, only if it is an image.
    /// </summary>
    public static IReadOnlyList<Uri> BestImageURLs(TokenBalance.TokenInfo.MetadataInfo metadata)
    {
        var uris = new[] { metadata.DisplayUri, metadata.ThumbnailUri }.OfType<string>().ToList();
        if (metadata.ArtifactUri is { } artifact)
        {
            var mime = metadata.Formats?.FirstOrDefault(f => f.Uri == artifact)?.MimeType ?? "";
            if (mime.StartsWith("image/", StringComparison.Ordinal)) uris.Add(artifact);
        }
        var urls = new List<Uri>();
        foreach (var uri in uris)
            foreach (var url in IPFS.CandidateURLs(uri))
                if (!urls.Contains(url)) urls.Add(url);
        return urls;
    }

    // ---- Profiles --------------------------------------------------------------------------------

    /// <summary>TzProfiles data as TzKT indexes it (<c>extras.profile</c>), falling back to TzKT's own alias.</summary>
    public async Task<AccountProfile?> AccountProfileFor(Address address)
    {
        var reply = await Http.Get(http, BaseURL.AppendingPathComponent($"v1/accounts/{address.Value}"), TimeSpan.FromSeconds(15));
        if (reply.Status is 204 or 404) return null;
        if (!reply.IsSuccess) throw new HttpRequestException($"TzKT returned HTTP {reply.Status}");
        return Profile(reply.Body);
    }

    public static AccountProfile? Profile(byte[] data)
    {
        JsonObject? obj;
        try { obj = JsonNode.Parse(data) as JsonObject; }
        catch (JsonException) { return null; }
        if (obj is null) return null;
        var profile = (obj["extras"] as JsonObject)?["profile"] as JsonObject;
        var name = Str(profile?["alias"]) ?? Str(obj["alias"]);
        var twitter = Str(profile?["twitter"]);
        var description = Str(profile?["description"]);
        if (name is null && twitter is null && description is null) return null;
        return new AccountProfile(name?.Trim(' ', '\t'), twitter, description);
    }

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static decimal Dec(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<decimal>(out var d) ? d : 0;

    // ---- History ---------------------------------------------------------------------------------

    /// <summary>The account's most recent transactions and delegations, newest first.</summary>
    public async Task<IReadOnlyList<TezosTransaction>> RecentOperationsFor(Address address, int limit = 25)
    {
        var url = BaseURL.AppendingPathComponent($"v1/accounts/{address.Value}/operations").WithQuery(
            ("type", "transaction,delegation,origination"), ("limit", limit.ToString(CultureInfo.InvariantCulture)), ("sort.desc", "id"));
        return ParseOperations(await GetJSON(url, TimeSpan.FromSeconds(20)), address).Take(limit).ToList();
    }

    /// <summary>Pure parser for <c>/v1/accounts/{address}/operations</c>.</summary>
    public static IReadOnlyList<TezosTransaction> ParseOperations(byte[] data, Address address)
    {
        JsonArray? rows;
        try { rows = JsonNode.Parse(data) as JsonArray; }
        catch (JsonException) { return []; }
        if (rows is null) return [];
        var result = new List<TezosTransaction>();
        foreach (var row in rows.OfType<JsonObject>())
        {
            if (Str(row["type"]) is not { } type || Str(row["hash"]) is not { } hash || Str(row["timestamp"]) is not { } stamp ||
                !DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp))
                continue;
            var sender = row["sender"] as JsonObject;
            var senderAddress = Str(sender?["address"]) ?? "";
            var kind = type switch
            {
                "transaction" => TezosTransaction.TransactionKind.Transaction,
                "delegation" => TezosTransaction.TransactionKind.Delegation,
                "origination" => TezosTransaction.TransactionKind.Origination,
                _ => TezosTransaction.TransactionKind.Other,
            };
            var other = kind switch
            {
                TezosTransaction.TransactionKind.Transaction => row["target"] as JsonObject,
                TezosTransaction.TransactionKind.Delegation => row["newDelegate"] as JsonObject,
                TezosTransaction.TransactionKind.Origination => row["originatedContract"] as JsonObject,
                _ => null,
            };
            // An internal operation a contract emitted because we called it (a multisig paying
            // out) is ours: the indexer lists it under our account with the contract as sender
            // and us as initiator, so it must not read as money coming in from the contract.
            var initiator = Str((row["initiator"] as JsonObject)?["address"]);
            var initiatedByUs = senderAddress != address.Value && initiator == address.Value;
            var outgoing = senderAddress == address.Value || initiatedByUs;
            var direction = outgoing
                ? (Str(other?["address"]) == address.Value ? TezosTransaction.TransactionDirection.SelfTransfer : TezosTransaction.TransactionDirection.Outgoing)
                : TezosTransaction.TransactionDirection.Incoming;
            var counterpartyObject = outgoing ? other : sender;
            var mutez = Dec(row["amount"]);
            var feeMutez = Dec(row["bakerFee"]);
            var entrypoint = Str((row["parameter"] as JsonObject)?["entrypoint"]) ?? Str(row["entrypoint"]);
            var id = row["id"] is JsonValue idValue && idValue.TryGetValue<long>(out var n) ? n.ToString(CultureInfo.InvariantCulture) : hash;
            // On a delegation `amount` is the delegator's balance, not a transfer.
            var isDelegation = kind == TezosTransaction.TransactionKind.Delegation;
            var tx = new TezosTransaction(
                id, hash, row["level"] is JsonValue lv && lv.TryGetValue<int>(out var level) ? level : 0, timestamp,
                kind, direction,
                Str(counterpartyObject?["address"]) is { } cp ? new Address(cp) : null,
                Str(counterpartyObject?["alias"]),
                isDelegation ? 0 : mutez / Mutez.PerTez, feeMutez / Mutez.PerTez,
                entrypoint,
                (Str(row["status"]) ?? "applied") == "applied");
            if (isDelegation)
            {
                tx = tx with
                {
                    PreviousDelegate = Str((row["prevDelegate"] as JsonObject)?["address"]) is { } prev ? new Address(prev) : null,
                    NewDelegate = Str((row["newDelegate"] as JsonObject)?["address"]) is { } next ? new Address(next) : null,
                    DelegatedBalance = mutez / Mutez.PerTez,
                    DelegationTarget = address,
                };
            }
            if (initiatedByUs) tx = tx with { Via = new Address(senderAddress) };
            result.Add(tx);
        }
        return result;
    }

    // ---- Bakers ----------------------------------------------------------------------------------

    /// <summary>Active bakers, most delegators first.</summary>
    public async Task<IReadOnlyList<BakerCandidate>> Bakers(int limit)
    {
        var url = BaseURL.AppendingPathComponent("v1/delegates").WithQuery(
            ("active", "true"), ("sort.desc", "numDelegators"), ("limit", limit.ToString(CultureInfo.InvariantCulture)),
            ("select", "address,alias,stakingBalance,numDelegators,stakersCount,limitOfStakingOverBaking"));
        return ParseBakers(await GetJSON(url, TimeSpan.FromSeconds(20))).Take(limit).ToList();
    }

    public static IReadOnlyList<BakerCandidate> ParseBakers(byte[] data)
    {
        JsonArray? rows;
        try { rows = JsonNode.Parse(data) as JsonArray; }
        catch (JsonException) { return []; }
        if (rows is null) return [];
        return rows.OfType<JsonObject>().Select(row =>
        {
            if (Str(row["address"]) is not { } address) return null;
            var limit = row["limitOfStakingOverBaking"] is JsonValue lv && lv.TryGetValue<decimal>(out var l) ? l : (decimal?)null;
            return new BakerCandidate(
                new Address(address), Str(row["alias"]),
                Dec(row["stakingBalance"]) / Mutez.PerTez,
                (int)Dec(row["numDelegators"]),
                (int)Dec(row["stakersCount"]),
                limit is null ? null : limit > 0);
        }).OfType<BakerCandidate>().ToList();
    }
}
