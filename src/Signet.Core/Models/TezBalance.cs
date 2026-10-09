using System.Globalization;

namespace Signet.Models;

/// <summary>
/// An account's tez, split the way the protocol splits it. All amounts in whole tez.
///
/// <c>balance</c> from the node is only the spendable part; staked tez and tez being unstaked
/// (frozen for the unbonding period, then finalizable until claimed) are held separately but
/// still belong to the account.
/// </summary>
public sealed record TezBalance(decimal Spendable, decimal Staked = 0, decimal UnstakedFrozen = 0, decimal UnstakedFinalizable = 0)
{
    /// <summary>Everything the account owns, matching the node's <c>full_balance</c>.</summary>
    public decimal Total => Spendable + Staked + UnstakedFrozen + UnstakedFinalizable;

    /// <summary>Builds from mutez strings as the RPC returns them.</summary>
    public static TezBalance? FromMutez(string? mutezSpendable, string? staked, string? unstakedFrozen, string? unstakedFinalizable)
    {
        static decimal? Tez(string? s) =>
            decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v / 1_000_000m : null;
        if (Tez(mutezSpendable) is not { } spendable) return null;
        return new TezBalance(spendable, Tez(staked) ?? 0, Tez(unstakedFrozen) ?? 0, Tez(unstakedFinalizable) ?? 0);
    }

    /// <summary>Lines for the breakdown. Spendable and staked always; unstaked lines only when non-zero.</summary>
    public IReadOnlyList<AssetBalance.Detail> Breakdown
    {
        get
        {
            var lines = new List<AssetBalance.Detail>
            {
                new("spendable", "Spendable", Spendable),
                new("staked", "Staked", Staked),
            };
            if (UnstakedFrozen > 0) lines.Add(new("unstaking", "Unstaking", UnstakedFrozen));
            if (UnstakedFinalizable > 0) lines.Add(new("finalizable", "Ready to finalize", UnstakedFinalizable));
            return lines;
        }
    }
}
