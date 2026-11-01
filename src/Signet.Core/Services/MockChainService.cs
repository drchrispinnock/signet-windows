using Signet.Models;

namespace Signet.Services;

/// <summary>Static data matching the sketch in spec/EXAMPLE.png. Used for previews and tests.</summary>
public sealed class MockChainService : IChainService
{
    public static readonly Address CaptainStake = new("tz1dCaMnnMJk76UodjewCK67ABiXWjcKj73N");

    public Task<TezBalance> TezBalanceFor(Address address) =>
        Task.FromResult(new TezBalance(Spendable: 1361.43m, Staked: 3000));

    public Task<decimal> EtherlinkBalanceFor(Address address) => Task.FromResult(5432.1m);

    public Task<IReadOnlyList<AssetBalance>> TokenBalancesFor(Address address)
    {
        if (address != CaptainStake) return Task.FromResult<IReadOnlyList<AssetBalance>>([]);
        static AssetBalance Token(string contract, string name, string symbol, decimal amount) =>
            new($"{contract}:0", new AssetBalance.AssetKind.Token(contract, "0"), name, symbol, amount) { Standard = "fa2" };
        return Task.FromResult<IReadOnlyList<AssetBalance>>(
        [
            Token("KT1FfjhvJZppBFQuUzNAdFPR1Z2jpD4XiXrF", "Aubergine", "GINE", 30_000),
            Token("KT1MZg99PxMDEENwB4Fi64xkqAVh5d1rv8Z9", "Tezos Pepe", "PEPE", 5_000),
            Token("KT1VaEsVNiBoA56eToEK6n6BcPgh1tdx9eXi", "Temple Key", "TKEY", 1.047540545999871429m),
        ]);
    }

    public Task<IReadOnlyList<string>> DomainsFor(Address address) =>
        Task.FromResult<IReadOnlyList<string>>(address == CaptainStake ? ["captstake.tez"] : ["mytez.tez"]);

    public Task<IReadOnlyList<NFT>> NftsFor(Address address) =>
        Task.FromResult<IReadOnlyList<NFT>>(Enumerable.Range(1, 6)
            .Select(i => new NFT($"mock:{i}", "KT1mock", $"{i}", $"NFT {i}", 1, thumbnailURL: null)).ToList());

    /// <summary>The sample history is dated from when this mock was made, so reading it twice gives the same operations, as an indexer would.</summary>
    private readonly DateTimeOffset createdAt = DateTimeOffset.Now;

    public Task<IReadOnlyList<TezosTransaction>> RecentTransactionsFor(Address address, int limit)
    {
        var now = createdAt;
        return Task.FromResult<IReadOnlyList<TezosTransaction>>(
        [
            new("1", "ooAAA111", 9_000_010, now.AddSeconds(-3_600), TezosTransaction.TransactionKind.Transaction, TezosTransaction.TransactionDirection.Incoming,
                CaptainStake, "Captain Stake", 12.5m, 0, null, true),
            new("2", "ooBBB222", 9_000_000, now.AddSeconds(-86_400), TezosTransaction.TransactionKind.Transaction, TezosTransaction.TransactionDirection.Outgoing,
                new Address("KT1RJ6PbjHpwc3M5rw5s2Nbmefwbuwbdxton"), "objkt.com Marketplace", 0, 0.000521m, "collect", true),
            new("3", "ooCCC333", 8_999_000, now.AddSeconds(-3 * 86_400), TezosTransaction.TransactionKind.Delegation, TezosTransaction.TransactionDirection.Outgoing,
                CaptainStake, "Captain Stake", 0, 0.0003m, null, true),
            new("4", "ooDDD444", 8_990_000, now.AddSeconds(-9 * 86_400), TezosTransaction.TransactionKind.Transaction, TezosTransaction.TransactionDirection.Outgoing,
                new Address("tz2BFTyPeYRzxd5aiBchbXN3WCZhx7BqbMBq"), null, 3, 0.0004m, null, false),
        ]);
    }

    public Task<Address?> ResolveDomain(string name) =>
        Task.FromResult<Address?>(name.ToLowerInvariant() == "captstake.tez" ? CaptainStake : null);

    public Task<AccountProfile?> AccountProfileFor(Address address) =>
        Task.FromResult(address == CaptainStake ? new AccountProfile("Captain Stake", "captstake", "Truth, Justice, Staking.") : null);

    public Task<TransferEstimate> EstimateTransfer(Wallet wallet, Address destination, decimal amount)
    {
        var burn = destination == CaptainStake ? 0 : 0.06425m;
        return Task.FromResult(new TransferEstimate(0.000521m, burn, amount + 0.000521m + burn, 169, burn > 0 ? 257 : 0,
                                                    new OperationLimits("521", 169, burn > 0 ? 257 : 0)));
    }

    /// <summary>Encrypted mock keys open with the password "correct horse" only.</summary>
    private static void CheckPassphrase(Wallet wallet, SigningKey signer)
    {
        if (wallet.KeyKind == KeyKind.Encrypted && signer is SigningKey.Secret { Passphrase: var p } && p != "correct horse")
            throw new ChainError.WrongPassphrase();
    }

    public Task<SentOperation> SendTransfer(Wallet wallet, SigningKey signer, Address destination, decimal amount, OperationLimits limits)
    {
        CheckPassphrase(wallet, signer);
        return Task.FromResult(new SentOperation("ooMockOperationHash1111111111111111111111111111111111", SignedBytes: "ab"));
    }

    public Task<SentOperation?> PendingOperationFor(Address address) => Task.FromResult<SentOperation?>(null);

    public Task<SentOperation> Rebroadcast(SentOperation sent) => Task.FromResult(new SentOperation(sent.Hash, SignedBytes: sent.SignedBytes));

    public async Task<Portfolio> PortfolioFor(Address address) =>
        new(await TokenBalancesFor(address), await NftsFor(address), new Portfolio.PortfolioState.Complete());

    public Task<int> WaitForConfirmation(string operationHash) => Task.FromResult(9_000_000);

    public Task<DelegateInfo> DelegateInfoFor(Address address)
    {
        if (address == CaptainStake)
        {
            var baker = new DelegateInfo.BakerRecord(false, 1379, new Address("tz4E128TU3t1TdLCJEY4oHg8gZzZNoNBknwh"), [], null, [])
            {
                StakingParameters = new StakingParameters(9_000_000, 10_000_000),
            };
            return Task.FromResult(new DelegateInfo(address, baker, true));
        }
        return Task.FromResult(new DelegateInfo(CaptainStake, null, true));
    }

    public Task<IReadOnlyList<BakerCandidate>> Bakers(int limit) => Task.FromResult<IReadOnlyList<BakerCandidate>>(
    [
        new(CaptainStake, "Captain Stake", 136_876, 38, 15, true),
        new(new Address("tz3cqThj23Feu55KDynm7Vg81mCMpWDgzQZq"), "Tezos Foundation Baker 1", 24_666_552, 18, 4, true),
    ]);

    public Task<TransferEstimate> EstimateStaking(StakingOperation operation, Wallet wallet) =>
        Task.FromResult(new TransferEstimate(0.000421m, 0, 0.000421m, 200, 0, new OperationLimits("421", 200, 0)));

    public Task<SentOperation> PerformStaking(StakingOperation operation, Wallet wallet, SigningKey signer, OperationLimits limits)
    {
        CheckPassphrase(wallet, signer);
        return Task.FromResult(new SentOperation($"ooMockStaking{operation.BridgeKind}"));
    }

    public Task<string> ProofOfPossession(SigningKey signer) => Task.FromResult("BLsigMockProof");

    public Task<TransferEstimate> EstimateEtherlinkDeposit(Wallet wallet, decimal amount) =>
        Task.FromResult(new TransferEstimate(0.001234m, 0, amount + 0.001234m, 3000, 0, new OperationLimits("1234", 3000, 0)));

    public Task<SentOperation> SendEtherlinkDeposit(Wallet wallet, SigningKey signer, decimal amount, OperationLimits limits)
    {
        CheckPassphrase(wallet, signer);
        return Task.FromResult(new SentOperation("ooMockDeposit"));
    }

    /// <summary>Accounts whose EVM alias the mock has linked; tests reset it.</summary>
    public static readonly HashSet<Address> LinkedAliases = [];

    public Task<EtherlinkAliasStatus> EtherlinkAliasStatusFor(Address address)
    {
        var alias = EtherlinkAlias.AddressFor(address);
        lock (LinkedAliases)
            return Task.FromResult<EtherlinkAliasStatus>(LinkedAliases.Contains(address)
                ? new EtherlinkAliasStatus.Linked(alias) : new EtherlinkAliasStatus.Unlinked(alias));
    }

    public Task<TransferEstimate> EstimateEtherlinkLink(Wallet wallet) =>
        Task.FromResult(new TransferEstimate(0.001291m, 0, 0.001291m, 2000, 0, new OperationLimits("1291", 2000, 0)));

    public Task<SentOperation> SendEtherlinkLink(Wallet wallet, SigningKey signer, OperationLimits limits)
    {
        CheckPassphrase(wallet, signer);
        lock (LinkedAliases) LinkedAliases.Add(wallet.Address);
        return Task.FromResult(new SentOperation("ooMockLink"));
    }

    public Task<SignedPayload> SignPayload(SigningKey signer, string payloadHex)
    {
        if (signer is SigningKey.Secret { Passphrase: "wrong" }) throw new ChainError.WrongPassphrase();
        return Task.FromResult(new SignedPayload("edpkMock", $"edsigMock{payloadHex[^Math.Min(8, payloadHex.Length)..]}"));
    }

    public Task<GovernanceInfo> GovernanceInfoFor(Address address) => Task.FromResult(new GovernanceInfo
    {
        Kind = VotingPeriodKind.Proposal,
        Index = 184,
        Position = 16754,
        Remaining = 184845,
        Proposals = [new GovernanceInfo.Proposal("PtSeouLouXkxhg39oWzjxDWaCydNfR3RxCUrNe4Q9Ro8BTehcbh", 123_456_789_000)],
        VotingPower = address == CaptainStake ? 1_234_567_000 : null,
        TotalVotingPower = 600_000_000_000_000,
        QuorumPerTenThousand = 5500,
    });

    public Task<string> PerformGovernance(GovernanceOperation operation, Wallet wallet, SigningKey signer)
    {
        CheckPassphrase(wallet, signer);
        return Task.FromResult($"ooMockGovernance{operation.BridgeKind}");
    }
}
