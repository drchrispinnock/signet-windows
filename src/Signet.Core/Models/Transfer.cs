using System.Globalization;
using System.Text.Json;
using Signet.Services;

namespace Signet.Models;

/// <summary>What a transfer will cost, in tez.</summary>
/// <param name="Fee">The whole fee, the reveal included when the account needs one.</param>
/// <param name="Burn">Storage burn, e.g. the allocation cost when the destination has never held tez.</param>
/// <param name="Limits">The exact limits the operation must be sent with; <c>null</c> for estimates no operation can be sent from.</param>
public sealed record TransferEstimate(decimal Fee, decimal Burn, decimal Total, int GasLimit, int StorageLimit, OperationLimits? Limits = null);

/// <summary>
/// The fee, gas and storage an operation was approved with, in mutez and units, plus the reveal
/// the node will prepend for an unrevealed account. The bridge sends with exactly these and
/// refuses anything that would cost more; nothing is re-estimated after approval (S14).
/// </summary>
public sealed record OperationLimits(string FeeMutez, int GasLimit, int StorageLimit,
                                     string? RevealFeeMutez = null, int? RevealGasLimit = null, int? RevealStorageLimit = null)
{
    /// <summary>As the bridge's <c>parseLimits</c> reads them: camelCase keys, sorted, absent reveal fields left out.</summary>
    public string Json
    {
        get
        {
            var members = new SortedDictionary<string, object>(StringComparer.Ordinal)
            {
                ["feeMutez"] = FeeMutez, ["gasLimit"] = GasLimit, ["storageLimit"] = StorageLimit,
            };
            if (RevealFeeMutez is not null) members["revealFeeMutez"] = RevealFeeMutez;
            if (RevealGasLimit is not null) members["revealGasLimit"] = RevealGasLimit;
            if (RevealStorageLimit is not null) members["revealStorageLimit"] = RevealStorageLimit;
            return JsonSerializer.Serialize(members);
        }
    }

    /// <summary>From a bridge estimate's <c>limits</c> object; <c>null</c> when any figure is missing or malformed.</summary>
    public static OperationLimits? From(JSONValue? value)
    {
        if (value?["feeMutez"]?.StringValue is not { } fee || !Mutez.IsValid(fee) ||
            value["gasLimit"]?.IntValue is not { } gas || value["storageLimit"]?.IntValue is not { } storage) return null;
        var reveal = value["revealFeeMutez"]?.StringValue;
        if (reveal is not null && !Mutez.IsValid(reveal)) return null;
        return new OperationLimits(fee, gas, storage, reveal, value["revealGasLimit"]?.IntValue, value["revealStorageLimit"]?.IntValue);
    }
}

/// <summary>
/// An operation that was signed and handed to the node. <c>Injected</c> is false when the node's
/// answer was lost: the operation may or may not be in its mempool, so the right move is to
/// watch the chain for <c>Hash</c> or send <c>SignedBytes</c> again, never to make a new operation (S15).
/// </summary>
public sealed record SentOperation(string Hash, bool Injected = true, string? SignedBytes = null, string? OriginatedAddress = null)
{
    /// <summary>From the bridge's <c>{ hash, injected, signedBytes }</c>.</summary>
    public static SentOperation? From(JSONValue value) =>
        value["hash"]?.StringValue is { Length: > 0 } hash
            ? new SentOperation(hash, value["injected"]?.BoolValue ?? true, value["signedBytes"]?.StringValue, value["address"]?.StringValue)
            : null;
}

/// <summary>Public identity an indexer knows for an address: the TzProfiles data TzKT carries.</summary>
public sealed record AccountProfile(string? Name, string? Twitter, string? Description);

/// <summary>1 tez = 1,000,000 mutez.</summary>
public static class Mutez
{
    /// <summary>A non-negative whole number of mutez as a decimal string, as the bridge reports fees.</summary>
    public static bool IsValid(string text) => text.Length is > 0 and <= 20 && text.All(char.IsAsciiDigit);

    public const decimal PerTez = 1_000_000m;

    public static string FromTez(decimal tez) =>
        decimal.Round(tez * PerTez, 0, MidpointRounding.ToZero).ToString("0", CultureInfo.InvariantCulture);

    public static decimal? ToTez(string? mutez) =>
        decimal.TryParse(mutez, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v / PerTez : null;
}
