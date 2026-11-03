using Signet.Models;
using Signet.Services;
using Signet.ViewModels;

namespace Signet.Tests;

/// <summary>
/// Etherlink governance: the layer 1 contracts per network, their settings, the state as the
/// bridge reports it, the operations, and when the Governance item and its Etherlink tab show.
/// </summary>
public class EtherlinkGovernanceTests
{
    private const string Hash = "0x009e07373751a43acbf0cd747b404f45130e0ad561c4b7da937e02eb812bd0f7e8";
    private static readonly EtherlinkGovernance.Process Slow = EtherlinkGovernance.Process.Slow;
    private static readonly EtherlinkGovernance.Process Fast = EtherlinkGovernance.Process.Fast;

    [Fact]
    public void MainnetShipsTheGaneshaContractsAndTestnetsNone()
    {
        Assert.Equal("KT1AXRU3wLc87WNhLhVGrgqDGubLACUMUgPb", Network.Mainnet.DefaultEtherlinkGovernance.Get(Slow)?.Value);
        Assert.Equal("KT19oUVQPnVLuUBYXrBVd46WJnNAMpqkKSwo", Network.Mainnet.DefaultEtherlinkGovernance.Get(Fast)?.Value);
        Assert.Equal(Network.Mainnet.DefaultEtherlinkGovernance, Network.Mainnet.EtherlinkGovernanceContracts);
        // Shown only with Layer 2 on, and never where no contract is set.
        Assert.True(!Network.Mainnet.CanGovernEtherlink && Network.Mainnet.L2On().CanGovernEtherlink);
        Assert.Equal([Slow, Fast], Network.Mainnet.L2On().EtherlinkGovernanceProcesses);
        foreach (var network in new[] { Network.Shadownet, Network.Bakingnet, Network.Ushuaianet, Network.Quantumnet, Network.Custom })
            Assert.True(network.EtherlinkGovernanceContracts.IsEmpty && !network.L2On().CanGovernEtherlink, network.Name);
        var fastOnly = Network.Mainnet.L2On().UsingEtherlinkGovernance(null, Slow);
        Assert.True(fastOnly.CanGovernEtherlink && fastOnly.EtherlinkGovernanceProcesses.SequenceEqual([Fast]) && !fastOnly.IsUsingDefaultEtherlinkGovernance(Slow));
        Assert.False(fastOnly.UsingEtherlinkGovernance(null, Fast).CanGovernEtherlink);
    }

    [Fact]
    public void KernelHashesAndContractAddressesAreChecked()
    {
        Assert.Equal(Hash, EtherlinkGovernance.KernelHash($" {Hash.ToUpperInvariant()} "));
        Assert.Equal(Hash, EtherlinkGovernance.KernelHash(Hash[2..]));
        Assert.Null(EtherlinkGovernance.KernelHash(Hash + "00"));
        Assert.Null(EtherlinkGovernance.KernelHash(Hash[..^1]));
        Assert.Null(EtherlinkGovernance.KernelHash("0xzz" + Hash[4..]));
        Assert.False(EtherlinkGovernance.IsKernelHash(""));
        Assert.Equal("KT1AXRU3wLc87WNhLhVGrgqDGubLACUMUgPb", EtherlinkGovernance.ContractAddress(" KT1AXRU3wLc87WNhLhVGrgqDGubLACUMUgPb ")?.Value);
        Assert.Null(EtherlinkGovernance.ContractAddress("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb"));
        Assert.Null(EtherlinkGovernance.ContractAddress("KT1AXRU3wLc87WNhLhVGrgqDGubLACUMUgPX"));   // bad checksum
    }

    [Fact]
    public void OperationsNameTheirEntrypointAndPayload()
    {
        var propose = new EtherlinkGovernance.Operation.NewProposal(Hash);
        Assert.True(propose.Entrypoint == "new_proposal" && propose.Payload == Hash[2..] && propose.KernelHash == Hash && propose.Title == "Propose kernel");
        var upvote = new EtherlinkGovernance.Operation.Upvote(Hash);
        Assert.True(upvote.Entrypoint == "upvote_proposal" && upvote.Payload == Hash[2..] && upvote.Title == "Upvote proposal");
        var vote = new EtherlinkGovernance.Operation.Vote(EtherlinkGovernance.Vote.Nay);
        Assert.True(vote.Entrypoint == "vote" && vote.Payload == "nay" && vote.KernelHash is null && vote.Title == "Vote Nay");
        Assert.Equal(["yea", "nay", "pass"], EtherlinkGovernance.AllVotes.Select(v => v.RawValue()));   // the contract's spelling, not the protocol's "yay"
    }

    private const string StateJSON = """
        {"periodIndex":42,"periodKind":"promotion","remainingBlocks":100,
         "config":{"periodLength":4800,"upvotingLimit":20,"scale":100,"proposalQuorum":5,"promotionQuorum":15,"promotionSupermajority":80},
         "totalVotingPower":"700","current":"promotion",
         "candidate":"0x009e07373751a43acbf0cd747b404f45130e0ad561c4b7da937e02eb812bd0f7e8",
         "tallies":{"yea":"10","nay":"20","pass":"30"},
         "proposals":[{"hash":"0x009e07373751a43acbf0cd747b404f45130e0ad561c4b7da937e02eb812bd0f7e8","proposers":["tz1dCaMnnMJk76UodjewCK67ABiXWjcKj73N"],"upvotes":"50","upvoted":true}],
         "proposalsKnown":false,"votingPower":"70","votesFor":["tz1dCaMnnMJk76UodjewCK67ABiXWjcKj73N"],"upvotesUsed":2,"myVote":"pass"}
        """;

    [Fact]
    public void ParsesTheBridgeState()
    {
        var contract = new Address("KT19oUVQPnVLuUBYXrBVd46WJnNAMpqkKSwo");
        var state = TaquitoChainService.EtherlinkGovernanceStateFrom(JSONValue.Parse(StateJSON), contract);
        Assert.True(state.Contract == contract && state.PeriodIndex == 42 && state.PeriodKind == EtherlinkGovernance.PeriodKind.Promotion && state.RemainingBlocks == 100);
        Assert.True(state.Config.PeriodLength == 4800 && state.Config.PromotionSupermajority == 80 && state.Config.Fraction(state.Config.PromotionQuorum) == 0.15);
        Assert.True(state.TotalVotingPower == 700 && state.Share(70) == 0.1);
        Assert.Equal(Hash, state.Candidate);
        Assert.True(state.Tallies.Total == 60 && state.Tallies.Pass == 30);
        Assert.True(state.Proposals.Count == 1 && state.Proposals[0].Upvoted && state.Proposals[0].Proposers.SequenceEqual([MockChainService.CaptainStake]) && state.Proposals[0].Upvotes == 50);
        Assert.True(!state.ProposalsKnown && state.VotingPower == 70 && state.CanVote && state.VotesFor.SequenceEqual([MockChainService.CaptainStake])
                    && state.UpvotesUsed == 2 && state.MyVote == EtherlinkGovernance.Vote.Pass);

        // Every number is checked (S20): a negative count, a non-numeric power or a bad hash is an error, never a trap.
        foreach (var broken in new[]
        {
            StateJSON.Replace("\"remainingBlocks\":100", "\"remainingBlocks\":-1"),
            StateJSON.Replace("\"votingPower\":\"70\"", "\"votingPower\":\"7e1\""),
            StateJSON.Replace("\"periodKind\":\"promotion\"", "\"periodKind\":\"cooldown\""),
            StateJSON.Replace("\"candidate\":\"0x009e", "\"candidate\":\"0x9e"),
        })
        {
            Assert.ThrowsAny<TaquitoBridge.BridgeError>(() => TaquitoChainService.EtherlinkGovernanceStateFrom(JSONValue.Parse(broken), contract));
        }
    }

    [Fact]
    public void ContractsAreKeptPerNetworkOnlyWhereTheyDiffer()
    {
        var store = new InMemoryAppStateStore();
        var model = new WalletViewModel(WalletViewModel.SampleWallets, new MockChainService(), stateStore: store);
        Assert.Equal("KT1AXRU3wLc87WNhLhVGrgqDGubLACUMUgPb", model.EtherlinkGovernanceContract(Slow, Network.Mainnet));
        Assert.Equal("", model.EtherlinkGovernanceContract(Slow, Network.Shadownet));
        // A replacement on the network in use is applied and saved; the default is not saved.
        Assert.True(model.SetEtherlinkGovernanceContract(" KT1VZVNCNnhUp7s15d9RsdycP7C1iwYhAQ8r ", Slow, Network.Mainnet));
        Assert.Equal("KT1VZVNCNnhUp7s15d9RsdycP7C1iwYhAQ8r", model.Network.EtherlinkGovernanceContracts.Get(Slow)?.Value);
        Assert.Equal("KT1VZVNCNnhUp7s15d9RsdycP7C1iwYhAQ8r", store.Load().EtherlinkGovernance?["Mainnet"]["slow"]);
        Assert.Single(store.Load().EtherlinkGovernance!);
        Assert.Single(store.Load().EtherlinkGovernance!["Mainnet"]);
        Assert.False(model.SetEtherlinkGovernanceContract("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb", Slow, Network.Mainnet));
        Assert.Equal("KT1VZVNCNnhUp7s15d9RsdycP7C1iwYhAQ8r", model.Network.EtherlinkGovernanceContracts.Get(Slow)?.Value);
        // Empty turns the process off and is remembered as "".
        Assert.True(model.SetEtherlinkGovernanceContract("", Fast, Network.Mainnet));
        Assert.True(model.Network.EtherlinkGovernanceContracts.Get(Fast) is null && store.Load().EtherlinkGovernance?["Mainnet"]["fast"] == "");
        model.UseDefaultEtherlinkGovernanceContract(Slow, Network.Mainnet);
        model.UseDefaultEtherlinkGovernanceContract(Fast, Network.Mainnet);
        Assert.True(model.Network.EtherlinkGovernanceContracts.Equals(Network.Mainnet.DefaultEtherlinkGovernance) && store.Load().EtherlinkGovernance is null);
        // Another network, without switching: a contract can be given where we ship none.
        Assert.True(model.SetEtherlinkGovernanceContract("KT1DxndcFitAbxLdJCN3C1pPivqbC3RJxD1R", Fast, Network.Shadownet));
        Assert.True(model.Network.Name == "Mainnet" && model.EtherlinkGovernanceContract(Fast, Network.Shadownet) == "KT1DxndcFitAbxLdJCN3C1pPivqbC3RJxD1R");
        // A relaunch reads the same choices back.
        var again = new WalletViewModel(WalletViewModel.SampleWallets, new MockChainService(), stateStore: store);
        Assert.Equal(new EtherlinkGovernance.Contracts([new(Fast, new Address("KT1DxndcFitAbxLdJCN3C1pPivqbC3RJxD1R"))]),
                     again.Resolved(Network.Shadownet).EtherlinkGovernanceContracts);
    }

    [Fact]
    public async Task TheEtherlinkTabNeedsLayer2AndABakerOrVotingKey()
    {
        // The sample account is Captain Stake's voting key in the mock, but not a baker.
        var model = new WalletViewModel(WalletViewModel.SampleWallets, new MockChainService(), stateStore: new InMemoryAppStateStore());
        await model.Refresh();
        Assert.True(!model.CanGovern && !model.CanGovernEtherlink && model.EtherlinkVotesFor.Count == 0);   // Layer 2 off: nothing is asked
        model.SetL2Enabled(true, Network.Mainnet);
        await model.Refresh();
        Assert.Equal([MockChainService.CaptainStake], model.EtherlinkVotesFor);
        Assert.True(model.CanGovernEtherlink && model.CanGovern);
        // Off again, or no contract: the item goes.
        model.SetEtherlinkGovernanceContract("", Slow, Network.Mainnet);
        model.SetEtherlinkGovernanceContract("", Fast, Network.Mainnet);
        await model.Refresh();
        Assert.True(!model.CanGovernEtherlink && !model.CanGovern);

        // A baker with Layer 2 on gets both tabs; a watch-only baker gets nothing.
        var baker = new Wallet("captain", MockChainService.CaptainStake, publicKey: "edpkX", keyKind: KeyKind.Unencrypted);
        var store = new InMemoryWalletStore();
        store.AddWithSecretKey(baker, "edskX");
        var bakerModel = new WalletViewModel(chain: new MockChainService(), walletStore: store, stateStore: new InMemoryAppStateStore());
        bakerModel.SetL2Enabled(true, Network.Mainnet);
        await bakerModel.Refresh();
        Assert.True(bakerModel.CanGovern && bakerModel.CanGovernEtherlink && bakerModel.EtherlinkVotesFor.Count == 0);
        var watch = new WalletViewModel([new Wallet("captain", MockChainService.CaptainStake, keyKind: KeyKind.None)], new MockChainService(), stateStore: new InMemoryAppStateStore());
        watch.SetL2Enabled(true, Network.Mainnet);
        await watch.Refresh();
        Assert.False(watch.CanGovernEtherlink);

        // Reading the state, estimating and sending through the approved-limits path.
        var state = await bakerModel.EtherlinkGovernanceState(Fast);
        Assert.True(state.PeriodKind == EtherlinkGovernance.PeriodKind.Proposal && state.CanVote && state.Proposals.FirstOrDefault()?.Hash == MockChainService.MockKernelHash);
        var upvote = new EtherlinkGovernance.Operation.Upvote(MockChainService.MockKernelHash);
        var approved = await bakerModel.EstimateEtherlinkGovernance(upvote, Fast);
        Assert.True(approved.Contract.Value == "KT19oUVQPnVLuUBYXrBVd46WJnNAMpqkKSwo" && approved.Estimate.Limits is not null);
        var result = await bakerModel.PerformEtherlinkGovernance(upvote, Fast, approved, null);
        Assert.Equal("ooMockEtherlinkupvote_proposal", result.Hash);
        // The estimate is for one operation on one contract in one context (S31).
        await Assert.ThrowsAsync<WalletViewModel.WalletError.ContextChanged>(() =>
            bakerModel.PerformEtherlinkGovernance(new EtherlinkGovernance.Operation.Vote(EtherlinkGovernance.Vote.Yea), Fast, approved, null));
        await Assert.ThrowsAsync<WalletViewModel.WalletError.ContextChanged>(() => bakerModel.PerformEtherlinkGovernance(upvote, Slow, approved, null));
        await Assert.ThrowsAsync<WalletViewModel.WalletError.NotEstimated>(() => bakerModel.PerformEtherlinkGovernance(upvote, Fast, null, null));
        await Assert.ThrowsAsync<WalletViewModel.WalletError.InvalidKernelHash>(() =>
            bakerModel.EstimateEtherlinkGovernance(new EtherlinkGovernance.Operation.NewProposal("0x1234"), Fast));
        bakerModel.SetL2Enabled(false, Network.Mainnet);
        await Assert.ThrowsAsync<ChainError.NoEtherlinkNode>(() => bakerModel.EtherlinkGovernanceState(Fast));
    }

    /// <summary>
    /// Mainnet's fast contract is read live: the period is one of the two kinds, the
    /// thresholds are Ganesha's, and a known voting key lists the bakers it votes for.
    /// </summary>
    [Fact, Trait(Category.Name, Category.Network)]
    public async Task ReadsTheLiveMainnetContracts()
    {
        var service = new TaquitoChainService(Network.Mainnet);
        var fast = new Address("KT19oUVQPnVLuUBYXrBVd46WJnNAMpqkKSwo");
        var votingKey = new Address("tz1XjYbYEdJgoUK2ERHD5iVaquJQwPbvYjDJ");
        var bakers = await service.EtherlinkVotingRightsFor(votingKey, fast);
        Assert.True(bakers.Count > 0 && bakers.All(b => b.Value.StartsWith("tz3", StringComparison.Ordinal)));
        var state = await service.EtherlinkGovernanceState(fast, votingKey);
        Assert.True(state.Config.PeriodLength == 4800 && state.Config.PromotionSupermajority == 80 && state.Config.UpvotingLimit == 20);
        Assert.True(state.RemainingBlocks > 0 && state.RemainingBlocks <= 4800 && state.TotalVotingPower > 0);
        Assert.True(state.VotesFor.SequenceEqual(bakers) && state.VotingPower > 0 && state.ProposalsKnown);
        if (state.PeriodKind == EtherlinkGovernance.PeriodKind.Promotion) Assert.NotNull(state.Candidate);
    }
}
