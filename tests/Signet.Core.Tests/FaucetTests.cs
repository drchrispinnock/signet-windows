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
        var (nonce, solution) = FaucetService.Solve(challenge, cancellation: TestContext.Current.CancellationToken);
        Assert.StartsWith("000", solution);
        Assert.Equal(64, solution.Length);
        // Reproducible: the same nonce gives the same hash.
        Assert.Equal(nonce, FaucetService.Solve(challenge, cancellation: TestContext.Current.CancellationToken).Nonce);
    }

    /// <summary>
    /// S21: the faucet is untrusted. A negative difficulty would reach <c>new string('0', n)</c> and
    /// throw; a huge one would run forever (65 can never match a 64-digit hash). Both are refused
    /// before any hashing.
    /// </summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(9)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(1_000_000)]
    [InlineData(int.MaxValue)]
    public void RefusesUnreasonableDifficulty(int difficulty)
    {
        var challenge = new FaucetService.Challenge("abc", difficulty, 1, 1);
        Assert.ThrowsAny<FaucetService.FaucetError>(() => FaucetService.Solve(challenge, cancellation: TestContext.Current.CancellationToken));
        Assert.ThrowsAny<FaucetService.FaucetError>(() => FaucetService.Validate(challenge));
        var json = Encoding.UTF8.GetBytes($$"""{"challenge":"c1","difficulty":{{difficulty}},"challengeCounter":1,"challengesNeeded":1}""");
        Assert.ThrowsAny<FaucetService.FaucetError>(() => FaucetService.ChallengeFrom(json));
    }

    [Fact]
    public void RefusesUnreasonableCountsAndLengths()
    {
        static FaucetService.Challenge Challenge(string json, FaucetService.Challenge? after = null) => FaucetService.ChallengeFrom(Encoding.UTF8.GetBytes(json), after);
        Assert.ThrowsAny<FaucetService.FaucetError>(() => Challenge("""{"challenge":"c","difficulty":1,"challengeCounter":0,"challengesNeeded":1}"""));
        Assert.ThrowsAny<FaucetService.FaucetError>(() => Challenge("""{"challenge":"c","difficulty":1,"challengeCounter":1,"challengesNeeded":0}"""));
        Assert.ThrowsAny<FaucetService.FaucetError>(() => Challenge("""{"challenge":"c","difficulty":1,"challengeCounter":1,"challengesNeeded":1001}"""));
        Assert.ThrowsAny<FaucetService.FaucetError>(() => Challenge("""{"challenge":"c","difficulty":1,"challengeCounter":99,"challengesNeeded":5}"""));
        Assert.ThrowsAny<FaucetService.FaucetError>(() => Challenge("""{"challenge":"","difficulty":1,"challengeCounter":1,"challengesNeeded":1}"""));
        var longChallenge = new string('x', FaucetService.Limits.ChallengeLength + 1);
        Assert.ThrowsAny<FaucetService.FaucetError>(() => Challenge($$"""{"challenge":"{{longChallenge}}","difficulty":1,"challengeCounter":1,"challengesNeeded":1}"""));
        // Fractions, booleans and absurd numbers are not counters.
        Assert.ThrowsAny<FaucetService.FaucetError>(() => Challenge("""{"challenge":"c","difficulty":1.5,"challengeCounter":1,"challengesNeeded":1}"""));
        Assert.ThrowsAny<FaucetService.FaucetError>(() => Challenge("""{"challenge":"c","difficulty":true,"challengeCounter":1,"challengesNeeded":1}"""));
        Assert.ThrowsAny<FaucetService.FaucetError>(() => Challenge("""{"challenge":"c","difficulty":1,"challengeCounter":1e30,"challengesNeeded":1}"""));
        // The sequence must move forward and never grow.
        var first = Challenge("""{"challenge":"c1","difficulty":1,"challengeCounter":1,"challengesNeeded":3}""");
        Assert.Equal(3, Challenge("""{"challenge":"c2","difficulty":1,"challengeCounter":2}""", first).ChallengesNeeded);
        Assert.ThrowsAny<FaucetService.FaucetError>(() => Challenge("""{"challenge":"c2","difficulty":1,"challengeCounter":1}""", first));
        Assert.ThrowsAny<FaucetService.FaucetError>(() => Challenge("""{"challenge":"c2","difficulty":1,"challengeCounter":2,"challengesNeeded":50}""", first));
        // Faucet limits and amounts must be finite and positive.
        Assert.ThrowsAny<FaucetService.FaucetError>(() => FaucetService.Checked(new FaucetService.Info("tz1", true, -1, 10)));
        Assert.ThrowsAny<FaucetService.FaucetError>(() => FaucetService.Checked(new FaucetService.Info("tz1", true, 10, 1)));
        Assert.ThrowsAny<FaucetService.FaucetError>(() => FaucetService.Checked(new FaucetService.Info("tz1", true, 1, double.PositiveInfinity)));
        Assert.True(!FaucetService.IsReasonableAmount(double.NaN) && !FaucetService.IsReasonableAmount(0) && FaucetService.IsReasonableAmount(100));
    }

    /// <summary>A hard challenge stops when it is cancelled or its deadline passes, instead of burning CPU forever.</summary>
    [Fact(Timeout = 60_000)]
    public async Task HardChallengesAreCancellableAndBounded()
    {
        var hard = new FaucetService.Challenge("never", 8, 1, 1);
        using var cancel = new CancellationTokenSource();
        var worker = Task.Run(() => FaucetService.Solve(hard, cancellation: cancel.Token), TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        cancel.Cancel();
        await Assert.ThrowsAsync<FaucetService.FaucetError.Cancelled>(() => worker);

        var started = DateTime.UtcNow;
        Assert.Throws<FaucetService.FaucetError.TimedOut>(() => FaucetService.Solve(hard, DateTime.UtcNow.AddSeconds(0.3), TestContext.Current.CancellationToken));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
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
        var info = await faucet.GetInfo(TestContext.Current.CancellationToken);
        Assert.True(info.MinTez >= 0);

        var hash = await faucet.RequestTez(fresh, info.MinTez, cancellation: TestContext.Current.CancellationToken);
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
        var info = await faucet.GetInfo(TestContext.Current.CancellationToken);
        Assert.StartsWith("tz", info.FaucetAddress);
        Assert.Equal(1, info.MinTez);
        var header = await NodeMonitor.HttpProbe(Network.Quantumnet.RpcURL.AppendingPathComponent("chains/main/blocks/head/header"));
        Assert.NotEqual(NodeStatus.LightColor.Red, NodeMonitor.Evaluate(header, null, DateTimeOffset.UtcNow).Light);
    }
}
