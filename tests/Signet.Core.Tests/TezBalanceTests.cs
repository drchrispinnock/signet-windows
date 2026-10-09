using Signet.Models;
using Signet.Services;

namespace Signet.Tests;

public class TezBalanceTests
{
    [Fact]
    public void TotalIsTheSumOfAllParts()
    {
        var balance = new TezBalance(Spendable: 10, Staked: 20, UnstakedFrozen: 3, UnstakedFinalizable: 4);
        Assert.Equal(37m, balance.Total);
    }

    [Fact]
    public void ParsesMutezStringsLikeTheNodeReturns()
    {
        var balance = TezBalance.FromMutez("1386761464", "21315693554", "0", "0");
        Assert.NotNull(balance);
        Assert.Equal(1386.761464m, balance.Spendable);
        Assert.Equal(21315.693554m, balance.Staked);
        Assert.Equal(22702.455018m, balance.Total);
        Assert.Null(TezBalance.FromMutez(null, "1", null, null));
    }

    [Fact]
    public void BreakdownAlwaysShowsSpendableAndStakedButOnlyNonZeroUnstaking()
    {
        var quiet = new TezBalance(Spendable: 5, Staked: 0);
        Assert.Equal(["Spendable", "Staked"], quiet.Breakdown.Select(d => d.Label));

        var busy = new TezBalance(Spendable: 5, Staked: 10, UnstakedFrozen: 1, UnstakedFinalizable: 2);
        Assert.Equal(["Spendable", "Staked", "Unstaking", "Ready to finalize"], busy.Breakdown.Select(d => d.Label));
        Assert.Equal([5m, 10m, 1m, 2m], busy.Breakdown.Select(d => d.Amount));
    }

    /// <summary>
    /// Captain Stake is a baker, so staked tez dominates. Both fetch paths must agree with the
    /// node's own full_balance and with each other.
    /// </summary>
    [Fact, Trait(Category.Name, Category.Network)]
    public async Task CaptainStakeFullBalanceIncludesStake()
    {
        var address = new Address("tz1dCaMnnMJk76UodjewCK67ABiXWjcKj73N");
        var service = new TaquitoChainService(Network.Mainnet);

        var viaTaquito = await service.TezBalanceFor(address);
        var viaRPC = await service.DirectTezBalanceFor(address);

        Assert.True(viaTaquito.Staked > viaTaquito.Spendable);
        Assert.True(viaTaquito.Total > viaTaquito.Spendable);
        // Both paths read head; allow for a block landing between calls.
        Assert.True(Math.Abs(viaTaquito.Total - viaRPC.Total) < 100);
    }
}

public class AccountNotOnChainTests
{
    [Fact(Skip = Pending.ViewModel)]
    public void FlagsTheAccountInsteadOfFailing() { }

    /// <summary>A brand-new key has certainly never been funded, so both paths must report it as absent.</summary>
    [Fact, Trait(Category.Name, Category.Network)]
    public async Task FreshAddressIsReportedAsNotOnChain()
    {
        var fresh = new Address((await new KeyGenerator().Generate(AddressScheme.Tz1)).Address);
        var service = new TaquitoChainService(Network.Mainnet);
        var viaTaquito = await Assert.ThrowsAsync<ChainError.AccountNotOnChain>(() => service.TezBalanceFor(fresh));
        Assert.Equal(new ChainError.AccountNotOnChain(fresh), viaTaquito);
        var viaRPC = await Assert.ThrowsAsync<ChainError.AccountNotOnChain>(() => service.DirectTezBalanceFor(fresh));
        Assert.Equal(new ChainError.AccountNotOnChain(fresh), viaRPC);
    }
}
