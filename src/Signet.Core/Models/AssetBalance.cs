using System.Globalization;

namespace Signet.Models;

/// <summary>A fungible balance shown in the asset list: tez, Etherlink, or any other token.</summary>
public sealed record AssetBalance(string Id, AssetBalance.AssetKind Kind, string Name, string Symbol, decimal Amount)
{
    public abstract record AssetKind
    {
        public sealed record Tez : AssetKind;
        public sealed record Etherlink : AssetKind;
        /// <summary>The account's shielded tez on the TzEL rollup (shown while shielded transactions are on).</summary>
        public sealed record Shielded : AssetKind;
        public sealed record Token(string Contract, string TokenId) : AssetKind;
    }

    /// <summary>One line of a balance breakdown, e.g. "Staked 21,315.69 tz".</summary>
    public sealed record Detail(string Id, string Label, decimal Amount);

    // Amount: in whole units (already divided by the token's decimals). For tez this is the full balance.

    /// <summary>How <c>Amount</c> splits up, if there is anything to show.</summary>
    public IReadOnlyList<Detail> Details { get; init; } = [];
    /// <summary>The token's logo, one URL per gateway to try (tokens only).</summary>
    public IReadOnlyList<Uri> IconURLs { get; init; } = [];
    /// <summary><c>fa1.2</c> / <c>fa2</c> for tokens.</summary>
    public string? Standard { get; init; }
    /// <summary>
    /// Why the amount could not be read (the Etherlink node unreachable); the row shows this
    /// instead of a figure.
    /// </summary>
    public string? Unavailable { get; init; }

    public string FormattedAmount => Format(Amount, Symbol);

    // The breakdown and logos are part of the value: an unchanged refresh must compare equal (no redraw).
    public bool Equals(AssetBalance? other) =>
        other is not null && Id == other.Id && Kind == other.Kind && Name == other.Name && Symbol == other.Symbol && Amount == other.Amount &&
        Standard == other.Standard && Unavailable == other.Unavailable && Details.SequenceEqual(other.Details) && IconURLs.SequenceEqual(other.IconURLs);
    public override int GetHashCode() => HashCode.Combine(Id, Kind, Amount);

    /// <summary>The Shielded row before the TzEL wallet exists: always 0 tz.</summary>
    public static readonly AssetBalance ShieldedPlaceholder = new("shielded", new AssetKind.Shielded(), "Shielded", "tz", 0);

    public static string Format(decimal amount, string symbol) =>
        $"{amount.ToString("#,0.######", CultureInfo.CurrentCulture)} {symbol}";
}
