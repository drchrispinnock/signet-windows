using System.Text;
using Signet.Models;
using Signet.Services;

namespace Signet.Tests;

public class TransactionParsingTests
{
    public static readonly Address Me = new("tz1dCaMnnMJk76UodjewCK67ABiXWjcKj73N");
    public static readonly byte[] Fixture = Encoding.UTF8.GetBytes("""
    [
      {"type":"transaction","id":1,"hash":"ooOUT","level":100,"timestamp":"2026-09-16T15:43:01Z","status":"applied","amount":1500000,"bakerFee":594,
       "sender":{"alias":"Captain Stake","address":"tz1dCaMnnMJk76UodjewCK67ABiXWjcKj73N"},"target":{"address":"tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb"}},
      {"type":"transaction","id":2,"hash":"ooIN","level":99,"timestamp":"2026-09-15T10:00:00Z","status":"applied","amount":250000,"bakerFee":400,
       "sender":{"alias":"objkt.com Treasury","address":"tz2BFTyPeYRzxd5aiBchbXN3WCZhx7BqbMBq"},"target":{"alias":"Captain Stake","address":"tz1dCaMnnMJk76UodjewCK67ABiXWjcKj73N"}},
      {"type":"transaction","id":3,"hash":"ooCALL","level":98,"timestamp":"2026-09-14T10:00:00Z","status":"failed","amount":0,"bakerFee":799,
       "sender":{"address":"tz1dCaMnnMJk76UodjewCK67ABiXWjcKj73N"},"target":{"alias":"objkt.com Marketplace","address":"KT1RJ6PbjHpwc3M5rw5s2Nbmefwbuwbdxton"},"parameter":{"entrypoint":"collect","value":{"int":"1"}}},
      {"type":"delegation","id":4,"hash":"ooDEL","level":97,"timestamp":"2026-09-13T10:00:00Z","status":"applied","bakerFee":300,"amount":5000000,
       "sender":{"address":"tz1dCaMnnMJk76UodjewCK67ABiXWjcKj73N"},"newDelegate":{"alias":"Everstake","address":"tz1aRoaRhSpRYvFdyvgWLL6TGyRoGF51wDjM"}},
      {"type":"delegation","id":6,"hash":"ooJOIN","level":95,"timestamp":"2026-09-11T10:00:00Z","status":"applied","bakerFee":300,"amount":6118049,
       "sender":{"address":"tz1SoQEinkjove9bCc5XanTpmvw9mPWzLBaq"},"prevDelegate":{"alias":"Kiln","address":"tz3Vq38qYD3GEbWcXHMLt5PaASZrkDtEiA8D"},"newDelegate":{"alias":"Captain Stake","address":"tz1dCaMnnMJk76UodjewCK67ABiXWjcKj73N"}},
      {"type":"delegation","id":7,"hash":"ooLEAVE","level":94,"timestamp":"2026-09-10T10:00:00Z","status":"applied","bakerFee":300,"amount":7261872,
       "sender":{"address":"tz1ghpncFJry1RKLEtKuLaEpdeP8HfFEFXsG"},"prevDelegate":{"alias":"Captain Stake","address":"tz1dCaMnnMJk76UodjewCK67ABiXWjcKj73N"},"newDelegate":null},
      {"type":"transaction","id":5,"hash":"ooSELF","level":96,"timestamp":"2026-09-12T10:00:00Z","status":"applied","amount":1,"bakerFee":1,
       "sender":{"address":"tz1dCaMnnMJk76UodjewCK67ABiXWjcKj73N"},"target":{"address":"tz1dCaMnnMJk76UodjewCK67ABiXWjcKj73N"}}
    ]
    """);

    [Fact]
    public void ClassifiesDirectionCounterpartyAndAmounts()
    {
        var txs = TzKTService.ParseOperations(Fixture, Me);
        Assert.Equal(7, txs.Count);

        var outgoing = txs[0];
        Assert.Equal(TezosTransaction.TransactionDirection.Outgoing, outgoing.Direction);
        Assert.Equal("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb", outgoing.Counterparty?.Value);
        Assert.Null(outgoing.CounterpartyAlias);
        Assert.Equal(1.5m, outgoing.Amount);
        Assert.Equal(0.000594m, outgoing.Fee);

        var incoming = txs[1];
        Assert.Equal(TezosTransaction.TransactionDirection.Incoming, incoming.Direction);
        Assert.Equal("objkt.com Treasury", incoming.CounterpartyAlias);
        Assert.Equal(0.25m, incoming.Amount);

        var call = txs[2];
        Assert.Equal("collect", call.Entrypoint);
        Assert.True(call.IsContractCall);
        Assert.False(call.IsApplied);

        var delegation = txs[3];
        Assert.Equal(TezosTransaction.TransactionKind.Delegation, delegation.Kind);
        Assert.Equal("Everstake", delegation.CounterpartyAlias);
        Assert.Equal(0m, delegation.Amount);                       // the amount field is a balance, not a transfer
        Assert.Equal(5m, delegation.DelegatedBalance);
        Assert.Equal(TezosTransaction.DelegationRoleKind.WeDelegated, delegation.DelegationRole(Me));

        // Fixture order: out, in, call, our delegation, join, leave, self-transfer.
        var join = txs[4];
        Assert.Equal(TezosTransaction.TransactionDirection.Incoming, join.Direction);
        Assert.Equal("tz1SoQEinkjove9bCc5XanTpmvw9mPWzLBaq", join.Counterparty?.Value);
        Assert.Equal(TezosTransaction.DelegationRoleKind.DelegatorJoined, join.DelegationRole(Me));
        Assert.Equal(6.118049m, join.DelegatedBalance);

        var leave = txs[5];
        Assert.Equal(TezosTransaction.DelegationRoleKind.DelegatorLeft, leave.DelegationRole(Me));
        Assert.Equal(Me, leave.PreviousDelegate);
        Assert.Null(leave.NewDelegate);

        Assert.Equal(TezosTransaction.TransactionDirection.SelfTransfer, txs[6].Direction);
    }

    [Fact]
    public void NamesPreferOurRecordsThenTheIndexer()
    {
        var model = new Signet.ViewModels.WalletViewModel(Signet.ViewModels.WalletViewModel.SampleWallets, new MockChainService());
        Assert.Equal(("Savings", true), model.DisplayName(Signet.ViewModels.WalletViewModel.SampleWallets[1].Address, "Somebody Else"));
        Assert.Equal(("Captain Stake", false), model.DisplayName(MockChainService.CaptainStake, "Captain Stake"));
        var stranger = new Address("tz3WXYtyDUNL91qfiCJtVUX746QpNv5i5ve5");
        Assert.Equal((stranger.Shortened(), false), model.DisplayName(stranger, null));
    }

    [Fact]
    public async Task RefreshLoadsTransactions()
    {
        var model = new Signet.ViewModels.WalletViewModel(Signet.ViewModels.WalletViewModel.SampleWallets, new MockChainService());
        await model.Refresh();
        Assert.Equal(4, model.Transactions.Count);
        Assert.Equal(Signet.ViewModels.WalletViewModel.ActivityTab.Transactions, model.ActivityTabSelection);
    }

    [Fact, Trait(Category.Name, Category.Network)]
    public async Task CaptainStakeHasHistoryOnTzKT()
    {
        var txs = await new TzKTService(Network.Mainnet.TzktURL!).RecentOperationsFor(Me, 10);
        Assert.NotEmpty(txs);
        Assert.True(txs.Count <= 10);
        Assert.All(txs, t => Assert.StartsWith("o", t.Hash));
    }
}
