using Signet.Models;
using Signet.Services;

namespace Signet.Tests;

/// <summary>
/// The whole tz5 story on a live testnet: make an ML-DSA-44 key, fund it from the faucet, let the
/// node see it, estimate and send tez from it (reveal + transaction, both ML-DSA signed).
///
/// Known blocker (2026-10-08, upstream): the Shadownet faucet rejects tz5 destinations with
/// "The address 'tz5…' is invalid", so this fails at the funding step until the faucet is fixed.
/// </summary>
public class PostQuantumSendTests
{
    [Fact, Trait(Category.Name, Category.Network)]
    public Task Tz5WalletCanReceiveAndSendOnShadownet() =>
        PostQuantumSend.Run(Network.Shadownet, new FaucetService(Network.Shadownet.FaucetURL!), polls: 24, every: TimeSpan.FromSeconds(5));
}

/// <summary>The same story on Quantumnet, whose faucet accepts tz5 and whose blocks are 6 s apart.</summary>
[Collection(Quantumnet.FaucetCollection)]
public class QuantumnetPostQuantumSendTests
{
    [Fact, Trait(Category.Name, Category.Network)]
    public Task Tz5WalletCanReceiveAndSendOnQuantumnet() =>
        PostQuantumSend.Run(Network.Quantumnet, FaucetService.For(Network.Quantumnet)!, polls: 30, every: TimeSpan.FromSeconds(4));
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

        var hash = await chain.SendTransfer(wallet, new SigningKey.Secret(quantum.SecretKey, null), destination, 0.5m);
        Assert.StartsWith("o", hash);
        Assert.True(await chain.WaitForConfirmation(hash) > 0);
    }
}
