using System.Globalization;

namespace Signet.Models;

/// <summary>What a transfer will cost, in tez.</summary>
/// <param name="Burn">Storage burn, e.g. the allocation cost when the destination has never held tez.</param>
public sealed record TransferEstimate(decimal Fee, decimal Burn, decimal Total, int GasLimit, int StorageLimit);

/// <summary>Public identity an indexer knows for an address: the TzProfiles data TzKT carries.</summary>
public sealed record AccountProfile(string? Name, string? Twitter, string? Description);

/// <summary>1 tez = 1,000,000 mutez.</summary>
public static class Mutez
{
    public const decimal PerTez = 1_000_000m;

    public static string FromTez(decimal tez) =>
        decimal.Round(tez * PerTez, 0, MidpointRounding.ToZero).ToString("0", CultureInfo.InvariantCulture);

    public static decimal? ToTez(string? mutez) =>
        decimal.TryParse(mutez, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v / PerTez : null;
}
