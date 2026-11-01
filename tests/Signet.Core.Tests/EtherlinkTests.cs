using System.Text;
using Signet.Models;
using Signet.Services;
using Signet.ViewModels;

namespace Signet.Tests;

// Etherlink (0.9): per-network Etherlink settings and the dashboard's Etherlink balance row.
// Upstream's ShieldedSwitchTests and ShieldedRowTests, and the shielded lines of the AppState
// tests, are left out with TzEL.

internal static class EtherlinkTestSupport
{
    /// <summary>The network with Layer 2 switched on at its shipped node, as a user would in Settings.</summary>
    public static Network L2On(this Network network) => network.UsingL2(true, network.L2RPCURL);

    /// <summary>Upstream compares the saved maps with dictionary literals; <c>null</c> expects an absent map.</summary>
    public static void AssertMap<T>(IReadOnlyDictionary<string, T>? actual, params (string Key, T Value)[] expected)
    {
        if (expected.Length == 0)
        {
            Assert.Null(actual);
            return;
        }
        Assert.NotNull(actual);
        Assert.Equal(expected.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => KeyValuePair.Create(e.Key, e.Value)),
                     actual.OrderBy(p => p.Key, StringComparer.Ordinal));
    }

    /// <summary>A fresh tz1 on Shadownet funded with 10 tez from the faucet (proof of work), or a failed assertion.</summary>
    public static async Task<(Wallet Wallet, KeyMaterial Material)> FundedShadownetTz1(string alias, TaquitoChainService layer1)
    {
        var material = await new KeyGenerator().Generate(AddressScheme.Tz1);
        var wallet = new Wallet(alias, new Address(material.Address), AddressScheme.Tz1, material.PublicKey, KeyKind.Unencrypted);
        await FaucetService.For(Network.Shadownet)!.RequestTez(wallet.Address, 10);
        var funded = false;
        for (var i = 0; i < 40 && !funded; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);
            try { funded = (await layer1.TezBalanceFor(wallet.Address)).Spendable >= 10; }
            catch (Exception) { }   // not on chain yet
        }
        Assert.True(funded, "the faucet transfer never showed up");
        return (wallet, material);
    }

    /// <summary>Waits for the rollup to credit a deposit; true once the Etherlink balance reaches <c>amount</c>.</summary>
    public static async Task<bool> WaitForEtherlinkBalance(TaquitoChainService layer1, Address address, decimal amount)
    {
        for (var i = 0; i < 45; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);
            try { if (await layer1.EtherlinkBalanceFor(address) >= amount) return true; }
            catch (Exception) { }
        }
        return false;
    }
}

public class EtherlinkNetworkTests
{
    [Fact]
    public void LayerTwoIsOffByDefaultAndShippedForMainnetAndShadownet()
    {
        Assert.True(!Network.Mainnet.HasL2 && Network.Mainnet.L2RPCURL?.Host == "michelson.etherlink.mainnet.octez.io");
        Assert.True(!Network.Shadownet.HasL2 && Network.Shadownet.L2RPCURL?.Host == "michelson.etherlink.shadownet.octez.io");
        Assert.True(Network.Mainnet.L2On().HasL2 && Network.Mainnet.L2On().IsUsingDefaultL2Node && Network.Mainnet.L2On() != Network.Mainnet);
        Assert.True(Network.Mainnet.ExpectedL2ChainID == "NetXohUVN5QWR4f" && Network.Shadownet.ExpectedL2ChainID == "NetXtLrzvQDobza");
        foreach (var network in new[] { Network.Bakingnet, Network.Ushuaianet, Network.Quantumnet, Network.Custom })
            Assert.True(!network.L2Enabled && network.L2RPCURL is null && !network.HasL2, network.Name);
        // Switched on without a node is not usable yet; off with a node keeps the node for later.
        var on = Network.Bakingnet.UsingL2(true, null);
        Assert.True(on.L2Enabled && !on.HasL2);
        var off = Network.Mainnet.L2On().UsingL2(false, Network.Mainnet.L2RPCURL);
        Assert.True(!off.HasL2 && off.IsUsingDefaultL2Node && off == Network.Mainnet);
    }

    [Fact]
    public void TheLayer2NetworkIsTheMichelsonInterfaceWithItsOwnIndexer()
    {
        Assert.Null(Network.Shadownet.Layer2);   // off by default
        var l2 = Network.Shadownet.L2On().Layer2;
        Assert.NotNull(l2);
        Assert.True(l2.RpcURL == Network.Shadownet.L2RPCURL && l2.ExpectedChainID == "NetXtLrzvQDobza" && l2.LayerNumber == 2);
        Assert.Equal("api.shadownet.etherlink.tzkt.io", l2.TzktURL?.Host);
        Assert.Equal("https://shadownet.etherlink.tzkt.io/ooX", l2.ExplorerURLForOperation("ooX")?.AbsoluteString());
        Assert.True(l2.Layer2 is null && !l2.HasL2 && !l2.IsMainnet);
        Assert.Equal("api.etherlink.tzkt.io", Network.Mainnet.L2On().Layer2?.TzktURL?.Host);
        Assert.Null(Network.Bakingnet.L2On().Layer2);
    }

    [Fact]
    public void OnlyTz1ToTz3KeysAreUsableOnTheRollup()
    {
        Assert.True(new Address("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb").IsUsableOnEtherlink);
        Assert.True(new Address("tz2BFTyPeYRzxd5aiBchbXN3WCZhx7BqbMBq").IsUsableOnEtherlink);
        Assert.True(new Address("tz3WXYtyDUNL91qfiCJtVUX746QpNv5i5ve5").IsUsableOnEtherlink);
        Assert.False(new Address("tz4HVR6aty9KwsQFHh81C1G7gBdhxT8kuytm").IsUsableOnEtherlink);
        Assert.False(new Address("KT1Mkqf6TnMVmhAAuBJCizkzAu4Nhg6DTPmM").IsUsableOnEtherlink);
        // A scheme check only: a malformed tz1 passes it and is caught by validity elsewhere.
        Assert.True(new Address("tz1nonsense").IsUsableOnEtherlink && !new Address("tz1nonsense").IsValidAccount && !new Address("").IsUsableOnEtherlink);
    }
}

public class EtherlinkSettingsTests
{
    [Fact]
    public void EtherlinkChoicesAreKeptPerNetworkAndSurviveNodeChanges()
    {
        var state = new InMemoryAppStateStore();
        var model = new WalletViewModel(WalletViewModel.SampleWallets, new MockChainService(), stateStore: state);
        Assert.True(model.Network.Name == "Mainnet" && !model.IsL2Enabled(Network.Mainnet) && !model.Network.HasL2);

        // On, on the network in use: applied at once and saved because it differs from the default.
        model.SetL2Enabled(true, Network.Mainnet);
        Assert.True(model.Network.HasL2 && model.Network.IsUsingDefaultL2Node && model.IsL2Enabled(Network.Mainnet));
        EtherlinkTestSupport.AssertMap(state.Load().L2Enabled, ("Mainnet", true));
        EtherlinkTestSupport.AssertMap<string>(state.Load().L2NodeURLs);
        // Changing the layer 1 node keeps the Etherlink choice.
        Assert.True(model.SetNode("rpc.example.org"));
        Assert.True(model.Network.HasL2 && model.Network.RpcURL.Host == "rpc.example.org");
        model.UseDefaultNode();
        Assert.True(model.Network.HasL2);
        model.SetL2Enabled(false, Network.Mainnet);
        Assert.False(model.Network.HasL2);
        EtherlinkTestSupport.AssertMap<bool>(state.Load().L2Enabled);

        // Another network, without switching: on needs a node before it counts.
        model.SetL2Enabled(true, Network.Bakingnet);
        Assert.True(model.IsL2Enabled(Network.Bakingnet) && model.L2NodeURL(Network.Bakingnet) is null);
        Assert.False(model.SetL2Node("not a url", Network.Bakingnet));
        Assert.True(model.SetL2Node("l2.example.org", Network.Bakingnet));
        EtherlinkTestSupport.AssertMap(state.Load().L2NodeURLs, ("Bakingnet", "https://l2.example.org"));
        EtherlinkTestSupport.AssertMap(state.Load().L2Enabled, ("Bakingnet", true));
        model.SwitchNetwork(Network.Bakingnet);
        Assert.True(model.Network.HasL2 && model.Network.L2RPCURL?.Host == "l2.example.org");
        model.UseDefaultL2Node(Network.Bakingnet);
        Assert.True(model.Network.L2Enabled && !model.Network.HasL2);
        EtherlinkTestSupport.AssertMap<string>(state.Load().L2NodeURLs);

        // A fresh model over the same state resolves the same choices.
        model.SetL2Enabled(true, Network.Mainnet);
        var again = new WalletViewModel(WalletViewModel.SampleWallets, new MockChainService(), stateStore: state);
        Assert.True(again.Network.Name == "Bakingnet" && again.IsL2Enabled(Network.Bakingnet) && again.IsL2Enabled(Network.Mainnet));
        Assert.True(again.L2NodeURL(Network.Mainnet) == Network.Mainnet.L2DefaultRPCURL && !again.IsL2Enabled(Network.Shadownet));
    }
}

public class EtherlinkRowTests
{
    internal static readonly Wallet Tz4 = new("bls", new Address("tz4HVR6aty9KwsQFHh81C1G7gBdhxT8kuytm"), keyKind: KeyKind.Unencrypted);

    private static AssetBalance? EtherlinkRow(WalletViewModel model) => model.Assets.FirstOrDefault(a => a.Kind is AssetBalance.AssetKind.Etherlink);

    [Fact]
    public async Task RowFollowsTheNetworkSettingAndTheKeyKind()
    {
        var model = new WalletViewModel([.. WalletViewModel.SampleWallets, Tz4], new MockChainService());
        await model.Refresh();
        Assert.Equal(["tez"], model.Assets.Select(a => a.Id));   // Layer 2 is off by default
        model.SetL2Enabled(true, Network.Mainnet);
        await model.Refresh();
        Assert.Equal(["tez", "etherlink"], model.Assets.Select(a => a.Id));
        Assert.True(EtherlinkRow(model)?.Amount == 5432.1m && EtherlinkRow(model)?.Unavailable is null);

        model.SetL2Enabled(false, Network.Mainnet);
        await model.Refresh();
        Assert.Equal(["tez"], model.Assets.Select(a => a.Id));

        model.SetL2Enabled(true, Network.Mainnet);
        model.Select(Tz4);
        await model.Refresh();
        Assert.Equal(["tez"], model.Assets.Select(a => a.Id));

        model.Select(WalletViewModel.SampleWallets[0]);
        model.SwitchNetwork(Network.Bakingnet);
        await model.Refresh();
        Assert.Equal(["tez"], model.Assets.Select(a => a.Id));
    }

    internal sealed class FailingEtherlink(bool notOnChain) : TestChainService
    {
        public override Task<TezBalance> TezBalanceFor(Address address) =>
            notOnChain ? throw new ChainError.AccountNotOnChain(address) : Task.FromResult(new TezBalance(Spendable: 12));
        public override Task<decimal> EtherlinkBalanceFor(Address address) => throw new HttpRequestException("Could not connect to the server.");
    }

    [Fact]
    public async Task AnUnreachableEtherlinkNodeBlanksOnlyThatRow()
    {
        var model = new WalletViewModel(WalletViewModel.SampleWallets, new FailingEtherlink(notOnChain: false));
        model.SetL2Enabled(true, Network.Mainnet);
        await model.Refresh();
        Assert.True(model.ErrorMessage is null);
        Assert.Equal(["tez", "etherlink"], model.Assets.Select(a => a.Id));
        Assert.Contains("Etherlink balance unavailable", EtherlinkRow(model)?.Unavailable);
    }

    [Fact]
    public async Task AnAccountNotOnLayer1ShowsNoEtherlinkRow()
    {
        var model = new WalletViewModel(WalletViewModel.SampleWallets, new FailingEtherlink(notOnChain: true));
        model.SetL2Enabled(true, Network.Mainnet);
        await model.Refresh();
        Assert.True(model.AccountNotOnChain && model.Assets.Count == 0);
    }
}

/// <summary>Live: the Shadownet Etherlink endpoint answers as a Tezos node and refuses a wrong chain id.</summary>
public class EtherlinkLiveTests
{
    /// <summary>
    /// A transfer between two tz accounts on the Michelson interface, through the ordinary
    /// Taquito path against the Layer 2 node: fund on layer 1, deposit through the bridge, then
    /// send on Etherlink and watch both balances move.
    /// </summary>
    [Fact, Trait(Category.Name, Category.Network)]
    public async Task SendsTezBetweenAccountsOnEtherlink()
    {
        var layer1 = new TaquitoChainService(Network.Shadownet.L2On());
        var layer2 = new TaquitoChainService(Network.Shadownet.L2On().Layer2!);
        var (wallet, payer) = await EtherlinkTestSupport.FundedShadownetTz1("l2-payer", layer1);
        var payee = await new KeyGenerator().Generate(AddressScheme.Tz2);
        var signer = new SigningKey.Secret(payer.SecretKey, null, wallet.Address);

        var deposit = await layer1.EstimateEtherlinkDeposit(wallet, 3);
        var sent = await layer1.SendEtherlinkDeposit(wallet, signer, 3, deposit.Limits!);
        await layer1.WaitForConfirmation(sent.Hash);
        Assert.True(await EtherlinkTestSupport.WaitForEtherlinkBalance(layer1, wallet.Address, 3));

        // Now the account is on Etherlink: an ordinary transfer there, fee and all.
        var before = await layer2.TezBalanceFor(wallet.Address);
        Assert.Equal(3, before.Spendable);
        var estimate = await layer2.EstimateTransfer(wallet, new Address(payee.Address), 1);
        Assert.NotNull(estimate.Limits);
        var transfer = await layer2.SendTransfer(wallet, signer, new Address(payee.Address), 1, estimate.Limits!);
        TestContext.Current.SendDiagnosticMessage($"etherlink transfer {transfer.Hash}");
        Assert.True(await layer2.WaitForConfirmation(transfer.Hash) > 0);
        Assert.Equal(1, (await layer2.TezBalanceFor(new Address(payee.Address))).Spendable);
        Assert.True((await layer2.TezBalanceFor(wallet.Address)).Spendable < 2);
    }

    [Fact, Trait(Category.Name, Category.Network)]
    public async Task ReadsABalanceFromShadownetEtherlink()
    {
        var someone = new Address("tz1KqTpEZ7Yob7QbPE4Hy4Wo8fHG8LhKxZSx");
        var balance = await new TaquitoChainService(Network.Shadownet.L2On()).EtherlinkBalanceFor(someone);
        Assert.True(balance >= 0);
        await Assert.ThrowsAsync<ChainError.NoEtherlinkNode>(() => new TaquitoChainService(Network.Shadownet).EtherlinkBalanceFor(someone));
        var wrongChain = Network.Shadownet.L2On() with { ExpectedL2ChainID = Network.Mainnet.ExpectedL2ChainID };
        var error = await Assert.ThrowsAsync<ChainError.WrongChain>(() => new TaquitoChainService(wrongChain).EtherlinkBalanceFor(someone));
        Assert.Equal(new ChainError.WrongChain(Network.Mainnet.ExpectedL2ChainID!, Network.Shadownet.ExpectedL2ChainID!), error);
    }

    /// <summary>
    /// A fresh account is only derived on Etherlink until it links itself; after the gateway
    /// no-op the view records it. The account needs tez on Etherlink first (bridge deposit).
    /// </summary>
    [Fact, Trait(Category.Name, Category.Network)]
    public async Task LinksAFreshAccountToItsEtherlinkAlias()
    {
        var layer1 = new TaquitoChainService(Network.Shadownet.L2On());
        var layer2 = new TaquitoChainService(Network.Shadownet.L2On().Layer2!);
        var (wallet, material) = await EtherlinkTestSupport.FundedShadownetTz1("link-test", layer1);
        var signer = new SigningKey.Secret(material.SecretKey, null, wallet.Address);
        var alias = EtherlinkAlias.AddressFor(wallet.Address);
        Assert.Equal(new EtherlinkAliasStatus.Unlinked(alias), await layer2.EtherlinkAliasStatusFor(wallet.Address));

        var deposit = await layer1.EstimateEtherlinkDeposit(wallet, 2);
        var sent = await layer1.SendEtherlinkDeposit(wallet, signer, 2, deposit.Limits!);
        await layer1.WaitForConfirmation(sent.Hash);
        Assert.True(await EtherlinkTestSupport.WaitForEtherlinkBalance(layer1, wallet.Address, 2));
        // Money on the Michelson side does not link the EVM side.
        Assert.Equal(new EtherlinkAliasStatus.Unlinked(alias), await layer2.EtherlinkAliasStatusFor(wallet.Address));

        var estimate = await layer2.EstimateEtherlinkLink(wallet);
        Assert.True(estimate.Fee > 0 && estimate.Limits is not null);
        var link = await layer2.SendEtherlinkLink(wallet, signer, estimate.Limits!);
        TestContext.Current.SendDiagnosticMessage($"etherlink link {link.Hash}");
        Assert.True(await layer2.WaitForConfirmation(link.Hash) > 0);
        Assert.Equal(new EtherlinkAliasStatus.Linked(alias), await layer2.EtherlinkAliasStatusFor(wallet.Address));
    }
}

/// <summary>
/// The EVM-side address is shown only once it exists on chain; before that the account links
/// itself with a gateway no-op on the Layer 2 node.
/// </summary>
public class EtherlinkAliasTests
{
    [Fact]
    public async Task StatusAndLinkGoThroughTheLayer2Service()
    {
        var store = new InMemoryWalletStore();
        store.AddWithSecretKey(WalletViewModel.SampleWallets[0], "edskSAMPLE");
        store.AddWithSecretKey(WalletViewModel.SampleWallets[1], "edeskSAMPLE");
        var model = new WalletViewModel(WalletViewModel.SampleWallets, new MockChainService(), walletStore: store);
        var me = WalletViewModel.SampleWallets[0];
        lock (MockChainService.LinkedAliases) MockChainService.LinkedAliases.Remove(me.Address);
        var alias = EtherlinkAlias.AddressFor(me.Address);

        Assert.Null(await model.EtherlinkAliasStatusForSelected());   // Layer 2 off
        model.SetL2Enabled(true, Network.Mainnet);
        Assert.Equal(new EtherlinkAliasStatus.Unlinked(alias), await model.EtherlinkAliasStatusForSelected());
        await Assert.ThrowsAsync<WalletViewModel.WalletError.NotEstimated>(() => model.LinkToEtherlink(null, null));
        var estimate = await model.EstimateEtherlinkLink();
        Assert.True(estimate.Fee > 0 && estimate.Limits is not null);
        var (hash, level) = await model.LinkToEtherlink(estimate.Limits, null);
        Assert.True(hash == "ooMockLink" && level > 0);
        Assert.Equal(new EtherlinkAliasStatus.Linked(alias), await model.EtherlinkAliasStatusForSelected());

        // An encrypted key needs its password; a watch-only or tz4 account cannot link at all.
        model.Select(WalletViewModel.SampleWallets[1]);
        var again = await model.EstimateEtherlinkLink();
        await Assert.ThrowsAsync<ChainError.WrongPassphrase>(() => model.LinkToEtherlink(again.Limits, "wrong"));
        var watch = new Wallet("watch", new Address("tz3WXYtyDUNL91qfiCJtVUX746QpNv5i5ve5"));
        var tz4 = new Wallet("bls", new Address("tz4HVR6aty9KwsQFHh81C1G7gBdhxT8kuytm"), publicKey: "BLpk", keyKind: KeyKind.Unencrypted);
        var others = new WalletViewModel([watch, tz4], new MockChainService());
        others.SetL2Enabled(true, Network.Mainnet);
        await Assert.ThrowsAnyAsync<SendViewModel.SendError>(() => others.EstimateEtherlinkLink());
        others.Select(tz4);
        Assert.Null(await others.EtherlinkAliasStatusForSelected());
    }

    [Fact]
    public void ViewRepliesAreParsedAndCheckedAgainstTheDerivedAddress()
    {
        var me = new Address("tz1VSVEv2mCtMYczcTDyeVg4DRUvy57ENN8E");
        const string alias = "0x68767b38768e554a31735268b2d356d5cc75d96c";
        static byte[] Reply(string kind, string named) =>
            Encoding.UTF8.GetBytes($$$"""{"data":{"prim":"Some","args":[{"prim":"Pair","args":[{"int":"{{{kind}}}"},{"string":"{{{named}}}"}]}]}}""");
        // As the Shadownet gateway answered, checksum-cased.
        Assert.Equal(new EtherlinkAliasStatus.Linked(alias), TaquitoChainService.AliasStatus(Reply("0", "0x68767b38768E554A31735268b2d356d5Cc75D96c"), me));
        Assert.Equal(new EtherlinkAliasStatus.Unlinked(alias), TaquitoChainService.AliasStatus(Reply("1", alias), me));
        Assert.ThrowsAny<Exception>(() => TaquitoChainService.AliasStatus(Reply("0", "0x16142132dd616dd8f61b8972ae4b9fcf8a22a450"), me));
        Assert.ThrowsAny<Exception>(() => TaquitoChainService.AliasStatus(Reply("2", alias), me));
        Assert.ThrowsAny<Exception>(() => TaquitoChainService.AliasStatus(Encoding.UTF8.GetBytes("""{"data":{"prim":"None"}}"""), me));
    }

    [Fact]
    public async Task Layer1ServicesRefuseAliasCalls()
    {
        var layer1 = new TaquitoChainService(Network.Shadownet.L2On());
        await Assert.ThrowsAsync<ChainError.NotALayer2Service>(() => layer1.EtherlinkAliasStatusFor(new Address("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb")));
        await Assert.ThrowsAsync<ChainError.NotALayer2Service>(() => layer1.EstimateEtherlinkLink(WalletViewModel.SampleWallets[0]));
        // Upstream also checks TaquitoBridge.kind(of: "sendEtherlinkLink") == .signing; the call policy (S30) is not ported.
    }
}

/// <summary>F20: the saved-state encoding of a network's node, Layer 2 and links is a pure value round trip.</summary>
public class AppStateNetworkTests
{
    [Fact]
    public void RememberingDefaultsSavesNothingAndResolvingGivesThemBack()
    {
        var state = new AppState().Remember(Network.Shadownet);
        Assert.Equal(new AppState(), state);   // a network on its defaults leaves every map null
        Assert.True(state.Resolved(Network.Shadownet) == Network.Shadownet && !state.Resolved(Network.Mainnet).L2Enabled);
    }

    [Fact]
    public void CustomChoicesRoundTripSparsely()
    {
        var node = new Uri("https://node.example.org");
        var l2 = new Uri("https://l2.example.org");
        var explorer = new Uri("https://explorer.example.org");
        var custom = Network.Shadownet.UsingNode(node).UsingL2(true, l2).Using(explorer, Network.Service.Explorer).Using(null, Network.Service.Faucet);
        var state = new AppState().Remember(custom);
        EtherlinkTestSupport.AssertMap(state.NodeURLs, ("Shadownet", node.AbsoluteString()));
        EtherlinkTestSupport.AssertMap(state.L2NodeURLs, ("Shadownet", l2.AbsoluteString()));
        EtherlinkTestSupport.AssertMap(state.L2Enabled, ("Shadownet", true));
        // Only what differs, and "" for off.
        Assert.NotNull(state.ServiceURLs);
        Assert.Equal(["Shadownet"], state.ServiceURLs.Keys);
        EtherlinkTestSupport.AssertMap(state.ServiceURLs["Shadownet"], ("explorer", explorer.AbsoluteString()), ("faucet", ""));
        var back = state.Resolved(Network.Shadownet);
        Assert.Equal(custom, back);
        Assert.True(back.RpcURL == node && back.HasL2 && back.L2RPCURL == l2 && back.ExplorerURL == explorer && back.FaucetURL is null);
        Assert.Equal(Network.Mainnet, state.Resolved(Network.Mainnet));   // another network is untouched
        // Back to the defaults: the maps empty out to null again.
        Assert.Equal(new AppState(), state.Remember(Network.Shadownet));
    }

    [Fact]
    public void SavedTextThatIsNotAURLIsIgnoredAndLayer2StaysOff()
    {
        var state = new AppState
        {
            NodeURLs = new Dictionary<string, string> { ["Shadownet"] = "not a url" },
            L2NodeURLs = new Dictionary<string, string> { ["Shadownet"] = "::nope::" },
            ServiceURLs = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["Shadownet"] = new Dictionary<string, string> { ["tzkt"] = "nor this", ["explorer"] = "" },
            },
        };
        var back = state.Resolved(Network.Shadownet);
        Assert.True(back.RpcURL == Network.Shadownet.DefaultRPCURL && !back.L2Enabled && back.L2RPCURL == Network.Shadownet.L2DefaultRPCURL);
        Assert.True(back.TzktURL == Network.Shadownet.DefaultTzKTURL && back.ExplorerURL is null);
    }
}

public class NetworkLinkSettingsTests
{
    [Fact]
    public void InactiveNodeEditsPreserveUnrelatedSavedOverrides()
    {
        var services = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["Shadownet"] = new Dictionary<string, string> { ["tzkt"] = "invalid saved service text", ["explorer"] = "", ["future-service"] = "preserve me" },
        };
        var original = new AppState
        {
            L2Enabled = new Dictionary<string, bool> { ["Shadownet"] = false },
            L2NodeURLs = new Dictionary<string, string> { ["Shadownet"] = "invalid saved L2 text" },
            ServiceURLs = services,
        };
        var store = new InMemoryAppStateStore(original);
        var model = new WalletViewModel([], new MockChainService(), stateStore: store);
        Assert.True(model.SetNode("https://node.example.org", Network.Shadownet));
        EtherlinkTestSupport.AssertMap(store.Load().NodeURLs, ("Shadownet", "https://node.example.org"));
        var custom = store.Load();
        model.UseDefaultNode(Network.Shadownet);
        foreach (var saved in new[] { custom, store.Load() })
        {
            EtherlinkTestSupport.AssertMap(saved.L2Enabled, ("Shadownet", false));
            EtherlinkTestSupport.AssertMap(saved.L2NodeURLs, ("Shadownet", "invalid saved L2 text"));
            EtherlinkTestSupport.AssertMap(saved.ServiceURLs!["Shadownet"],
                ("tzkt", "invalid saved service text"), ("explorer", ""), ("future-service", "preserve me"));
        }
        Assert.True(store.Load().NodeURLs is null && model.Network == Network.Mainnet);
    }

    [Fact]
    public void LinksAreKeptPerNetworkAndSurviveRelaunch()
    {
        var state = new InMemoryAppStateStore();
        var model = new WalletViewModel(WalletViewModel.SampleWallets, new MockChainService(), stateStore: state);
        Assert.Equal("Mainnet", model.Network.Name);

        // Another network, without switching: saved only where it differs from the default.
        Assert.False(model.SetServiceURL("not a url", Network.Service.Explorer, Network.Shadownet));
        Assert.True(model.SetServiceURL("explorer.example.org", Network.Service.Explorer, Network.Shadownet));
        Assert.Equal("explorer.example.org", model.ServiceURL(Network.Service.Explorer, Network.Shadownet)?.Host);
        EtherlinkTestSupport.AssertMap(state.Load().ServiceURLs!["Shadownet"], ("explorer", "https://explorer.example.org"));
        Assert.Equal(Network.Mainnet.ExplorerURL, model.Network.ExplorerURL);

        // Empty text turns a link off; it is saved as "" so the default does not come back.
        Assert.True(model.SetServiceURL("", Network.Service.Faucet, Network.Shadownet));
        Assert.Null(model.ServiceURL(Network.Service.Faucet, Network.Shadownet));
        Assert.Equal("", state.Load().ServiceURLs?["Shadownet"]["faucet"]);

        // The network in use: applied at once, and the chain service sees the new TzKT.
        Assert.True(model.SetServiceURL("https://tzkt.example.org/", Network.Service.Tzkt, Network.Mainnet));
        Assert.Equal(new Uri("https://tzkt.example.org"), model.Network.TzktURL);
        Assert.Equal(new Uri("https://tzkt.example.org"), model.Resolved(Network.Mainnet).TzktURL);

        // Relaunch over the same state: everything comes back, and Use Default clears it.
        var second = new WalletViewModel(WalletViewModel.SampleWallets, new MockChainService(), stateStore: state);
        Assert.Equal(new Uri("https://tzkt.example.org"), second.Network.TzktURL);
        second.SwitchNetwork(Network.Shadownet);
        Assert.Equal(new Uri("https://explorer.example.org/#op/ooX"), second.Network.ExplorerURLForOperation("ooX"));
        Assert.Null(second.Network.FaucetURL);
        second.UseDefaultServiceURL(Network.Service.Explorer, Network.Shadownet);
        second.UseDefaultServiceURL(Network.Service.Faucet, Network.Shadownet);
        Assert.True(second.Network.ExplorerURL == Network.Shadownet.ExplorerURL && second.Network.FaucetURL == Network.Shadownet.FaucetURL);
        Assert.False(state.Load().ServiceURLs?.ContainsKey("Shadownet") ?? false);
        second.UseDefaultServiceURL(Network.Service.Tzkt, Network.Mainnet);
        Assert.Null(state.Load().ServiceURLs);
    }

    [Fact]
    public void MainnetNeverGetsAFaucet()
    {
        var model = new WalletViewModel(WalletViewModel.SampleWallets, new MockChainService(), stateStore: new InMemoryAppStateStore());
        Assert.False(Network.Mainnet.Offers(Network.Service.Faucet));
        Assert.False(model.SetServiceURL("faucet.example.org", Network.Service.Faucet, Network.Mainnet));
        Assert.Null(model.Network.FaucetURL);
        Assert.Null(Network.Mainnet.Using(new Uri("https://faucet.example.org"), Network.Service.Faucet).FaucetURL);
        Assert.True(Network.Shadownet.Offers(Network.Service.Faucet) && Network.Custom.Offers(Network.Service.Faucet));
    }
}
