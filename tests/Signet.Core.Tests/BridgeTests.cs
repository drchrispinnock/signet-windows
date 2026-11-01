using Signet.Models;
using Signet.Services;
using Signet.ViewModels;

namespace Signet.Tests;

/// <summary>The Etherlink bridge (0.9.1): where it is offered and how a deposit is carried out.</summary>
public class BridgeNetworkTests
{
    [Fact]
    public void ShippedNetworksKnowTheirBridgeOthersDoNot()
    {
        Assert.False(Network.Mainnet.CanDepositToL2);   // Layer 2 is off by default
        Assert.True(Network.Mainnet.L2On().CanDepositToL2 && Network.Mainnet.L2BridgeHelper?.Value == "KT1Wj8SUGmnEPFqyahHAcjcNQwe6YGhEXJb5" && Network.Mainnet.L2Rollup == "sr1Ghq66tYK9y3r8CC1Tf8i8m5nxh8nTvZEf");
        Assert.True(Network.Shadownet.L2On().CanDepositToL2 && Network.Shadownet.L2BridgeHelper?.Value == "KT19aBsSWvWtvEkbiqReJnD8UzQMWcD8SHUD" && Network.Shadownet.L2Rollup == "sr19fMYrr5C4qqvQqQrDSjtP31GcrWjodzvg");
        // A user-supplied Layer 2 node brings no bridge with it.
        var bakingnet = Network.Bakingnet.UsingL2(true, new Uri("https://l2.example.org"));
        Assert.True(bakingnet.HasL2 && !bakingnet.CanDepositToL2);
        // Layer 2 off hides the bridge too.
        Assert.False(Network.Mainnet.L2On().UsingL2(false, Network.Mainnet.L2RPCURL).CanDepositToL2);
    }
}

public class BridgeModelTests
{
    private static readonly Wallet Tz4 = new("bls", new Address("tz4HVR6aty9KwsQFHh81C1G7gBdhxT8kuytm"), publicKey: "BLpk", keyKind: KeyKind.Unencrypted);
    private static readonly Wallet Watch = new("watch", new Address("tz3WXYtyDUNL91qfiCJtVUX746QpNv5i5ve5"));

    [Fact]
    public void BridgeIsOfferedOnlyWhereItCanWork()
    {
        var model = new WalletViewModel([.. WalletViewModel.SampleWallets, Tz4, Watch], new MockChainService());
        Assert.True(model.Network.Name == "Mainnet" && !model.CanBridge);   // Layer 2 off by default
        model.SetL2Enabled(true, Network.Mainnet);
        Assert.True(model.CanBridge);                                       // tz1 we can sign for
        model.Select(WalletViewModel.SampleWallets[1]);
        Assert.True(model.CanBridge);                                       // tz2, encrypted: password at confirm time
        model.Select(Tz4);
        Assert.False(model.CanBridge);                                      // no tz4 on the rollup yet
        model.Select(Watch);
        Assert.False(model.CanBridge);                                      // nothing to sign with
        model.Select(WalletViewModel.SampleWallets[0]);
        model.SetL2Enabled(false, Network.Mainnet);
        Assert.False(model.CanBridge);
        model.SetL2Enabled(true, Network.Mainnet);
        model.SwitchNetwork(Network.Bakingnet);
        Assert.False(model.CanBridge);                                      // no bridge shipped there
    }

    [Fact]
    public async Task DepositNeedsAnEstimateAPositiveAmountAndTheRightPassword()
    {
        var store = new InMemoryWalletStore();
        store.AddWithSecretKey(WalletViewModel.SampleWallets[0], "edskSAMPLE");
        store.AddWithSecretKey(WalletViewModel.SampleWallets[1], "edeskSAMPLE");
        var model = new WalletViewModel(WalletViewModel.SampleWallets, new MockChainService(), walletStore: store);
        model.SetL2Enabled(true, Network.Mainnet);
        var approved = await model.EstimateBridgeDeposit(2);
        var estimate = approved.Estimate;
        Assert.True(estimate.Limits is not null && estimate.Total == 2 + estimate.Fee);
        await Assert.ThrowsAnyAsync<SendViewModel.SendError>(() => model.EstimateBridgeDeposit(0));
        await Assert.ThrowsAsync<WalletViewModel.WalletError.ContextChanged>(() => model.BridgeToEtherlink(3, approved, null));
        await Assert.ThrowsAsync<WalletViewModel.WalletError.NotEstimated>(() => model.BridgeToEtherlink(2, null, null));
        await Assert.ThrowsAnyAsync<SendViewModel.SendError>(() => model.BridgeToEtherlink(-1, approved, null));
        var (hash, level) = await model.BridgeToEtherlink(2, approved, null);
        Assert.True(hash == "ooMockDeposit" && level > 0);

        model.Select(WalletViewModel.SampleWallets[1]);   // encrypted
        // The estimate was for the other account (S31): it must be made again for this one.
        await Assert.ThrowsAsync<WalletViewModel.WalletError.ContextChanged>(() => model.BridgeToEtherlink(1, approved, "correct horse"));
        var again = await model.EstimateBridgeDeposit(1);
        await Assert.ThrowsAsync<ChainError.WrongPassphrase>(() => model.BridgeToEtherlink(1, again, "wrong"));
        await model.BridgeToEtherlink(1, again, "correct horse");

        model.SetL2Enabled(false, Network.Mainnet);
        await Assert.ThrowsAsync<WalletViewModel.WalletError.ContextChanged>(() => model.BridgeToEtherlink(1, again, "correct horse"));
    }

    /// <summary>Every part of the context a deposit was estimated in must still hold when it is sent (S31).</summary>
    [Fact]
    public async Task DepositEstimatesAreBoundToAccountNetworkNodeAndDirectory()
    {
        var store = new InMemoryWalletStore();
        store.AddWithSecretKey(WalletViewModel.SampleWallets[0], "edskSAMPLE");
        var model = new WalletViewModel(WalletViewModel.SampleWallets, new MockChainService(), walletStore: store);
        model.SetL2Enabled(true, Network.Mainnet);
        (string What, Action Change)[] changes =
        [
            ("network", () => { model.SwitchNetwork(Network.Shadownet); model.SetL2Enabled(true, Network.Shadownet); }),
            ("layer 1 node", () => model.SetNode("https://rpc.example.org")),
            ("layer 2 node", () => model.SetL2Node("https://l2.example.org", Network.Mainnet)),
            ("layer 2 off and on again", () => { model.SetL2Enabled(false, Network.Mainnet); model.SetL2Enabled(true, Network.Mainnet); }),
            ("account", () => model.Select(WalletViewModel.SampleWallets[1])),
            ("account away and back", () => { model.Select(WalletViewModel.SampleWallets[1]); model.Select(WalletViewModel.SampleWallets[0]); }),
            ("network away and back", () => { model.SwitchNetwork(Network.Shadownet); model.SwitchNetwork(Network.Mainnet); }),
        ];
        foreach (var (what, change) in changes)
        {
            model.SwitchNetwork(Network.Mainnet);
            model.Select(WalletViewModel.SampleWallets[0]);
            var approved = await model.EstimateBridgeDeposit(1);
            change();
            var refused = await Record.ExceptionAsync(() => model.BridgeToEtherlink(1, approved, null));
            Assert.True(refused is WalletViewModel.WalletError.ContextChanged, what);
        }
        model.UseDefaultNode(); model.UseDefaultL2Node(Network.Mainnet); model.Select(WalletViewModel.SampleWallets[0]);
        // A context that moved while the node was being asked is refused at the estimate itself.
        WalletViewModel? moving = null;
        var slow = new SlowEstimateChain(() => moving?.Select(WalletViewModel.SampleWallets[1]));
        moving = new WalletViewModel(WalletViewModel.SampleWallets, slow, walletStore: store);
        moving.SetL2Enabled(true, Network.Mainnet);
        await Assert.ThrowsAsync<WalletViewModel.WalletError.ContextChanged>(() => moving.EstimateBridgeDeposit(1));
    }

    /// <summary>A chain whose deposit estimate lets the test move the model before answering.</summary>
    private sealed class SlowEstimateChain(Action during) : TestChainService
    {
        public override Task<TezBalance> TezBalanceFor(Address address) => Task.FromResult(new TezBalance(Spendable: 10));
        public override Task<TransferEstimate> EstimateEtherlinkDeposit(Wallet wallet, decimal amount)
        {
            during();
            return Task.FromResult(new TransferEstimate(0.001m, 0, amount + 0.001m, 0, 0, new OperationLimits("1000", 0, 0)));
        }
    }
}

/// <summary>
/// Live: a fresh Shadownet account deposits 1 tez to itself on Etherlink through the bridge and
/// the rollup credits it.
/// </summary>
public class ShadownetBridgeTests
{
    [Fact, Trait(Category.Name, Category.Network)]
    public async Task DepositsTezToTheSameAccountOnEtherlink()
    {
        var chain = new TaquitoChainService(Network.Shadownet.L2On());
        var (wallet, material) = await EtherlinkTestSupport.FundedShadownetTz1("bridge-test", chain);
        var signer = new SigningKey.Secret(material.SecretKey, null, wallet.Address);
        Assert.Equal(0, await chain.EtherlinkBalanceFor(wallet.Address));

        var estimate = await chain.EstimateEtherlinkDeposit(wallet, 1);
        Assert.True(estimate.Fee > 0 && estimate.Limits is not null);
        var sent = await chain.SendEtherlinkDeposit(wallet, signer, 1, estimate.Limits!);
        TestContext.Current.SendDiagnosticMessage($"bridge deposit {sent.Hash}");
        Assert.True(await chain.WaitForConfirmation(sent.Hash) > 0);

        Assert.True(await EtherlinkTestSupport.WaitForEtherlinkBalance(chain, wallet.Address, 1), "the rollup never credited the deposit");
        Assert.Equal(1, await chain.EtherlinkBalanceFor(wallet.Address));
        Assert.True((await chain.TezBalanceFor(wallet.Address)).Spendable < 9);
    }
}

/// <summary>The Send sheet keeps the network it shows and the service it signs through together (S31).</summary>
public class SendSideContextTests
{
    /// <summary>A chain service that remembers which network it was built for.</summary>
    private sealed class LabelledChain(Network network) : TestChainService
    {
        public Network Network { get; } = network;
        public override Task<TezBalance> TezBalanceFor(Address address) => Task.FromResult(new TezBalance(Spendable: 10));
    }

    [Fact]
    public void CapturedSidesSurviveSettingsChanges()
    {
        var model = new WalletViewModel(WalletViewModel.SampleWallets, chainFactory: n => new LabelledChain(n));
        model.SetL2Enabled(true, Network.Mainnet);
        var (layer1, layer2) = SendSideContext.Capture(model);
        Assert.NotNull(layer2);
        var etherlink = layer2;
        Assert.True(layer1.Network.Name == "Mainnet" && layer1.Network.HasL2 && (layer1.Chain as LabelledChain)?.Network == layer1.Network && !layer1.ImplicitAccountsOnly);
        Assert.True(etherlink.Network.LayerNumber == 2 && (etherlink.Chain as LabelledChain)?.Network == etherlink.Network && etherlink.ImplicitAccountsOnly);

        // Layer 2 turned off, the network switched and the node changed after the sheet opened:
        // the captured pair is untouched and still matched, and the live model no longer offers Layer 2.
        model.SetL2Enabled(false, Network.Mainnet);
        Assert.True(model.Layer2Chain is null && model.Network.Layer2 is null);
        model.SwitchNetwork(Network.Shadownet);
        model.SetNode("https://rpc.example.org");
        Assert.True((etherlink.Chain as LabelledChain)?.Network == etherlink.Network && etherlink.Network.Chain == "etherlink-mainnet");
        Assert.True((layer1.Chain as LabelledChain)?.Network == layer1.Network && layer1.Network.Name == "Mainnet");

        // With Layer 2 off when the sheet opens there is no Etherlink side at all: no fallback to layer 1.
        Assert.Null(SendSideContext.Capture(model).Layer2);
    }
}
