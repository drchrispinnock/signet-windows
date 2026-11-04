using System.Collections.Concurrent;
using Signet.Models;
using Signet.Services;
using Signet.ViewModels;

namespace Signet.Tests;

/// <summary>
/// S16: a load started for one account or network never lands on the screen of another.
/// The fixture answers only when the test says so, so ordering is stated, never timed (F25).
/// </summary>
public class RefreshContextTests
{
    private static readonly Wallet A = new("A", new Address("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb"), keyKind: KeyKind.Unencrypted);
    private static readonly Wallet B = new("B", new Address("tz2BFTyPeYRzxd5aiBchbXN3WCZhx7BqbMBq"), keyKind: KeyKind.Unencrypted);

    /// <summary>
    /// One pair of latches per (account, network) request: <c>Started</c> is opened by the chain when
    /// the balance request arrives, <c>Release</c> by the test when the answer may go out.
    /// </summary>
    private sealed class Gates
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> started = new(), release = new();
        private static string Key(Address address, Network network) => $"{address.Value}@{network.Name}";
        private static TaskCompletionSource Latch(ConcurrentDictionary<string, TaskCompletionSource> map, string key) =>
            map.GetOrAdd(key, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        public TaskCompletionSource Started(Address address, Network network) => Latch(started, Key(address, network));
        public TaskCompletionSource Release(Address address, Network network) => Latch(release, Key(address, network));
    }

    /// <summary>Reports each balance request and holds its answer until released; names its network in the domain.</summary>
    private sealed class GatedChain(Network network, Gates gates) : TestChainService
    {
        public override async Task<TezBalance> TezBalanceFor(Address address)
        {
            gates.Started(address, network).TrySetResult();
            await gates.Release(address, network).Task;
            return new TezBalance(address == A.Address ? 5 : 7);
        }
        public override Task<IReadOnlyList<string>> DomainsFor(Address address) =>
            Task.FromResult<IReadOnlyList<string>>([$"{network.Name.ToLowerInvariant()}.tez"]);
    }

    /// <summary>
    /// Waits until <c>condition</c> holds; a refresh started by <c>Select</c> or a network switch runs
    /// in its own task, so its publication can only be waited for, not awaited.
    /// </summary>
    private static async Task Settle(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.True(condition(), "the model did not settle in time");
    }

    [Fact]
    public async Task ASlowLoadForThePreviousAccountIsDropped()
    {
        var gates = new Gates();
        var model = new WalletViewModel(wallets: [A, B], chainFactory: n => new GatedChain(n, gates));
        var slow = model.Refresh();                                         // A's load starts…
        await gates.Started(A.Address, Network.Mainnet).Task;
        Assert.True(model.IsLoading);
        model.Select(B);                                                    // …and is superseded.
        // Windows: Select starts B's load synchronously up to its first await, so IsLoading is already B's (Swift's task starts later).
        Assert.True(model.TezBalance is null, "switching clears the dashboard at once");
        await gates.Started(B.Address, Network.Mainnet).Task;
        gates.Release(B.Address, Network.Mainnet).TrySetResult();           // B answers first.
        await Settle(() => model.TezBalance is not null);
        Assert.True(model.TezBalance?.Spendable == 7 && model.Domains.SequenceEqual(["mainnet.tez"]) && !model.IsLoading);
        gates.Release(A.Address, Network.Mainnet).TrySetResult();           // A's answer arrives late.
        await slow;
        Assert.Equal(7, model.TezBalance?.Spendable);
        Assert.True(model.SelectedWallet?.Alias == "B" && model.Network == Network.Mainnet && !model.IsLoading);
    }

    [Fact]
    public async Task ASlowLoadForThePreviousNetworkIsDropped()
    {
        var gates = new Gates();
        var model = new WalletViewModel(wallets: [A], chainFactory: n => new GatedChain(n, gates));
        var slow = model.Refresh();                                         // mainnet's load starts…
        await gates.Started(A.Address, Network.Mainnet).Task;
        model.SwitchNetwork(Network.Shadownet);                             // …and Shadownet's replaces it.
        Assert.True(model.TezBalance is null && model.Domains.Count == 0);
        await gates.Started(A.Address, Network.Shadownet).Task;
        gates.Release(A.Address, Network.Mainnet).TrySetResult();           // mainnet answers first, too late.
        await slow;
        Assert.True(model.TezBalance is null && model.Domains.Count == 0, "mainnet's answer must not appear");
        gates.Release(A.Address, Network.Shadownet).TrySetResult();
        await Settle(() => model.TezBalance is not null);
        Assert.True(model.Domains.SequenceEqual(["shadownet.tez"]) && model.TezBalance?.Spendable == 5);
        Assert.True(model.Network.Name == "Shadownet" && !model.IsLoading);
    }
}
