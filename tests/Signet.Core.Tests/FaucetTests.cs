using System.Text;
using Signet.Models;
using Signet.Services;

namespace Signet.Tests;

public class FaucetServiceTests
{
    [Fact]
    public void SolvesAProofOfWorkChallenge()
    {
        var challenge = new FaucetService.Challenge("abc123", 3, 1, 1);
        var (nonce, solution) = FaucetService.Solve(challenge);
        Assert.StartsWith("000", solution);
        Assert.Equal(64, solution.Length);
        // Reproducible: the same nonce gives the same hash.
        Assert.Equal(nonce, FaucetService.Solve(challenge).Nonce);
    }

    [Fact]
    public void ParsesChallengeAndHashReplies()
    {
        static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);
        var parsed = FaucetService.ChallengeFrom(Bytes("""{"challenge":"c1","difficulty":4,"challengeCounter":1,"challengesNeeded":5}"""));
        Assert.Equal(new FaucetService.Challenge("c1", 4, 1, 5), parsed);

        var later = Bytes("""{"challenge":"c2","difficulty":4,"challengeCounter":2}""");
        Assert.Equal(2, FaucetService.ChallengeFrom(later).ChallengeCounter);

        Assert.Equal("ooABC", FaucetService.TxHash(Bytes("""{"txHash":"ooABC"}""")));
        Assert.ThrowsAny<FaucetService.FaucetError>(() => FaucetService.TxHash(later));
        Assert.ThrowsAny<FaucetService.FaucetError>(() => FaucetService.ChallengeFrom(Bytes("""{"message":"Too many requests"}""")));
    }

    [Fact, Trait(Category.Name, Category.Network)]
    public async Task ShadownetFaucetFundsAFreshAddress()
    {
        var fresh = new Address((await new KeyGenerator().Generate(AddressScheme.Tz1)).Address);
        var faucet = new FaucetService(Network.Shadownet.FaucetURL!);
        var info = await faucet.GetInfo();
        Assert.True(info.MinTez >= 0);

        var hash = await faucet.RequestTez(fresh, info.MinTez);
        Assert.StartsWith("o", hash);
        Assert.Equal(51, hash.Length);
    }
}

public class QuantumnetTests
{
    [Fact]
    public void QuantumnetIsConfiguredOffTeztnets()
    {
        var q = Network.Quantumnet;
        Assert.Equal("https://quantumnet.pqpark.dal.nomadic-labs.com/rpc", q.RpcURL.AbsoluteString());
        Assert.Equal("quantumnet-tzkt.pqpark.dal.nomadic-labs.com", q.TzktURL?.Host);
        Assert.Equal(Network.FaucetKindValue.Pqpark, q.FaucetKind);
        Assert.Equal("https://quantumnet-tzkt.pqpark.dal.nomadic-labs.com/#op/ooX", q.ExplorerURLForOperation("ooX")?.AbsoluteString());
        Assert.Equal("https://tzkt.io/ooX", Network.Mainnet.ExplorerURLForOperation("ooX")?.AbsoluteString());
        Assert.Equal(new Uri("https://shadownet.tzkt.io/ooX"), Network.Shadownet.ExplorerURLForOperation("ooX"));
        Assert.Contains("Quantumnet", Network.All.Select(n => n.Name));
    }

    [Fact, Trait(Category.Name, Category.Network)]
    public async Task QuantumnetNodeFaucetAndIndexerAnswer()
    {
        var faucet = FaucetService.For(Network.Quantumnet);
        Assert.NotNull(faucet);
        var info = await faucet.GetInfo();
        Assert.StartsWith("tz", info.FaucetAddress);
        Assert.Equal(1, info.MinTez);
        var header = await NodeMonitor.HttpProbe(Network.Quantumnet.RpcURL.AppendingPathComponent("chains/main/blocks/head/header"));
        Assert.NotEqual(NodeStatus.LightColor.Red, NodeMonitor.Evaluate(header, null, DateTimeOffset.UtcNow).Light);
    }
}
