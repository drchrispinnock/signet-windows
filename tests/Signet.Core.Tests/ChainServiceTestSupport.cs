using Signet.Models;
using Signet.Services;

namespace Signet.Tests;

/// <summary>Default implementations so small test doubles only override what they care about.</summary>
public abstract class TestChainService : IChainService
{
    public abstract Task<TezBalance> TezBalanceFor(Address address);
    public virtual Task<decimal> EtherlinkBalanceFor(Address address) => Task.FromResult(0m);
    public virtual Task<IReadOnlyList<AssetBalance>> TokenBalancesFor(Address address) => Task.FromResult<IReadOnlyList<AssetBalance>>([]);
    public virtual Task<IReadOnlyList<string>> DomainsFor(Address address) => Task.FromResult<IReadOnlyList<string>>([]);
    public virtual Task<IReadOnlyList<NFT>> NftsFor(Address address) => Task.FromResult<IReadOnlyList<NFT>>([]);
    public virtual Task<IReadOnlyList<TezosTransaction>> RecentTransactionsFor(Address address, int limit) => Task.FromResult<IReadOnlyList<TezosTransaction>>([]);
    public virtual Task<Address?> ResolveDomain(string name) => Task.FromResult<Address?>(null);
    public virtual Task<AccountProfile?> AccountProfileFor(Address address) => Task.FromResult<AccountProfile?>(null);
    public virtual Task<TransferEstimate> EstimateTransfer(Wallet wallet, Address destination, decimal amount) =>
        Task.FromResult(new TransferEstimate(0.001m, 0, amount + 0.001m, 0, 0, new OperationLimits("1000", 0, 0)));
    public virtual Task<SentOperation> SendTransfer(Wallet wallet, SigningKey signer, Address destination, decimal amount, OperationLimits limits) =>
        Task.FromResult(new SentOperation("ooTest"));
    public virtual Task<SentOperation?> PendingOperationFor(Address address) => Task.FromResult<SentOperation?>(null);
    public virtual Task<SentOperation> Rebroadcast(SentOperation sent) => Task.FromResult(sent);
    public virtual async Task<Portfolio> PortfolioFor(Address address) =>
        new(await TokenBalancesFor(address), await NftsFor(address), new Portfolio.PortfolioState.Complete());
    public virtual Task<int> WaitForConfirmation(string operationHash) => Task.FromResult(1);
    public virtual Task<DelegateInfo> DelegateInfoFor(Address address) => Task.FromResult(new DelegateInfo(null, null, null));
    public virtual Task<IReadOnlyList<BakerCandidate>> Bakers(int limit) => Task.FromResult<IReadOnlyList<BakerCandidate>>([]);
    public virtual Task<TransferEstimate> EstimateStaking(StakingOperation operation, Wallet wallet) =>
        Task.FromResult(new TransferEstimate(0.001m, 0, 0.001m, 0, 0, new OperationLimits("1000", 0, 0)));
    public virtual Task<SentOperation> PerformStaking(StakingOperation operation, Wallet wallet, SigningKey signer, OperationLimits limits) =>
        Task.FromResult(new SentOperation("ooTest"));
    public virtual Task<string> ProofOfPossession(SigningKey signer) => Task.FromResult("BLsigTest");
    public virtual Task<TransferEstimate> EstimateEtherlinkDeposit(Wallet wallet, decimal amount) =>
        Task.FromResult(new TransferEstimate(0.001m, 0, amount + 0.001m, 0, 0, new OperationLimits("1000", 0, 0)));
    public virtual Task<SentOperation> SendEtherlinkDeposit(Wallet wallet, SigningKey signer, decimal amount, OperationLimits limits) =>
        Task.FromResult(new SentOperation("ooTest"));
    public virtual Task<EtherlinkAliasStatus> EtherlinkAliasStatusFor(Address address) =>
        Task.FromResult<EtherlinkAliasStatus>(new EtherlinkAliasStatus.Unlinked(EtherlinkAlias.AddressFor(address)));
    public virtual Task<TransferEstimate> EstimateEtherlinkLink(Wallet wallet) =>
        Task.FromResult(new TransferEstimate(0.001m, 0, 0.001m, 0, 0, new OperationLimits("1000", 0, 0)));
    public virtual Task<SentOperation> SendEtherlinkLink(Wallet wallet, SigningKey signer, OperationLimits limits) =>
        Task.FromResult(new SentOperation("ooTest"));
    public virtual Task<SignedPayload> SignPayload(SigningKey signer, string payloadHex) => Task.FromResult(new SignedPayload("edpkTest", "edsigTest"));
    public virtual Task<GovernanceInfo> GovernanceInfoFor(Address address) => Task.FromResult(new GovernanceInfo());
    public virtual Task<string> PerformGovernance(GovernanceOperation operation, Wallet wallet, SigningKey signer) => Task.FromResult("ooTest");
    public virtual Task<IReadOnlyList<Address>> EtherlinkVotingRightsFor(Address address, Address contract) => Task.FromResult<IReadOnlyList<Address>>([]);
    public virtual Task<TransferEstimate> EstimateShieldDeposit(Wallet wallet, ShieldDeposit deposit) =>
        Task.FromResult(new TransferEstimate(0.001m, 0, deposit.Amount + 0.001m, 0, 0, new OperationLimits("1000", 0, 0)));
    public virtual Task<SentOperation> SendShieldDeposit(Wallet wallet, SigningKey signer, ShieldDeposit deposit, OperationLimits limits) =>
        Task.FromResult(new SentOperation("ooShieldDeposit"));
    public virtual Task<TransferEstimate> EstimateOutboxExecution(Wallet wallet, OutboxExecution execution) =>
        Task.FromResult(new TransferEstimate(0.001m, 0, 0.001m, 0, 0, new OperationLimits("1000", 0, 0)));
    public virtual Task<SentOperation> SendOutboxExecution(Wallet wallet, SigningKey signer, OutboxExecution execution, OperationLimits limits) =>
        Task.FromResult(new SentOperation("ooOutbox"));
    public virtual Task<TokenTransferEstimate> EstimateTokenTransfer(TokenTransferRequest request, Wallet wallet) =>
        Task.FromResult(new TokenTransferEstimate(0.001m, 0, new BatchLimits([new BatchLimits.Operation("1000", 0, 0)])));
    public virtual Task<SentOperation> SendTokenTransfer(TokenTransferRequest request, Wallet wallet, SigningKey signer, BatchLimits limits) =>
        Task.FromResult(new SentOperation("ooTest"));
    public virtual Task<EtherlinkGovernance.State> EtherlinkGovernanceState(Address contract, Address address) => Task.FromResult(new EtherlinkGovernance.State(contract));
    public virtual Task<TransferEstimate> EstimateEtherlinkGovernance(EtherlinkGovernance.Operation operation, Address contract, Wallet wallet) =>
        Task.FromResult(new TransferEstimate(0.001m, 0, 0.001m, 0, 0, new OperationLimits("1000", 0, 0)));
    public virtual Task<SentOperation> SendEtherlinkGovernance(EtherlinkGovernance.Operation operation, Address contract, Wallet wallet, SigningKey signer, OperationLimits limits) =>
        Task.FromResult(new SentOperation("ooTest"));
}
