using Signet.Models;
using Signet.Services;
using Signet.ViewModels;

namespace Signet.Tests;

/// <summary>The Domains tab and the renamed Activity tab.</summary>
public class DomainsTabTests
{
    private static readonly Address Captain = new("tz1dCaMnnMJk76UodjewCK67ABiXWjcKj73N");

    [Fact]
    public void NamesTheTabs()
    {
        Assert.Equal(["Activity", "Assets", "NFTs", "Domains"], Enum.GetValues<WalletViewModel.ActivityTab>().Select(t => t.RawValue()));
    }

    /// <summary>The reply api.tezos.domains gave for captstake.tez, which the account owns, points at and publishes.</summary>
    [Fact]
    public void ReadsTheApisReply()
    {
        const string reply = """{"data":{"owned":{"items":[{"name":"captstake.tez","address":"tz1dCaMnnMJk76UodjewCK67ABiXWjcKj73N","owner":"tz1dCaMnnMJk76UodjewCK67ABiXWjcKj73N","expiresAtUtc":"2029-06-22T19:27:05Z"}]},"pointing":{"items":[{"name":"captstake.tez","address":"tz1dCaMnnMJk76UodjewCK67ABiXWjcKj73N","owner":"tz1dCaMnnMJk76UodjewCK67ABiXWjcKj73N","expiresAtUtc":"2029-06-22T19:27:05Z"}]},"reverseRecord":{"domain":{"name":"captstake.tez"}}}}""";
        var domains = TezosDomainsService.ParseDomains(reply);
        var name = Assert.Single(domains);
        Assert.True(name.Name == "captstake.tez" && name.IsReverseRecord);
        Assert.True(name.IsOwnedBy(Captain) && name.PointsAt(Captain));
        Assert.Equal(new DateTimeOffset(2029, 6, 22, 19, 27, 5, TimeSpan.Zero), name.Expires);
        Assert.Equal("https://app.tezos.domains/domain/captstake.tez", name.AppURL?.AbsoluteString());
    }

    [Fact]
    public void PutsThePublishedNameFirstThenTheRestByName()
    {
        const string other = "tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb";
        var reply = """
            {"data":{"owned":{"items":[{"name":"zeta.tez","address":"OTHER","owner":"CAPTAIN","expiresAtUtc":null},{"name":"me.tez","address":"CAPTAIN","owner":"CAPTAIN","expiresAtUtc":"2030-01-01T00:00:00Z"}]},
             "pointing":{"items":[{"name":"alpha.tez","address":"CAPTAIN","owner":"OTHER","expiresAtUtc":"2027-01-01T00:00:00Z"},{"name":"me.tez","address":"CAPTAIN","owner":"CAPTAIN","expiresAtUtc":"2030-01-01T00:00:00Z"}]},
             "reverseRecord":{"domain":{"name":"me.tez"}}}}
            """.Replace("OTHER", other).Replace("CAPTAIN", Captain.Value);
        var domains = TezosDomainsService.ParseDomains(reply);
        Assert.Equal(["me.tez", "alpha.tez", "zeta.tez"], domains.Select(d => d.Name));
        Assert.True(!domains[1].IsOwnedBy(Captain) && domains[1].PointsAt(Captain));
        Assert.True(domains[2].IsOwnedBy(Captain) && !domains[2].PointsAt(Captain) && domains[2].Expires is null);
        Assert.Empty(TezosDomainsService.ParseDomains("""{"data":{"owned":{"items":[]},"pointing":{"items":[]},"reverseRecord":null}}"""));
        Assert.Throws<TezosDomainsService.DomainsError>(() => TezosDomainsService.ParseDomains("""{"errors":[{"message":"bad"}]}"""));
    }

    [Fact]
    public async Task LoadsTheListForTheSelectedAccountOnly()
    {
        var model = new WalletViewModel(wallets: WalletViewModel.SampleWallets, chain: new MockChainService());
        Assert.NotNull(model.SelectedWallet);
        Assert.Null(model.CurrentDomainList);
        await model.LoadDomainList();
        var loaded = Assert.IsType<WalletViewModel.DomainListState.Loaded>(model.CurrentDomainList);
        Assert.True(loaded.Domains[0].IsReverseRecord);
        // Another account's list is never shown for this one.
        var first = model.SelectedWallet!;
        if (model.Wallets.FirstOrDefault(w => w.Address != first.Address) is { } second)
        {
            model.Select(second);
            Assert.Null(model.CurrentDomainList);
        }
    }

    [Fact]
    public void OffersTheDomainsTabOnMainnetOnly()
    {
        var model = new WalletViewModel(wallets: WalletViewModel.SampleWallets, chain: new MockChainService());
        model.Network = Network.Mainnet;
        Assert.Contains(WalletViewModel.ActivityTab.Domains, model.ActivityTabs);
        model.ActivityTabSelection = WalletViewModel.ActivityTab.Domains;
        Assert.Equal(WalletViewModel.ActivityTab.Domains, model.ShownActivityTab);
        model.Network = Network.Shadownet;
        Assert.Equal(["Activity", "Assets", "NFTs"], model.ActivityTabs.Select(t => t.RawValue()));
        Assert.Equal(WalletViewModel.ActivityTab.Transactions, model.ShownActivityTab);
    }

    [Fact]
    public void FollowsThePagesOfTheApi()
    {
        const string first = """{"data":{"owned":{"items":[{"name":"a.tez","address":null,"owner":"tz1dCaMnnMJk76UodjewCK67ABiXWjcKj73N","expiresAtUtc":null}],"pageInfo":{"hasNextPage":true,"endCursor":"C1"}},"pointing":{"items":[],"pageInfo":{"hasNextPage":false,"endCursor":null}},"reverseRecord":null}}""";
        var page = TezosDomainsService.ParsePage(first);
        Assert.True(page.OwnedNext == "C1" && page.PointingNext is null && page.Items.Count == 1 && page.Reverse is null);
        Assert.True(TezosDomainsService.PageSize <= 50);
    }

    [Fact]
    public async Task OnlyMainnetHasDomains()
    {
        // Answered before any request: Shadownet has no Tezos Domains endpoint.
        Assert.Null(await new TaquitoChainService(Network.Shadownet).TezosDomainsFor(Captain));
        Assert.NotNull(Network.Mainnet.TezosDomainsURL);
    }
}
