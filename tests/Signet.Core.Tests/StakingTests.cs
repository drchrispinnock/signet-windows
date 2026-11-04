using System.Text;
using System.Text.Json.Nodes;
using Signet.Models;
using Signet.Services;

namespace Signet.Tests;

public class StakingOperationTests
{
    private static readonly Address CaptainStake = MockChainService.CaptainStake;

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
        // Reactivating a deactivated baker is the same self-delegation as registering.
        Assert.Equal("registerDelegate", new StakingOperation.ReactivateBaker().BridgeKind);
        Assert.Empty(new StakingOperation.ReactivateBaker().BridgeArgument);
        Assert.Equal("Reactivate baker", new StakingOperation.ReactivateBaker().Title);
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

    [Fact]
    public void ParsesBakersFromTzKT()
    {
        var json = Encoding.UTF8.GetBytes("""
        [{"address":"tz3cqThj23Feu55KDynm7Vg81mCMpWDgzQZq","alias":"Tezos Foundation Baker 1","stakingBalance":24666552157412,"numDelegators":18,"stakersCount":4,"limitOfStakingOverBaking":9000000},
         {"address":"tz1irJKkXS2DBWkU1NnmFQx1c1L7pbGg4yhk","alias":"Coinbase Baker","stakingBalance":104790106393592,"numDelegators":2810,"stakersCount":0,"limitOfStakingOverBaking":null}]
        """);
        var bakers = TzKTService.ParseBakers(json);
        Assert.Equal(2, bakers.Count);
        Assert.Equal("Tezos Foundation Baker 1", bakers[0].Alias);
        Assert.True(bakers[0].AcceptsStaking);
        Assert.Equal(24666552.157412m, bakers[0].StakingBalance);
        Assert.Null(bakers[1].AcceptsStaking);
        Assert.Equal(2810, bakers[1].Delegators);
    }
}

public class StakingViewModelTests
{
    private static readonly OperationLimits Limits = new("421", 200, 0);

    [Fact]
    public async Task RefreshLoadsDelegateInfoAndOperationsRoundTrip()
    {
        var store = new InMemoryWalletStore();
        foreach (var wallet in Signet.ViewModels.WalletViewModel.SampleWallets) store.AddWithSecretKey(wallet, $"edsk{wallet.Alias}");
        var model = new Signet.ViewModels.WalletViewModel(chain: new MockChainService(), walletStore: store);
        await model.Refresh();
        Assert.Equal(MockChainService.CaptainStake, model.DelegateInfo?.Delegate);
        Assert.False(model.DelegateInfo?.IsBaker);

        var estimate = await model.EstimateStaking(new StakingOperation.Stake(1));
        Assert.True(estimate.Fee > 0);
        var result = await model.PerformStaking(new StakingOperation.Delegate(MockChainService.CaptainStake), estimate.Limits, null);
        Assert.Equal("ooMockStakingsetDelegate", result.Hash);
        Assert.Equal(9_000_000, result.Level);
        Assert.Equal("Captain Stake", (await model.Bakers()).FirstOrDefault()?.Alias);
    }

    [Fact]
    public async Task EncryptedWalletNeedsThePassword()
    {
        var vault = new Wallet("Vault", new Address("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb"), publicKey: "edpkV", keyKind: KeyKind.Encrypted);
        var store = new InMemoryWalletStore();
        store.AddWithSecretKey(vault, "edeskFAKE");
        var model = new Signet.ViewModels.WalletViewModel(chain: new MockChainService(), walletStore: store);
        await Assert.ThrowsAsync<ChainError.WrongPassphrase>(() => model.PerformStaking(new StakingOperation.FinalizeUnstake(), Limits, "nope"));
        var ok = await model.PerformStaking(new StakingOperation.FinalizeUnstake(), Limits, "correct horse");
        Assert.Equal("ooMockStakingfinalizeUnstake", ok.Hash);
    }
}

/// <summary>Helpers shared by the live Quantumnet tests.</summary>
internal static class Quantumnet
{
    /// <summary>
    /// Not upstream: xUnit runs classes in parallel, and two simultaneous payouts from the PQPark
    /// faucet clash in the mempool ("already contains a conflicting operation"). Tests that use
    /// that faucet share this collection so they run one at a time.
    /// </summary>
    public const string FaucetCollection = "Quantumnet faucet";

    /// <summary>A fresh tz1 funded with 20 tez from the PQPark faucet, or a failed assertion.</summary>
    public static async Task<(Wallet Wallet, KeyMaterial Material, TaquitoChainService Chain)> FundedTz1(string alias)
    {
        var chain = new TaquitoChainService(Network.Quantumnet);
        var material = await new KeyGenerator().Generate(AddressScheme.Tz1);
        var wallet = new Wallet(alias, new Address(material.Address), AddressScheme.Tz1, material.PublicKey, KeyKind.Unencrypted);
        await FaucetService.For(Network.Quantumnet)!.RequestTez(wallet.Address, 20);
        var funded = false;
        for (var i = 0; i < 30 && !funded; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);
            try { funded = (await chain.TezBalanceFor(wallet.Address)).Spendable >= 20; }
            catch (Exception) { }   // not on chain yet
        }
        Assert.True(funded, "the faucet transfer never showed up");
        return (wallet, material, chain);
    }
}

/// <summary>Live: a fresh tz1 on Quantumnet delegates to a baker, stakes, and registers as a baker itself.</summary>
[Collection(Quantumnet.FaucetCollection)]
public class QuantumnetStakingTests
{
    [Fact, Trait(Category.Name, Category.Network)]
    public async Task DelegateStakeAndRegisterOnQuantumnet()
    {
        var (wallet, material, chain) = await Quantumnet.FundedTz1("stake-test");
        var signer = new SigningKey.Secret(material.SecretKey, null, wallet.Address);
        async Task<string> Perform(StakingOperation operation) =>
            (await chain.PerformStaking(operation, wallet, signer, (await chain.EstimateStaking(operation, wallet)).Limits!)).Hash;

        var before = await chain.DelegateInfoFor(wallet.Address);
        Assert.True(before.Delegate is null && !before.IsBaker);

        var bakers = await chain.Bakers(10);
        var baker = bakers.FirstOrDefault(b => b.AcceptsStaking == true) ?? bakers.FirstOrDefault();
        Assert.True(baker is not null, "no bakers listed on Quantumnet");

        var d = await Perform(new StakingOperation.Delegate(baker.Address));
        await chain.WaitForConfirmation(d);
        var delegated = await chain.DelegateInfoFor(wallet.Address);
        Assert.Equal(baker.Address, delegated.Delegate);

        if (delegated.DelegateAcceptsStaking == true)
        {
            var s = await Perform(new StakingOperation.Stake(5));
            await chain.WaitForConfirmation(s);
            await Task.Delay(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken);
            var after = await chain.TezBalanceFor(wallet.Address);
            Assert.True(after.Staked >= 5, $"staked {after.Staked}");
        }
        else
        {
            TestContext.Current.SendDiagnosticMessage($"baker {baker.Alias ?? baker.Address.Value} does not accept staking; skipped the stake step");
        }

        var r = await Perform(new StakingOperation.RegisterAsBaker());
        await chain.WaitForConfirmation(r);
        var registered = await chain.DelegateInfoFor(wallet.Address);
        Assert.True(registered.IsBaker);
        Assert.Equal(wallet.Address, registered.Delegate);
    }
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
        var op = new StakingOperation.SetStakingParameters(q, MockChainService.CaptainStake);
        Assert.Equal("setDelegateParameters", op.BridgeKind);
        Assert.Equal(2_500_000, op.BridgeArgument["limitMillionth"]);
        Assert.Equal(MockChainService.CaptainStake.Value, op.BridgeArgument["source"]);
        Assert.True(TaquitoChainService.TaquitoEncodablePublicKey("edpkX") && !TaquitoChainService.TaquitoEncodablePublicKey("xmpkX"));
    }
}

/// <summary>
/// Live: a freshly registered Quantumnet baker sets its staking parameters, and if an XMSS (tz6)
/// public key is available from the local octez-client wallet, points its consensus key at it
/// through the raw node-forged path.
/// </summary>
[Collection(Quantumnet.FaucetCollection)]
public class QuantumnetBakerParametersTests
{
    [Fact, Trait(Category.Name, Category.Network)]
    public async Task SetsParametersAndMaybeATz6ConsensusKey()
    {
        var (wallet, material, chain) = await Quantumnet.FundedTz1("baker-test");
        var signer = new SigningKey.Secret(material.SecretKey, null, wallet.Address);
        async Task<string> Perform(StakingOperation operation) =>
            (await chain.PerformStaking(operation, wallet, signer, (await chain.EstimateStaking(operation, wallet)).Limits!)).Hash;

        var reg = await Perform(new StakingOperation.RegisterAsBaker());
        await chain.WaitForConfirmation(reg);

        var parameters = StakingParameters.FromUserValues(5, 10)!;
        var estimate = await chain.EstimateStaking(new StakingOperation.SetStakingParameters(parameters, wallet.Address), wallet);
        Assert.True(estimate.Fee > 0);
        var setHash = await Perform(new StakingOperation.SetStakingParameters(parameters, wallet.Address));
        await chain.WaitForConfirmation(setHash);
        await Task.Delay(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);
        var info = await chain.DelegateInfoFor(wallet.Address);
        Assert.True(info.IsBaker);
        Assert.True(info.Baker?.PendingStakingParameters.Any(p => p.LimitMillionth == 5_000_000 && p.EdgeBillionth == 100_000_000) == true,
                    $"pending: {string.Join(", ", info.Baker?.PendingStakingParameters ?? [])}");

        // tz6 consensus key via the raw path, if we have an xmpk public key to hand.
        var octezKeys = Path.Combine(TezosClientStore.OctezClientDirectory, "public_keys");
        var xmpk = File.Exists(octezKeys)
            ? (JsonNode.Parse(File.ReadAllText(octezKeys)) as JsonArray)?
                .Select(row => row?["value"]?["key"]?.GetValue<string>())
                .FirstOrDefault(k => k?.StartsWith("xmpk", StringComparison.Ordinal) == true)
            : null;
        if (xmpk is null)
        {
            TestContext.Current.SendDiagnosticMessage("no xmpk public key in .tezos-client; skipped the tz6 consensus-key step");
            return;
        }
        var est = await chain.EstimateStaking(new StakingOperation.UpdateConsensusKey(xmpk, null), wallet);
        Assert.True(est.Fee > 0);
        string hash;
        try
        {
            hash = await Perform(new StakingOperation.UpdateConsensusKey(xmpk, null));
        }
        catch (TaquitoBridge.BridgeError.JavaScript e) when (e.Message.Contains("consensus_key.active"))
        {
            // A consensus key serves one baker at a time; an earlier run of this test already took it.
            TestContext.Current.SendDiagnosticMessage($"xmpk {xmpk[..12]}… is already another baker's consensus key; skipped the tz6 step");
            return;
        }
        var level = await chain.WaitForConfirmation(hash);
        Assert.True(level > 0);
        await Task.Delay(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);
        var afterKey = await chain.DelegateInfoFor(wallet.Address);
        Assert.True((afterKey.Baker?.PendingConsensusKeys.Count ?? 0) > 0 || afterKey.Baker?.ConsensusKey is not null, $"baker after: {afterKey.Baker}");
    }
}

/// <summary>The bridge forges consensus and companion key updates itself and checks the node's bytes against its own.</summary>
public class RawForgingTests
{
    private const string Branch = "BLockGenesisGenesisGenesisGenesisGenesisf79b5d1CoW2";
    private static TaquitoBridge Bridge => TaquitoBridge.Shared;

    private static string JSON(params JsonObject[] contents) => new JsonArray(contents.Select(c => (JsonNode?)c.DeepClone()).ToArray()).ToJsonString();

    private static async Task<string> Ours(params JsonObject[] contents) =>
        (await Bridge.Call("forgeRawForTest", Branch, JSON(contents))).StringValue ?? throw new InvalidOperationException("no bytes");

    private static async Task<string> Taquitos(params JsonObject[] contents) =>
        (await Bridge.Call("taquitoForgeForTest", Branch, JSON(contents))).StringValue ?? throw new InvalidOperationException("no bytes");

    private static JsonObject Header(string kind, string source, string counter, string fee = "1234", string gas = "10100", string storage = "0") =>
        new() { ["kind"] = kind, ["source"] = source, ["fee"] = fee, ["counter"] = counter, ["gas_limit"] = gas, ["storage_limit"] = storage };

    [Theory]
    [InlineData(AddressScheme.Tz1)] [InlineData(AddressScheme.Tz2)] [InlineData(AddressScheme.Tz3)] [InlineData(AddressScheme.Tz4)] [InlineData(AddressScheme.Tz5)]
    public async Task ForgesRevealAndConsensusKeyUpdatesLikeTaquito(AddressScheme scheme)
    {
        var baker = await new KeyGenerator().Generate(AddressScheme.Tz1);
        var key = await new KeyGenerator().Generate(scheme);
        var reveal = Header("reveal", baker.Address, "7", fee: "0");
        reveal["public_key"] = baker.PublicKey;
        var update = Header("update_consensus_key", baker.Address, "8", fee: "300000", gas: "1040000", storage: "60000");
        update["pk"] = key.PublicKey;
        Assert.Equal(await Taquitos(reveal, update), await Ours(reveal, update));
    }

    [Theory]
    [InlineData(AddressScheme.Tz2)] [InlineData(AddressScheme.Tz3)] [InlineData(AddressScheme.Tz4)] [InlineData(AddressScheme.Tz5)]
    public async Task ForgesForEverySourceKind(AddressScheme scheme)
    {
        var baker = await new KeyGenerator().Generate(scheme);
        var key = await new KeyGenerator().Generate(AddressScheme.Tz1);
        var reveal = Header("reveal", baker.Address, "1", fee: "0");
        reveal["public_key"] = baker.PublicKey;
        var update = Header("update_companion_key", baker.Address, "2");
        update["pk"] = key.PublicKey;
        Assert.Equal(await Taquitos(reveal, update), await Ours(reveal, update));
    }

    [Fact]
    public async Task ForgesABLSProofLikeTaquito()
    {
        var baker = await new KeyGenerator().Generate(AddressScheme.Tz1);
        var bls = await new KeyGenerator().Generate(AddressScheme.Tz4);
        var proof = (await Bridge.Call("provePossession", new SigningKey.Secret(bls.SecretKey, null, new Address(bls.Address)).BridgeSpec))["proof"]?.StringValue!;
        Assert.StartsWith("BLsig", proof);
        var update = Header("update_consensus_key", baker.Address, "3");
        update["pk"] = bls.PublicKey;
        update["proof"] = proof;
        Assert.Equal(await Taquitos(update), await Ours(update));
    }

    [Fact]
    public async Task ZarithAndLargeNumbersMatchTaquito()
    {
        var baker = await new KeyGenerator().Generate(AddressScheme.Tz1);
        var update = Header("update_consensus_key", baker.Address, "123456789", fee: "99999999999", gas: "1040000", storage: "60000");
        update["pk"] = baker.PublicKey;
        Assert.Equal(await Taquitos(update), await Ours(update));
    }

    /// <summary>The audit's attack: the node returns a forged transfer where a consensus-key update was asked for.</summary>
    [Fact]
    public async Task RefusesNodeBytesThatDifferFromOurs()
    {
        var baker = await new KeyGenerator().Generate(AddressScheme.Tz1);
        var update = Header("update_consensus_key", baker.Address, "5");
        update["pk"] = baker.PublicKey;
        var json = JSON(update);
        var honest = await Ours(update);

        var transfer = Header("transaction", baker.Address, "5", fee: "1234", gas: "1500");
        transfer["amount"] = "50000000";
        transfer["destination"] = "tz1KqTpEZ7Yob7QbPE4Hy4Wo8fHG8LhKxZSx";
        var substituted = await Taquitos(transfer);
        Assert.NotEqual(honest, substituted);

        await Assert.ThrowsAnyAsync<Exception>(() => Bridge.Call("verifyRawForgingForTest", Branch, json, substituted));
        // A single changed byte (fee, branch, key…) is refused too; the honest bytes pass.
        // The byte at offset 35 lies inside the (random) source address; write whatever it is not.
        var tampered = honest[..70] + (honest[70..72] == "ff" ? "00" : "ff") + honest[72..];
        await Assert.ThrowsAnyAsync<Exception>(() => Bridge.Call("verifyRawForgingForTest", Branch, json, tampered));
        await Assert.ThrowsAnyAsync<Exception>(() => Bridge.Call("verifyRawForgingForTest", Branch, json, honest + "00"));
        await Assert.ThrowsAnyAsync<Exception>(() => Bridge.Call("verifyRawForgingForTest", Branch, json, ""));
        Assert.Equal(honest, (await Bridge.Call("verifyRawForgingForTest", Branch, json, honest.ToUpperInvariant())).StringValue);
    }

    [Fact]
    public async Task RefusesKindsItCannotForge()
    {
        var baker = await new KeyGenerator().Generate(AddressScheme.Tz1);
        var transfer = Header("transaction", baker.Address, "5");
        transfer["amount"] = "1";
        transfer["destination"] = baker.Address;
        await Assert.ThrowsAnyAsync<Exception>(() => Ours(transfer));
        var bad = Header("update_consensus_key", baker.Address, "5");
        bad["pk"] = "edpkNotARealKeyAtAll";
        await Assert.ThrowsAnyAsync<Exception>(() => Ours(bad));
    }

    [Fact(Skip = "Live and reads the user's ~/.tezos-client for an xmpk key (upstream: .realWalletState)")]
    public void ForgesAnXMSSKeyLikeTheQuantumnetNode() { }
}
