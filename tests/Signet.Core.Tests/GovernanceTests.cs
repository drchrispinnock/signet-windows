using Signet.Models;
using Signet.Services;

namespace Signet.Tests;

public class GovernanceTests
{
    [Fact]
    public void ParsesBridgePayload()
    {
        const string json = """
        {"kind":"exploration","index":185,"position":10,"remaining":990,
         "proposals":[{"hash":"PtSeouLouXkxhg39oWzjxDWaCydNfR3RxCUrNe4Q9Ro8BTehcbh","votingPower":"5"},{"hash":"PsRiotumaAMotcRoDWW1bysEhQy2n1M5fy8JgRp8jjRfHGmfeA7","votingPower":"9"}],
         "currentProposal":"PtSeouLouXkxhg39oWzjxDWaCydNfR3RxCUrNe4Q9Ro8BTehcbh",
         "votingPower":"1234","totalVotingPower":"10000","quorumPerTenThousand":5500,
         "ballots":{"yay":"600","nay":"100","pass":"300"},"myBallot":"pass","proposalCount":2}
        """;
        var info = TaquitoChainService.GovernanceInfoFrom(JSONValue.Parse(json));
        Assert.Equal(VotingPeriodKind.Exploration, info.Kind);
        Assert.Equal(185, info.Index);
        Assert.Equal(990, info.Remaining);
        Assert.Equal("PsRiotumaAMotcRoDWW1bysEhQy2n1M5fy8JgRp8jjRfHGmfeA7", info.Proposals[0].Hash);   // most power first
        Assert.Equal(1234m, info.VotingPower);
        Assert.True(info.CanVote);
        Assert.Equal(0.1234, info.Share(1234));
        Assert.Equal(1000m, info.BallotTally.Total);
        Assert.Equal(BallotVote.Pass, info.MyBallot);
        Assert.Equal(2, info.ProposalCount);
        Assert.True(info.Kind?.AcceptsBallots() == true && info.Kind?.AcceptsUpvotes() == false);

        var none = TaquitoChainService.GovernanceInfoFrom(JSONValue.Parse("""{"kind":"cooldown","votingPower":null}"""));
        Assert.Equal(VotingPeriodKind.Cooldown, none.Kind);
        Assert.False(none.CanVote);
    }

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

    [Fact]
    public async Task GovernanceMenuNeedsABakerWeCanSignFor()
    {
        var model = new Signet.ViewModels.WalletViewModel(Signet.ViewModels.WalletViewModel.SampleWallets, new MockChainService());
        await model.Refresh();
        Assert.False(model.CanGovern);   // sample wallet delegates to Captain Stake, it is not a baker itself
        var baker = new Wallet("captain", MockChainService.CaptainStake, publicKey: "edpkX", keyKind: KeyKind.Unencrypted);
        var store = new InMemoryWalletStore();
        store.AddWithSecretKey(baker, "edskX");
        var bakerModel = new Signet.ViewModels.WalletViewModel(chain: new MockChainService(), walletStore: store);
        await bakerModel.Refresh();
        Assert.True(bakerModel.CanGovern);
        var watch = new Signet.ViewModels.WalletViewModel([new Wallet("captain", MockChainService.CaptainStake, keyKind: KeyKind.None)], new MockChainService());
        await watch.Refresh();
        Assert.False(watch.CanGovern);
        var result = await bakerModel.PerformGovernance(new GovernanceOperation.Ballot("PtA", BallotVote.Yay), null);
        Assert.Equal("ooMockGovernanceballot", result.Hash);
    }

    [Fact, Trait(Category.Name, Category.Network)]
    public async Task ReadsTheLivePeriodOnShadownet()
    {
        var info = await new TaquitoChainService(Network.Shadownet).GovernanceInfoFor(new Address("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb"));
        Assert.NotNull(info.Kind);
        Assert.True(info.TotalVotingPower > 0);
    }
}
