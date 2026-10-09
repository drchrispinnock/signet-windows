using Signet.Models;

namespace Signet.Tests;

public class GovernanceTests
{
    [Fact(Skip = Pending.ChainService)]
    public void ParsesBridgePayload() { }

    [Fact]
    public void RecognisesProposalHashes()
    {
        Assert.True(GovernanceOperation.IsProposalHash("PtSeouLouXkxhg39oWzjxDWaCydNfR3RxCUrNe4Q9Ro8BTehcbh"));
        Assert.True(GovernanceOperation.IsProposalHash("PsRiotumaAMotcRoDWW1bysEhQy2n1M5fy8JgRp8jjRfHGmfeA7"));
        Assert.False(GovernanceOperation.IsProposalHash("PtSeouLouXkxhg39oWzjxDWaCydNfR3RxCUrNe4Q9Ro8BTehcbX"));   // bad checksum
        Assert.False(GovernanceOperation.IsProposalHash("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb"));
        Assert.False(GovernanceOperation.IsProposalHash(""));
    }

    [Fact]
    public void OperationsEncodeForTheBridge()
    {
        var up = new GovernanceOperation.Upvote(["PtA", "PtB"]);
        Assert.Equal("proposals", up.BridgeKind);
        Assert.Equal(["PtA", "PtB"], (IReadOnlyList<string>)up.BridgeArgument["proposals"]);
        Assert.Equal("Upvote 2 proposals", up.Title);
        var vote = new GovernanceOperation.Ballot("PtA", BallotVote.Nay);
        Assert.Equal("ballot", vote.BridgeKind);
        Assert.Equal("nay", vote.BridgeArgument["ballot"]);
        Assert.Equal("Vote Nay", vote.Title);
    }

    [Fact(Skip = Pending.ViewModel)]
    public void GovernanceMenuNeedsABakerWeCanSignFor() { }

    [Fact(Skip = Pending.ChainService), Trait(Category.Name, Category.Network)]
    public void ReadsTheLivePeriodOnShadownet() { }
}
