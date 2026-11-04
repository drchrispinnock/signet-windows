using System.Net;
using Signet.Models;
using Signet.Services;
using Signet.ViewModels;

namespace Signet.Tests;

/// <summary>
/// Upstream's audit repairs. Two of its tests were ported earlier beside the code they cover:
/// <c>LedgerTests.LedgerDeviceLeaseSerializesIndependentClients</c> and
/// <c>SendTests.JournalSurvivesReconstructionAndRefusesAReplacement</c>.
/// </summary>
public class AuditRepairTests
{
    private static string Directory()
    {
        var path = Path.Combine(Path.GetTempPath(), "signet-repair-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public async Task WalletLockSerializesThreads()
    {
        var root = Directory();
        try
        {
            using ManualResetEventSlim entered = new(), release = new(), second = new();
            var first = Task.Run(() => TezosClientStore.WithWalletLock(root, () =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
                return 0;
            }), TestContext.Current.CancellationToken);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
            var other = Task.Run(() => TezosClientStore.WithWalletLock(root, () => { second.Set(); return 0; }), TestContext.Current.CancellationToken);
            Assert.False(second.Wait(TimeSpan.FromMilliseconds(150), TestContext.Current.CancellationToken));
            release.Set();
            Assert.True(second.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
            await Task.WhenAll(first, other).WaitAsync(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);
        }
        finally { System.IO.Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task FailedGrantRefreshCannotUseTheCache()
    {
        var manager = new DAppConnectionManager(new InMemoryBridgeStorage(), () => [], (_, _) => null,
            permissionLoader: () => throw new TimeoutException("synthetic"));
        manager.PermissionsOverride = [DAppGrantTests.Grant()];
        await manager.ReloadPermissions();
        Assert.Single(manager.Permissions);
        manager.PermissionsOverride = null;
        await Assert.ThrowsAnyAsync<Exception>(() => manager.GrantFor(DAppGrantTests.Sign));
        await manager.ReloadPermissions();
        Assert.Empty(manager.Permissions);
    }

    [Fact]
    public void PurchasePrivilegesIncludeThePortAndEndpointsIncludeQueries()
    {
        var provider = BuyProvider.MtPelerin;
        Assert.False(BuyWebViewPolicy.IsTrusted(new Uri("https://widget.mtpelerin.com:8443/"), provider));
        Assert.False(BuyWebViewPolicy.MayCaptureMedia("https", "widget.mtpelerin.com", true, "https", "widget.mtpelerin.com", provider, originPort: 443, framePort: 8443));
        Assert.False(DAppRequest.SameEndpoint(new Uri("https://example.org/rpc?chain=a"), new Uri("https://example.org/rpc?chain=b")));
    }

    [Fact]
    public void ADefaultBridgeHasNoDiskStorage() => Assert.IsType<InMemoryBridgeStorage>(new TaquitoBridge().Storage);

    [Fact]
    public async Task AnInvalidatedDirectorySessionCannotReadANewGrant()
    {
        var manager = new DAppConnectionManager(new InMemoryBridgeStorage(), () => [], (_, _) => null,
            permissionLoader: () => Task.FromResult<IReadOnlyList<DAppPermission>>([DAppGrantTests.Grant()]));
        manager.Invalidate();
        await Assert.ThrowsAnyAsync<Exception>(() => manager.GrantFor(DAppGrantTests.Sign));
    }

    [Fact]
    public void HTTPParsingBudgetPrecedesSDKParsingAndDecryption()
    {
        Assert.False(JSONBudget.Accepts(System.Text.Encoding.UTF8.GetBytes(new string('[', 129) + "0" + new string(']', 129))));
        Assert.False(JSONBudget.Accepts(System.Text.Encoding.UTF8.GetBytes("[\"" + new string('a', (1 << 20) + 1) + "\"]")));
        Assert.False(JSONBudget.Accepts("[0,0,0,0,0]"u8, nodeLimit: 3));
        Assert.True(JSONBudget.Accepts("{\"quoted\":\"[[[\\\"\"}"u8, depthLimit: 2));
    }

    [Fact]
    public async Task ArtworkSlotsCoverActualDownloadWork()
    {
        int active = 0, peak = 0;
        var gate = new Lock();
        var loader = ImageLoader.WithDownload(async _ =>
        {
            lock (gate) { active++; peak = Math.Max(peak, active); }
            await Task.Delay(30);
            lock (gate) active--;
            return null;
        });
        await Task.WhenAll(Enumerable.Range(0, 24).Select(i => loader.ImageFor([new Uri($"https://artwork.example.org/{i}")])));
        Assert.True(peak <= ImageLoader.MaxConcurrent);
        Assert.True(peak > 1);
        Assert.Equal(0, active);
    }

    [Fact(Skip = "Windows has no WalletBackup journal recovery yet (upstream's newer WalletBackup)")]
    public void BackupRecoversBeforeItsFirstSnapshot() { }

    [Fact(Skip = "Windows pins public connections in SocketsHttpHandler.ConnectCallback, not a curl configuration; PublicHTTPClient's checks are tested in DAppTests")]
    public void PublicConnectionsPinTheCheckedAddressAndRefusePrivateAnswers() { }
}

/// <summary>
/// A server for <c>offline.invalid</c>: <c>wrong-chain</c> answers another chain id; every other path stays
/// in flight until the bridge cancels it (upstream's <c>AuditHTTPProtocol</c>).
/// </summary>
internal sealed class AuditHTTPHandler : HttpMessageHandler
{
    private static readonly HashSet<string> Stops = [];

    public static bool WasStopped(string url) { lock (Stops) return Stops.Any(s => s.StartsWith(url, StringComparison.Ordinal)); }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!;
        if (url.AbsolutePath.Contains("wrong-chain")) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("\"NetWrongChain\"") };
        try { await Task.Delay(Timeout.Infinite, cancellationToken); }
        catch (OperationCanceledException) { lock (Stops) Stops.Add(url.AbsoluteUri); throw; }
        throw new InvalidOperationException();
    }
}

/// <summary>Transport seams exercise timeout cleanup and wrong-chain refusal without using the network.</summary>
public class BridgeBoundaryRepairTests
{
    private static TaquitoBridge Bridge() => new(new HttpClient(new AuditHTTPHandler()) { Timeout = Timeout.InfiniteTimeSpan });

    [Fact]
    public async Task TimeoutCancelsHTTPAndRebuildsACleanContext()
    {
        var bridge = Bridge();
        var url = $"https://offline.invalid/timeout-{Guid.NewGuid()}";
        await Assert.ThrowsAsync<TaquitoBridge.BridgeError.TimedOut>(() => bridge.CallWithin(TimeSpan.FromMilliseconds(50), "getHead", url));
        for (var i = 0; i < 100 && !AuditHTTPHandler.WasStopped(url); i++) await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.True(AuditHTTPHandler.WasStopped(url));
        Assert.NotNull((await bridge.Call("version")).StringValue);
    }

    [Fact]
    public async Task MultisigChecksTheSelectedChainBeforeReadingContractData()
    {
        var network = Network.Mainnet.UsingNode(new Uri("https://offline.invalid/wrong-chain"));
        var service = new BridgeMultisigService(Bridge(), network);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => service.Info(network.RpcURL, MockMultisigService.SampleAddress));
        Assert.Contains("serves chain NetWrongChain", error.Message);
    }
}

/// <summary>A multisig service whose <c>Prepare</c> changes the model's network while it runs.</summary>
internal sealed class ContextSwitchingMultisig(Action change) : IMultisigService
{
    private static Exception Unsupported => new NotSupportedException();
    public Task<(MultisigInfo Info, string ChainID, string Bytes)> Prepare(Uri rpcURL, Address contract, MultisigAction action)
    {
        change();
        return Task.FromResult((new MultisigInfo(contract, true, "fixture", 0, 1, ["fixture"], 0m), "NetFixture", "0500"));
    }
    public Task<MultisigInfo> Info(Uri rpcURL, Address address) => throw Unsupported;
    public Task<string?> RevealedPublicKey(Uri rpcURL, Address address) => throw Unsupported;
    public Task<Address> AddressForPublicKey(string publicKey) => throw Unsupported;
    public Task<(string Bytes, string Signature)> Sign(SigningKey signer, MultisigProposal proposal) => throw Unsupported;
    public Task<TransferEstimate> EstimateOriginate(Uri rpcURL, Wallet wallet, int threshold, IReadOnlyList<string> keys, decimal amount) => throw Unsupported;
    public Task<(string Hash, Address Address)> Originate(Uri rpcURL, SigningKey signer, int threshold, IReadOnlyList<string> keys, decimal amount, OperationLimits limits) => throw Unsupported;
    public Task<TransferEstimate> EstimateSubmit(Uri rpcURL, Wallet wallet, MultisigProposal proposal, IReadOnlyList<string> signatures) => throw Unsupported;
    public Task<string> Submit(Uri rpcURL, SigningKey signer, MultisigProposal proposal, IReadOnlyList<string> signatures, OperationLimits limits) => throw Unsupported;
}

public class MultisigContextRepairTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProposalsRefuseSameNameEndpointAndAwayBackChanges(bool awayAndBack)
    {
        WalletViewModel? model = null;
        var service = new ContextSwitchingMultisig(() =>
        {
            if (model is null) return;
            if (awayAndBack) { model.Network = Network.Shadownet; model.Network = Network.Mainnet; }
            else model.Network = model.Network.UsingNode(new Uri("https://alternate.example.org"));
        });
        model = new WalletViewModel(wallets: [], chain: new MockChainService(), multisig: service, walletStore: new InMemoryWalletStore());
        var contract = new MultisigContract("fixture", MockMultisigService.SampleAddress);
        await Assert.ThrowsAnyAsync<WalletViewModel.WalletError>(() => model.ProposeMultisig(new MultisigAction.SetDelegate(null), contract));
        Assert.Empty(model.MultisigProposals);
    }
}
