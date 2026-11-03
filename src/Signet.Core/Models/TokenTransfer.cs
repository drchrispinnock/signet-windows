using System.Text.Json;
using System.Text.Json.Serialization;
using Signet.Services;

namespace Signet.Models;

/// <summary>
/// One FA2 token movement: <c>Amount</c> editions of <c>TokenId</c> at <c>Contract</c>, both as the decimal
/// text of a nat, as the bridge's <c>tokenTransferParams</c> reads them.
/// </summary>
public sealed record TokenTransfer(
    [property: JsonPropertyName("contract")] string Contract,
    [property: JsonPropertyName("tokenId")] string TokenId,
    [property: JsonPropertyName("amount")] string Amount)
{
    public TokenTransfer(NFT nft, int amount) : this(nft.Contract, nft.TokenId, Math.Max(0, amount).ToString(System.Globalization.CultureInfo.InvariantCulture)) { }

    public static bool IsNat(string text) =>
        text.Length is > 0 and <= 40 && text.All(char.IsAsciiDigit) && (text == "0" || !text.StartsWith('0'));

    [JsonIgnore]
    public bool IsValid => new Address(Contract) is { IsContract: true, IsValidAccount: true } && IsNat(TokenId) && IsNat(Amount) && Amount != "0";
}

/// <summary>
/// Tokens going from the selected account to one recipient in one operation group: the bridge
/// makes one FA2 <c>transfer</c> call per contract (<c>Contracts</c>, in order of first appearance).
/// A burn is the same request to <c>BurnAddress</c>.
/// </summary>
public sealed record TokenTransferRequest(Address To, IReadOnlyList<TokenTransfer> Transfers)
{
    /// <summary>The address marketplaces burn to: no key hashes to it, so tokens sent there never move again.</summary>
    public static readonly Address BurnAddress = new("tz1burnburnburnburnburnburnburjAYjjX");
    /// <summary>At most this many tokens in one transfer (the bridge refuses more).</summary>
    public const int MaxTokens = 100;

    public bool IsBurn => To == BurnAddress;
    public int Count => Transfers.Count;
    /// <summary>The contracts called, one operation each, in the order they first appear.</summary>
    public IReadOnlyList<string> Contracts => Transfers.Select(t => t.Contract).Distinct().ToList();
    public bool IsValid => To.IsValidAccount && Transfers.Count is > 0 and <= MaxTokens && Transfers.All(t => t.IsValid);

    /// <summary>As the bridge's <c>tokenTransferParams</c> reads the transfers (keys sorted).</summary>
    public string TransfersJSON => JsonSerializer.Serialize(Transfers.Select(t =>
        new SortedDictionary<string, string>(StringComparer.Ordinal) { ["amount"] = t.Amount, ["contract"] = t.Contract, ["tokenId"] = t.TokenId }));

    public bool Equals(TokenTransferRequest? other) => other is not null && To == other.To && Transfers.SequenceEqual(other.Transfers);
    public override int GetHashCode() => HashCode.Combine(To, Transfers.Count);
}

/// <summary>
/// The fee, gas and storage each operation of a batch was approved with, in order, plus the
/// reveal the node will prepend for an unrevealed account: <c>OperationLimits</c> for an operation
/// group (S14). The bridge's <c>parseBatchLimits</c> / <c>checkApprovedBatch</c> enforce them.
/// </summary>
public sealed record BatchLimits(IReadOnlyList<BatchLimits.Operation> Operations,
                                 string? RevealFeeMutez = null, int? RevealGasLimit = null, int? RevealStorageLimit = null)
{
    public sealed record Operation(string FeeMutez, int GasLimit, int StorageLimit);

    /// <summary>As the bridge's <c>parseBatchLimits</c> reads them: camelCase keys, sorted, absent reveal fields left out.</summary>
    public string Json
    {
        get
        {
            var members = new SortedDictionary<string, object>(StringComparer.Ordinal)
            {
                ["operations"] = Operations.Select(o => new SortedDictionary<string, object>(StringComparer.Ordinal)
                {
                    ["feeMutez"] = o.FeeMutez, ["gasLimit"] = o.GasLimit, ["storageLimit"] = o.StorageLimit,
                }).ToList(),
            };
            if (RevealFeeMutez is not null) members["revealFeeMutez"] = RevealFeeMutez;
            if (RevealGasLimit is not null) members["revealGasLimit"] = RevealGasLimit;
            if (RevealStorageLimit is not null) members["revealStorageLimit"] = RevealStorageLimit;
            return JsonSerializer.Serialize(members);
        }
    }

    /// <summary>From a bridge estimate's <c>limits</c> object; null when any figure is missing or malformed.</summary>
    public static BatchLimits? From(JSONValue? value)
    {
        if (value?["operations"]?.ArrayValue is not { Count: > 0 } rows) return null;
        var operations = new List<Operation>();
        foreach (var row in rows)
        {
            if (row["feeMutez"]?.StringValue is not { } fee || !Mutez.IsValid(fee) ||
                row["gasLimit"]?.IntValue is not { } gas || row["storageLimit"]?.IntValue is not { } storage) return null;
            operations.Add(new Operation(fee, gas, storage));
        }
        var reveal = value["revealFeeMutez"]?.StringValue;
        if (reveal is not null && !Mutez.IsValid(reveal)) return null;
        return new BatchLimits(operations, reveal, value["revealGasLimit"]?.IntValue, value["revealStorageLimit"]?.IntValue);
    }

    public bool Equals(BatchLimits? other) =>
        other is not null && Operations.SequenceEqual(other.Operations) && RevealFeeMutez == other.RevealFeeMutez &&
        RevealGasLimit == other.RevealGasLimit && RevealStorageLimit == other.RevealStorageLimit;
    public override int GetHashCode() => HashCode.Combine(Operations.Count, RevealFeeMutez);
}

/// <summary>What a token transfer will cost, in tez, and the exact limits it must be sent with.</summary>
/// <param name="Fee">The whole fee, the reveal included when the account needs one.</param>
/// <param name="Burn">Storage burn: the contract's ledger grows when the recipient has never held the token.</param>
public sealed record TokenTransferEstimate(decimal Fee, decimal Burn, BatchLimits Limits)
{
    public decimal Total => Fee + Burn;
}

/// <summary>An operation the NFT grid's context menu starts on one or more tokens; what the sheet opens with.</summary>
public sealed record NFTOperation(NFTOperation.OperationKind Kind, IReadOnlyList<NFT> Nfts)
{
    public enum OperationKind { Transfer, Burn }

    public string Id => $"{(Kind == OperationKind.Transfer ? "transfer" : "burn")}:" + string.Join(",", Nfts.Select(n => n.Id));
    /// <summary>Every token sits on the same layer (the menu refuses a selection spanning both).</summary>
    public int Layer => Nfts.FirstOrDefault()?.Layer ?? 1;

    public bool Equals(NFTOperation? other) => other is not null && Kind == other.Kind && Nfts.SequenceEqual(other.Nfts);
    public override int GetHashCode() => HashCode.Combine(Kind, Nfts.Count);
}
