namespace Signet.Models;

/// <summary>The five phases of a Tezos voting cycle, in order.</summary>
public enum VotingPeriodKind { Proposal, Exploration, Cooldown, Promotion, Adoption }

public static class VotingPeriodKindExtensions
{
    public static string RawValue(this VotingPeriodKind kind) => kind.ToString().ToLowerInvariant();

    public static VotingPeriodKind? FromRawValue(string? raw) =>
        Enum.GetValues<VotingPeriodKind>().Cast<VotingPeriodKind?>().FirstOrDefault(k => k!.Value.RawValue() == raw);

    public static string Title(this VotingPeriodKind kind) => kind switch
    {
        VotingPeriodKind.Proposal => "Proposal period",
        VotingPeriodKind.Exploration => "Exploration vote",
        VotingPeriodKind.Cooldown => "Cooldown period",
        VotingPeriodKind.Promotion => "Promotion vote",
        _ => "Adoption period",
    };

    /// <summary>Bakers can act: upvote in the proposal period, cast a ballot in the two votes.</summary>
    public static bool AcceptsUpvotes(this VotingPeriodKind kind) => kind == VotingPeriodKind.Proposal;
    public static bool AcceptsBallots(this VotingPeriodKind kind) => kind is VotingPeriodKind.Exploration or VotingPeriodKind.Promotion;
}

public enum BallotVote { Yay, Nay, Pass }

public static class BallotVoteExtensions
{
    public static string RawValue(this BallotVote vote) => vote.ToString().ToLowerInvariant();

    public static BallotVote? FromRawValue(string? raw) =>
        Enum.GetValues<BallotVote>().Cast<BallotVote?>().FirstOrDefault(v => v!.Value.RawValue() == raw);

    public static string Title(this BallotVote vote) => vote.ToString();
}

/// <summary>What the chain says about the current voting period and this baker's standing in it.</summary>
public sealed record GovernanceInfo
{
    /// <param name="VotingPower">Voting power behind the proposal so far.</param>
    public sealed record Proposal(string Hash, decimal VotingPower)
    {
        public string Id => Hash;
    }

    public sealed record Ballots(decimal Yay = 0, decimal Nay = 0, decimal Pass = 0)
    {
        public decimal Total => Yay + Nay + Pass;
    }

    public VotingPeriodKind? Kind { get; init; }
    public int? Index { get; init; }
    /// <summary>Blocks into the period, and blocks left.</summary>
    public int? Position { get; init; }
    public int? Remaining { get; init; }
    public IReadOnlyList<Proposal> Proposals { get; init; } = [];
    public string? CurrentProposal { get; init; }
    /// <summary>Our voting power if we are in the listings, else <c>null</c> (no vote this period).</summary>
    public decimal? VotingPower { get; init; }
    public decimal TotalVotingPower { get; init; }
    public int? QuorumPerTenThousand { get; init; }
    public Ballots BallotTally { get; init; } = new();
    /// <summary>How we voted already in this period, if we have.</summary>
    public BallotVote? MyBallot { get; init; }
    /// <summary>Proposals we have upvoted so far in this period (the protocol allows 20).</summary>
    public int ProposalCount { get; init; }

    public const int MaximumUpvotesPerPeriod = 20;

    public bool CanVote => VotingPower is not null;

    /// <summary>Fraction of total voting power behind <c>power</c>.</summary>
    public double Share(decimal power) => TotalVotingPower > 0 ? (double)(power / TotalVotingPower) : 0;
}

/// <summary>A voting operation. Neither kind carries a fee.</summary>
public abstract record GovernanceOperation
{
    /// <summary>Upvote proposals in the proposal period (one <c>proposals</c> operation, up to 20 hashes).</summary>
    public sealed record Upvote(IReadOnlyList<string> Hashes) : GovernanceOperation;
    /// <summary>Cast a ballot in an exploration or promotion vote.</summary>
    public sealed record Ballot(string Proposal, BallotVote Vote) : GovernanceOperation;

    public string Title => this switch
    {
        Upvote u => u.Hashes.Count == 1 ? "Upvote proposal" : $"Upvote {u.Hashes.Count} proposals",
        Ballot b => $"Vote {b.Vote.Title()}",
        _ => "",
    };

    public string BridgeKind => this is Upvote ? "proposals" : "ballot";

    public IReadOnlyDictionary<string, object> BridgeArgument => this switch
    {
        Upvote u => new Dictionary<string, object> { ["proposals"] = u.Hashes },
        Ballot b => new Dictionary<string, object> { ["proposal"] = b.Proposal, ["ballot"] = b.Vote.RawValue() },
        _ => new Dictionary<string, object>(),
    };

    /// <summary>Protocol hashes are base58check <c>P…</c> of 51 characters: 2-byte prefix plus a 32-byte hash.</summary>
    public static bool IsProposalHash(string text) =>
        text.Length == 51 && text.StartsWith('P') && Base58.CheckDecode(text) is { Length: 2 + 32 };
}
