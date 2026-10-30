namespace Signet.Models;

/// <summary>Where an account's delegation stands, plus its baker record if it is one.</summary>
/// <param name="DelegateAcceptsStaking">Whether the current delegate accepts staked tez (limit of staking over baking &gt; 0). null if unknown.</param>
public sealed record DelegateInfo(Address? Delegate, DelegateInfo.BakerRecord? Baker, bool? DelegateAcceptsStaking)
{
    public sealed record BakerRecord(
        bool Deactivated,
        int? GracePeriod,
        Address? ConsensusKey,
        IReadOnlyList<Address> PendingConsensusKeys,
        Address? CompanionKey,
        IReadOnlyList<Address> PendingCompanionKeys)
    {
        public StakingParameters? StakingParameters { get; init; }
        public IReadOnlyList<StakingParameters> PendingStakingParameters { get; init; } = [];

        // The key and parameter lists are part of the value: an unchanged refresh must compare equal (no redraw).
        public bool Equals(BakerRecord? other) =>
            other is not null && Deactivated == other.Deactivated && GracePeriod == other.GracePeriod &&
            ConsensusKey == other.ConsensusKey && PendingConsensusKeys.SequenceEqual(other.PendingConsensusKeys) &&
            CompanionKey == other.CompanionKey && PendingCompanionKeys.SequenceEqual(other.PendingCompanionKeys) &&
            StakingParameters == other.StakingParameters && PendingStakingParameters.SequenceEqual(other.PendingStakingParameters);
        public override int GetHashCode() => HashCode.Combine(Deactivated, GracePeriod, ConsensusKey, CompanionKey, StakingParameters);
    }

    public bool IsBaker => Baker is not null;
    public bool IsSelfDelegated => Baker is not null;

    /// <summary>
    /// Whether this account may stake. A baker always can: the staking limit only caps what
    /// other people may stake with it, never its own stake. Anyone else needs a delegate that
    /// has not switched staking off.
    /// </summary>
    public bool CanStake => IsBaker || (Delegate is not null && DelegateAcceptsStaking != false);
}

/// <summary>A baker's staking parameters, as the protocol stores them.</summary>
/// <param name="LimitMillionth">Max staked tez accepted from others, as a multiple of the baker's own stake, in millionths (9_000_000 = 9×).</param>
/// <param name="EdgeBillionth">Share of stakers' rewards kept by the baker, in billionths (1_000_000_000 = 100 %).</param>
/// <param name="Cycle">Cycle a pending change takes effect; null for the active set.</param>
public sealed record StakingParameters(int LimitMillionth, int EdgeBillionth, int? Cycle = null)
{
    public decimal LimitMultiplier => LimitMillionth / 1_000_000m;
    public decimal EdgePercent => EdgeBillionth / 10_000_000m;

    /// <summary>From user-facing numbers: a multiple (0–9) and a percentage (0–100).</summary>
    public static StakingParameters? FromUserValues(decimal limitMultiplier, decimal edgePercent)
    {
        if (limitMultiplier < 0 || limitMultiplier > 9 || edgePercent < 0 || edgePercent > 100) return null;
        return new StakingParameters((int)(limitMultiplier * 1_000_000), (int)(edgePercent * 10_000_000));
    }
}

/// <summary>A baker offered in the delegate picker.</summary>
/// <param name="AcceptsStaking">null when the indexer does not say.</param>
public sealed record BakerCandidate(Address Address, string? Alias, decimal StakingBalance, int Delegators, int Stakers, bool? AcceptsStaking)
{
    public string Id => Address.Value;
}

/// <summary>Operations in the staking and baking family.</summary>
public abstract record StakingOperation
{
    /// <summary><c>To</c> null = remove delegation.</summary>
    public sealed record Delegate(Address? To) : StakingOperation;
    public sealed record RegisterAsBaker : StakingOperation;
    public sealed record Stake(decimal Amount) : StakingOperation;
    public sealed record Unstake(decimal Amount) : StakingOperation;
    public sealed record FinalizeUnstake : StakingOperation;
    /// <summary><c>Proof</c> is required for tz4 (BLS) keys.</summary>
    public sealed record UpdateConsensusKey(string PublicKey, string? Proof) : StakingOperation;
    public sealed record UpdateCompanionKey(string PublicKey, string? Proof) : StakingOperation;
    /// <summary>Needs the baker's own address because it is a transaction to self.</summary>
    public sealed record SetStakingParameters(StakingParameters Parameters, Address Source) : StakingOperation;

    public string BridgeKind => this switch
    {
        Delegate => "setDelegate",
        RegisterAsBaker => "registerDelegate",
        Stake => "stake",
        Unstake => "unstake",
        FinalizeUnstake => "finalizeUnstake",
        UpdateConsensusKey => "updateConsensusKey",
        UpdateCompanionKey => "updateCompanionKey",
        SetStakingParameters => "setDelegateParameters",
        _ => throw new InvalidOperationException(),
    };

    public IReadOnlyDictionary<string, object> BridgeArgument => this switch
    {
        Delegate d => new Dictionary<string, object> { ["delegate"] = d.To?.Value ?? "" },
        RegisterAsBaker or FinalizeUnstake => new Dictionary<string, object>(),
        Stake s => new Dictionary<string, object> { ["amountMutez"] = Mutez.FromTez(s.Amount) },
        Unstake u => new Dictionary<string, object> { ["amountMutez"] = Mutez.FromTez(u.Amount) },
        UpdateConsensusKey k => KeyArgument(k.PublicKey, k.Proof),
        UpdateCompanionKey k => KeyArgument(k.PublicKey, k.Proof),
        SetStakingParameters p => new Dictionary<string, object>
        {
            ["limitMillionth"] = p.Parameters.LimitMillionth, ["edgeBillionth"] = p.Parameters.EdgeBillionth, ["source"] = p.Source.Value,
        },
        _ => throw new InvalidOperationException(),
    };

    private static Dictionary<string, object> KeyArgument(string pk, string? proof)
    {
        var argument = new Dictionary<string, object> { ["pk"] = pk };
        if (!string.IsNullOrEmpty(proof)) argument["proof"] = proof;
        return argument;
    }

    public string Title => this switch
    {
        Delegate d => d.To is null ? "Remove delegation" : "Delegate",
        RegisterAsBaker => "Register as a baker",
        Stake => "Stake",
        Unstake => "Unstake",
        FinalizeUnstake => "Finalize unstaked tez",
        UpdateConsensusKey => "Set consensus key",
        UpdateCompanionKey => "Set companion key",
        SetStakingParameters => "Set staking parameters",
        _ => "",
    };
}
