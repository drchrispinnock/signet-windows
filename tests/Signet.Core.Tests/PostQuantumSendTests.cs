using Signet.Models;
using Signet.Services;

namespace Signet.Tests;

/// <summary>
/// The whole tz5 story on a live testnet: make an ML-DSA-44 key, fund it from the faucet, let the
/// node see it, estimate and send tez from it (reveal + transaction, both ML-DSA signed). tz5 exists
/// only on Quantumnet (not on Shadownet), whose blocks are 6 s apart.
/// </summary>
[Collection(Quantumnet.FaucetCollection)]
public class QuantumnetPostQuantumSendTests
{
    [Fact, Trait(Category.Name, Category.Network)]
    public Task Tz5WalletCanReceiveAndSendOnQuantumnet() =>
        PostQuantumSend.Run(Network.Quantumnet, FaucetService.For(Network.Quantumnet)!, polls: 30, every: TimeSpan.FromSeconds(4));
}

/// <summary>
/// The same story for a tz4 (BLS) key on Shadownet: reveal + transaction, both BLS signed. The
/// reveal carries a proof of possession the node checks even in simulation, so the estimate
/// first asks for one (<c>ChainError.ProofOfPossessionRequired</c>) and succeeds once the wallet
/// carries the proof its key made.
/// </summary>
public class BLSSendTests
{
    [Fact, Trait(Category.Name, Category.Network)]
    public async Task Tz4WalletCanReceiveAndSendOnShadownet()
    {
        var generated = await new KeyGenerator().Generate(AddressScheme.Tz4);
        Assert.StartsWith("tz4", generated.Address);
        var wallet = new Wallet("bls-test", new Address(generated.Address), AddressScheme.Tz4, generated.PublicKey, KeyKind.Unencrypted);

        var faucet = FaucetService.For(Network.Shadownet)!;
        Assert.StartsWith("o", await faucet.RequestTez(wallet.Address, 2, cancellation: TestContext.Current.CancellationToken));

        var chain = new TaquitoChainService(Network.Shadownet);
        TezBalance? balance = null;
        for (var i = 0; i < 24 && balance is null; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            try { if (await chain.TezBalanceFor(wallet.Address) is { Spendable: > 0 } b) balance = b; }
            catch (Exception) { }   // not on chain yet
        }
        Assert.True(balance is not null, "faucet transfer to the tz4 address never showed up");
        Assert.True(balance.Spendable >= 2);

        var destination = new Address("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb");
        var signer = new SigningKey.Secret(generated.SecretKey, null, wallet.Address);
        await Assert.ThrowsAsync<ChainError.ProofOfPossessionRequired>(() => chain.EstimateTransfer(wallet, destination, 0.5m));
        wallet = wallet with { ProofOfPossession = await chain.ProofOfPossession(signer) };
        Assert.StartsWith("BLsig", wallet.ProofOfPossession);
        var estimate = await chain.EstimateTransfer(wallet, destination, 0.5m);
        Assert.True(estimate.Fee > 0);
        Assert.NotNull(estimate.Limits?.RevealFeeMutez);

        var hash = (await chain.SendTransfer(wallet, signer, destination, 0.5m, estimate.Limits!)).Hash;
        Assert.StartsWith("o", hash);
        Assert.True(await chain.WaitForConfirmation(hash) > 0);
    }
}

internal static class PostQuantumSend
{
    public static async Task Run(Network network, FaucetService faucet, int polls, TimeSpan every)
    {
        var quantum = await new KeyGenerator().Generate(AddressScheme.Tz5);
        Assert.StartsWith("tz5", quantum.Address);
        var wallet = new Wallet("pq-test", new Address(quantum.Address), AddressScheme.Tz5, quantum.PublicKey, KeyKind.Unencrypted);
        var chain = new TaquitoChainService(network);

        var fundingHash = await faucet.RequestTez(wallet.Address, 2);
        Assert.StartsWith("o", fundingHash);

        TezBalance? balance = null;
        for (var i = 0; i < polls && balance is null; i++)
        {
            await Task.Delay(every, TestContext.Current.CancellationToken);
            try
            {
                var b = await chain.TezBalanceFor(wallet.Address);
                if (b.Spendable > 0) balance = b;
            }
            catch (Exception) { }   // not on chain yet
        }
        Assert.True(balance is not null, "faucet transfer to the tz5 address never showed up");
        Assert.True(balance.Spendable >= 2);

        var destination = new Address("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb");
        var estimate = await chain.EstimateTransfer(wallet, destination, 0.5m);
        Assert.True(estimate.Fee > 0);

        var hash = (await chain.SendTransfer(wallet, new SigningKey.Secret(quantum.SecretKey, null, wallet.Address), destination, 0.5m, estimate.Limits!)).Hash;
        Assert.StartsWith("o", hash);
        Assert.True(await chain.WaitForConfirmation(hash) > 0);
    }
}
