using Signet.Models;

namespace Signet.Tests;

public class SendViewModelTests
{
    private const string Reason = "Pending port: SendViewModel";

    [Fact(Skip = Reason)] public void SuggestsOurOtherWalletsAndFiltersByText() { }
    [Fact(Skip = Reason)] public void RecognisesOurOwnEntriesAsVerified() { }
    [Fact(Skip = Reason)] public void UnknownAddressGetsProfileNameButIsNotVerified() { }
    [Fact(Skip = Reason)] public void ResolvesTezNames() { }
    [Fact(Skip = Reason)] public void RejectsGarbageAmountsAndOverspend() { }
    [Fact(Skip = Reason)] public void ConfirmsEstimatesSendsAndConfirms() { }
    [Fact(Skip = Reason)] public void RefusesToSendToItself() { }
}

public class TransferHelpersTests
{
    [Fact]
    public void MutezConversionTruncatesBelowOneMutez()
    {
        Assert.Equal("1500000", Mutez.FromTez(1.5m));
        Assert.Equal("1", Mutez.FromTez(0.0000019m));
        Assert.Equal(22702.455018m, Mutez.ToTez("22702455018"));
    }

    [Fact(Skip = Pending.TzKT)]
    public void ProfileParsingPrefersTzProfilesAlias() { }

    /// <summary>Estimation needs no secret: Captain Stake's public key is enough for the node to simulate.</summary>
    [Fact(Skip = Pending.ChainService), Trait(Category.Name, Category.Network)]
    public void EstimatesAMainnetTransferWithoutASecretKey() { }

    [Fact(Skip = "Pending port: TezosDomainsService"), Trait(Category.Name, Category.Network)]
    public void ForwardDomainLookupFindsCaptainStake() { }
}
