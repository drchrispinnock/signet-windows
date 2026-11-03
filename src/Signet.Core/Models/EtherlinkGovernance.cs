using System.Collections;
using System.Globalization;

namespace Signet.Models;

/// <summary>
/// Etherlink's on-chain governance: bakers propose and upvote kernel upgrades in a Proposal
/// period and vote Yea / Nay / Pass on the winner in the Promotion period that follows, in
/// layer 1 contracts (one for slow upgrades, one for fast ones) written by Baking Bad
/// (github.com/baking-bad/etherlink-governance). Calls carry an ordinary fee, unlike layer 1
/// ballots. A baker may name another account as its voting key through the delegation
/// contract named in each governance contract's storage; the governance contract then counts
/// that baker's power for the voting key (<c>list_voters</c>).
/// </summary>
public static class EtherlinkGovernance
{
    /// <summary>The two kernel upgrade tracks, each with its own contract and period length.</summary>
    public enum Process { Slow, Fast }

    public static string RawValue(this Process process) => process == Process.Slow ? "slow" : "fast";
    public static Process? ProcessFromRawValue(string raw) => raw switch { "slow" => Process.Slow, "fast" => Process.Fast, _ => null };
    public static IReadOnlyList<Process> AllProcesses { get; } = [Process.Slow, Process.Fast];

    public static string Title(this Process process) => process == Process.Slow ? "Slow kernel upgrades" : "Fast kernel upgrades";
    /// <summary>The field's label in the Networks sheet.</summary>
    public static string SettingTitle(this Process process) => process == Process.Slow ? "Slow governance" : "Fast governance";

    /// <summary>A promotion ballot, as the contract's <c>vote</c> entrypoint spells it.</summary>
    public enum Vote { Yea, Nay, Pass }

    public static IReadOnlyList<Vote> AllVotes { get; } = [Vote.Yea, Vote.Nay, Vote.Pass];
    public static string RawValue(this Vote vote) => vote switch { Vote.Yea => "yea", Vote.Nay => "nay", _ => "pass" };
    public static Vote? VoteFromRawValue(string raw) => raw switch { "yea" => Vote.Yea, "nay" => Vote.Nay, "pass" => Vote.Pass, _ => null };
    public static string Title(this Vote vote) => vote switch { Vote.Yea => "Yea", Vote.Nay => "Nay", _ => "Pass" };

    public enum PeriodKind { Proposal, Promotion }

    public static PeriodKind? PeriodKindFromRawValue(string raw) => raw switch { "proposal" => PeriodKind.Proposal, "promotion" => PeriodKind.Promotion, _ => null };
    public static string Title(this PeriodKind kind) => kind == PeriodKind.Proposal ? "Proposal period" : "Promotion period";

    /// <summary>A kernel proposed in the current proposal period.</summary>
    /// <param name="Hash">The kernel root hash, <c>0x</c> and 66 hex digits.</param>
    /// <param name="Upvotes">Voting power behind it so far.</param>
    /// <param name="Upvoted">Whether the selected account upvoted it already.</param>
    public sealed record Proposal(string Hash, IReadOnlyList<Address> Proposers, decimal Upvotes, bool Upvoted = false)
    {
        public string Id => Hash;

        public bool Equals(Proposal? other) =>
            other is not null && Hash == other.Hash && Proposers.SequenceEqual(other.Proposers) && Upvotes == other.Upvotes && Upvoted == other.Upvoted;
        public override int GetHashCode() => HashCode.Combine(Hash, Upvotes, Upvoted);
    }

    /// <summary>The contract's thresholds, each a share of <c>Scale</c> (100 for percent).</summary>
    public sealed record Config(int PeriodLength = 0, int UpvotingLimit = 20, int Scale = 100,
                                int ProposalQuorum = 0, int PromotionQuorum = 0, int PromotionSupermajority = 0)
    {
        public double Fraction(int value) => Scale > 0 ? (double)value / Scale : 0;
    }

    public sealed record Tallies(decimal Yea = 0, decimal Nay = 0, decimal Pass = 0)
    {
        public decimal Total => Yea + Nay + Pass;
    }

    /// <summary>The current period of one governance contract and where the selected account stands in it.</summary>
    public sealed class State(Address contract)
    {
        public Address Contract { get; set; } = contract;
        public int PeriodIndex { get; set; }
        public PeriodKind PeriodKind { get; set; } = PeriodKind.Proposal;
        /// <summary>Layer 1 blocks until the period ends.</summary>
        public int RemainingBlocks { get; set; }
        public Config Config { get; set; } = new();
        /// <summary>All bakers' voting power as the contract counted it when the period opened.</summary>
        public decimal TotalVotingPower { get; set; }
        /// <summary>Proposal period: the kernels proposed so far, most upvoted first.</summary>
        public IReadOnlyList<Proposal> Proposals { get; set; } = [];
        /// <summary>False when the proposal list could not be read (no indexer), so only a typed hash can be upvoted.</summary>
        public bool ProposalsKnown { get; set; } = true;
        /// <summary>Promotion period: the kernel being voted on.</summary>
        public string? Candidate { get; set; }
        public Tallies Tallies { get; set; } = new();
        /// <summary>The account's own voting power plus that of the bakers it votes for; 0 when it cannot vote.</summary>
        public decimal VotingPower { get; set; }
        /// <summary>Bakers whose voting key the account is, on this contract.</summary>
        public IReadOnlyList<Address> VotesFor { get; set; } = [];
        /// <summary>Proposals the account has submitted or upvoted in this period, of <c>Config.UpvotingLimit</c>.</summary>
        public int UpvotesUsed { get; set; }
        public Vote? MyVote { get; set; }

        public bool CanVote => VotingPower > 0;

        /// <summary>Fraction of total voting power behind <c>power</c>.</summary>
        public double Share(decimal power) => TotalVotingPower > 0 ? (double)(power / TotalVotingPower) : 0;
    }

    /// <summary>One call to a governance contract.</summary>
    public abstract record Operation
    {
        public sealed record NewProposal(string Hash) : Operation;
        public sealed record Upvote(string Hash) : Operation;
        public sealed record Vote(EtherlinkGovernance.Vote Ballot) : Operation;

        public string Title => this switch
        {
            NewProposal => "Propose kernel",
            Upvote => "Upvote proposal",
            Vote v => $"Vote {v.Ballot.Title()}",
            _ => "",
        };

        public string Entrypoint => this switch
        {
            NewProposal => "new_proposal",
            Upvote => "upvote_proposal",
            _ => "vote",
        };

        /// <summary>The entrypoint's argument as the bridge takes it: the hash's hex (no <c>0x</c>) or the vote word.</summary>
        public string Payload => this switch
        {
            NewProposal p => p.Hash[2..],
            Upvote u => u.Hash[2..],
            Vote v => v.Ballot.RawValue(),
            _ => "",
        };

        public string? KernelHash => this switch
        {
            NewProposal p => p.Hash,
            Upvote u => u.Hash,
            _ => null,
        };
    }

    /// <summary>A kernel root hash is 33 bytes: <c>0x</c> and 66 hex digits (the contract checks the length).</summary>
    public const int KernelHashBytes = 33;

    /// <summary><c>text</c> as a kernel hash the contract accepts (lowercase, with <c>0x</c>), or null.</summary>
    public static string? KernelHash(string text)
    {
        var hex = text.Trim().ToLowerInvariant();
        if (hex.StartsWith("0x", StringComparison.Ordinal)) hex = hex[2..];
        if (hex.Length != KernelHashBytes * 2 || !hex.All(Uri.IsHexDigit)) return null;
        return "0x" + hex;
    }

    public static bool IsKernelHash(string text) => KernelHash(text) is not null;

    /// <summary>Governance contract addresses are KT1s.</summary>
    public static Address? ContractAddress(string text)
    {
        var address = new Address(text.Trim());
        return address.IsContract && address.IsValidAccount ? address : null;
    }

    /// <summary>Where proposals are described and discussed.</summary>
    public static readonly Uri SiteURL = new("https://governance.etherlink.com");

    /// <summary>
    /// The contracts a network governs each process through. Compared by value, so a network
    /// record stays equal to another with the same contracts.
    /// </summary>
    public sealed class Contracts : IReadOnlyDictionary<Process, Address>, IEquatable<Contracts>
    {
        private readonly SortedDictionary<Process, Address> map;

        public static readonly Contracts Empty = new(new Dictionary<Process, Address>());

        public Contracts(IEnumerable<KeyValuePair<Process, Address>> entries) => map = new SortedDictionary<Process, Address>(entries.ToDictionary());

        /// <summary>The same map with <c>process</c> set to <c>contract</c> (null removes it).</summary>
        public Contracts With(Process process, Address? contract)
        {
            var copy = new Dictionary<Process, Address>(map);
            if (contract is { } c) copy[process] = c;
            else copy.Remove(process);
            return new Contracts(copy);
        }

        public Address this[Process key] => map[key];
        public Address? Get(Process key) => map.TryGetValue(key, out var value) ? value : null;
        public IEnumerable<Process> Keys => map.Keys;
        public IEnumerable<Address> Values => map.Values;
        public int Count => map.Count;
        public bool IsEmpty => map.Count == 0;
        public bool ContainsKey(Process key) => map.ContainsKey(key);
        public bool TryGetValue(Process key, out Address value) => map.TryGetValue(key, out value!);
        public IEnumerator<KeyValuePair<Process, Address>> GetEnumerator() => map.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public bool Equals(Contracts? other) => other is not null && map.SequenceEqual(other.map);
        public override bool Equals(object? obj) => Equals(obj as Contracts);
        public override int GetHashCode() => map.Aggregate(0, (h, p) => HashCode.Combine(h, p.Key, p.Value));
        public override string ToString() => string.Join(", ", map.Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.Key.RawValue()}: {p.Value.Value}")));
    }
}
