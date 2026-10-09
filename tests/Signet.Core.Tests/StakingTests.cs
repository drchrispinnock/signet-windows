using Signet.Models;

namespace Signet.Tests;

public class StakingOperationTests
{
    // MockChainService.captainStake upstream; switch to it when MockChainService is ported.
    internal static readonly Address CaptainStake = new("tz1dCaMnnMJk76UodjewCK67ABiXWjcKj73N");

    [Fact]
    public void MapsToBridgeKindsAndArguments()
    {
        Assert.Equal("setDelegate", new StakingOperation.Delegate(null).BridgeKind);
        Assert.Equal("", new StakingOperation.Delegate(null).BridgeArgument["delegate"]);
        Assert.Equal(CaptainStake.Value, new StakingOperation.Delegate(CaptainStake).BridgeArgument["delegate"]);
        Assert.Equal("1500000", new StakingOperation.Stake(1.5m).BridgeArgument["amountMutez"]);
        Assert.Equal("unstake", new StakingOperation.Unstake(2).BridgeKind);
        Assert.Empty(new StakingOperation.FinalizeUnstake().BridgeArgument);
        Assert.Equal(["pk"], new StakingOperation.UpdateConsensusKey("edpkX", null).BridgeArgument.Keys.Order());
        Assert.Equal(["pk", "proof"], new StakingOperation.UpdateCompanionKey("BLpkX", "BLsigP").BridgeArgument.Keys.Order());
        Assert.Equal("Register as a baker", new StakingOperation.RegisterAsBaker().Title);
    }

    [Fact]
    public void BakersCanAlwaysStakeOthersNeedAWillingDelegate()
    {
        var me = new Address("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb");
        var baker = new DelegateInfo.BakerRecord(Deactivated: false, GracePeriod: null, ConsensusKey: null, PendingConsensusKeys: [], CompanionKey: null, PendingCompanionKeys: []);
        // A baker with the default limit of 0 does not accept outside stakers but can still stake its own tez.
        Assert.True(new DelegateInfo(me, baker, false).CanStake);
        Assert.True(new DelegateInfo(me, baker, null).CanStake);
        Assert.True(new DelegateInfo(CaptainStake, null, true).CanStake);
        Assert.True(new DelegateInfo(CaptainStake, null, null).CanStake);
        Assert.False(new DelegateInfo(CaptainStake, null, false).CanStake);
        Assert.False(new DelegateInfo(null, null, null).CanStake);
    }

    [Fact(Skip = Pending.TzKT)]
    public void ParsesBakersFromTzKT() { }
}

public class StakingViewModelTests
{
    [Fact(Skip = Pending.ViewModel)]
    public void RefreshLoadsDelegateInfoAndOperationsRoundTrip() { }

    [Fact(Skip = Pending.ViewModel)]
    public void EncryptedWalletNeedsThePassword() { }
}

/// <summary>Live: a fresh tz1 on Quantumnet delegates to a baker, stakes, and registers as a baker itself.</summary>
public class QuantumnetStakingTests
{
    [Fact(Skip = Pending.ChainService), Trait(Category.Name, Category.Network)]
    public void DelegateStakeAndRegisterOnQuantumnet() { }
}

public class StakingParametersTests
{
    [Fact]
    public void ConvertsBetweenProtocolUnitsAndHumanNumbers()
    {
        var p = new StakingParameters(LimitMillionth: 9_000_000, EdgeBillionth: 10_000_000);
        Assert.Equal(9m, p.LimitMultiplier);
        Assert.Equal(1m, p.EdgePercent);
        var q = StakingParameters.FromUserValues(2.5m, 15);
        Assert.NotNull(q);
        Assert.Equal(2_500_000, q.LimitMillionth);
        Assert.Equal(150_000_000, q.EdgeBillionth);
        Assert.Null(StakingParameters.FromUserValues(10, 0));
        Assert.Null(StakingParameters.FromUserValues(1, 101));
        var op = new StakingOperation.SetStakingParameters(q, StakingOperationTests.CaptainStake);
        Assert.Equal("setDelegateParameters", op.BridgeKind);
        Assert.Equal(2_500_000, op.BridgeArgument["limitMillionth"]);
        Assert.Equal(StakingOperationTests.CaptainStake.Value, op.BridgeArgument["source"]);
        // Upstream also checks TaquitoChainService.taquitoEncodablePublicKey; it lands with that service.
    }
}

/// <summary>
/// Live: a freshly registered Quantumnet baker sets its staking parameters, and if an XMSS (tz6)
/// public key is available from the local octez-client wallet, points its consensus key at it
/// through the raw node-forged path.
/// </summary>
public class QuantumnetBakerParametersTests
{
    [Fact(Skip = Pending.ChainService), Trait(Category.Name, Category.Network)]
    public void SetsParametersAndMaybeATz6ConsensusKey() { }
}
