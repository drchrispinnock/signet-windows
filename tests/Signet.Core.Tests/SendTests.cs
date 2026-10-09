using System.Text;
using Signet.Models;
using Signet.Services;

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

    [Fact]
    public void ProfileParsingPrefersTzProfilesAlias()
    {
        var json = Encoding.UTF8.GetBytes("""
        {"alias":"TzKT Name","extras":{"profile":{"kind":"person","alias":"Captain Stake","twitter":"captstake","description":"Staking."}}}
        """);
        var profile = TzKTService.Profile(json);
        Assert.NotNull(profile);
        Assert.Equal("Captain Stake", profile.Name);
        Assert.Equal("captstake", profile.Twitter);
        Assert.Null(TzKTService.Profile(Encoding.UTF8.GetBytes("{\"address\":\"tz1x\"}")));
        Assert.Equal("Only TzKT", TzKTService.Profile(Encoding.UTF8.GetBytes("{\"alias\":\"Only TzKT\"}"))?.Name);
    }

    /// <summary>Estimation needs no secret: Captain Stake's public key is enough for the node to simulate.</summary>
    [Fact, Trait(Category.Name, Category.Network)]
    public async Task EstimatesAMainnetTransferWithoutASecretKey()
    {
        var captain = new Wallet("Captain Stake", MockChainService.CaptainStake,
                                 publicKey: "edpku5dNSxAs5R7g91zm9mDcfYVGsM4gCniaDSPVwYPt3xtszyzgJD", keyKind: KeyKind.Unencrypted);
        var estimate = await new TaquitoChainService(Network.Mainnet)
            .EstimateTransfer(captain, new Address("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb"), 1);
        Assert.True(estimate.Fee > 0);
        Assert.True(estimate.Fee < 0.01m);
        Assert.Equal(1 + estimate.Fee + estimate.Burn, estimate.Total);
    }

    [Fact, Trait(Category.Name, Category.Network)]
    public async Task ForwardDomainLookupFindsCaptainStake()
    {
        var service = new TezosDomainsService(Network.Mainnet.TezosDomainsURL!);
        Assert.Equal(MockChainService.CaptainStake, await service.Resolve("captstake.tez"));
        Assert.Null(await service.Resolve($"this-name-should-not-exist-{Guid.NewGuid().ToString()[..8]}.tez"));
    }
}
