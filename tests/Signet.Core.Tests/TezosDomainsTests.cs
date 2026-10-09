using Signet.Models;
using Signet.Services;

namespace Signet.Tests;

/// <summary>Live lookups against api.tezos.domains.</summary>
public class TezosDomainsTests
{
    [Fact, Trait(Category.Name, Category.Network)]
    public async Task ResolvesCaptainStake()
    {
        var service = new TezosDomainsService(Network.Mainnet.TezosDomainsURL!);
        Assert.Equal("captstake.tez", await service.ReverseName(new Address("tz1dCaMnnMJk76UodjewCK67ABiXWjcKj73N")));
    }

    [Fact, Trait(Category.Name, Category.Network)]
    public async Task AddressWithoutReverseRecordHasNoName()
    {
        var service = new TezosDomainsService(Network.Mainnet.TezosDomainsURL!);
        Assert.Null(await service.ReverseName(new Address("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb")));
    }

    [Fact, Trait(Category.Name, Category.Network)]
    public async Task ChainServiceReturnsZeroOrOneName()
    {
        var service = new TaquitoChainService(Network.Mainnet);
        Assert.Equal(["captstake.tez"], await service.DomainsFor(new Address("tz1dCaMnnMJk76UodjewCK67ABiXWjcKj73N")));
        Assert.Empty(await service.DomainsFor(new Address("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb")));
    }

    [Fact]
    public async Task NetworksWithoutDomainsReturnNothing()
    {
        var service = new TaquitoChainService(Network.Shadownet);
        Assert.Empty(await service.DomainsFor(new Address("tz1dCaMnnMJk76UodjewCK67ABiXWjcKj73N")));
    }
}

public class AvatarTests
{
    [Fact]
    public void AvatarsAlwaysComeFromMainnet()
    {
        var address = new Address("tz1dCaMnnMJk76UodjewCK67ABiXWjcKj73N");
        Assert.Equal("https://services.tzkt.io/v1/avatars/tz1dCaMnnMJk76UodjewCK67ABiXWjcKj73N", Profiles.AvatarURL(address)?.AbsoluteString());
        Assert.Equal(Network.Mainnet.TzktURL, Profiles.ProfileIndexer);
    }

    /// <summary>Captain Stake's avatar is served as WebP; the image loader must decode it.</summary>
    [Fact(Skip = "Pending port: ImageLoader (app layer)"), Trait(Category.Name, Category.Network)]
    public void CaptainStakeAvatarDecodes() { }
}
