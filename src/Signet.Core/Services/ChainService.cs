using Signet.Models;

namespace Signet.Services;

/// <summary>
/// Everything the UI needs from the chain and indexers, behind one interface so views can be
/// driven by mock data. Swift's <c>x(for: address)</c> becomes <c>XFor(address)</c> here (a member may
/// not share its name with the type it returns, e.g. <c>TezBalance</c>).
/// </summary>
public interface IChainService
{
    Task<TezBalance> TezBalanceFor(Address address);
    Task<decimal> EtherlinkBalanceFor(Address address);
    Task<IReadOnlyList<AssetBalance>> TokenBalancesFor(Address address);
    Task<IReadOnlyList<string>> DomainsFor(Address address);
    Task<IReadOnlyList<NFT>> NftsFor(Address address);
    /// <summary>Tokens and NFTs from one indexer snapshot, with whether it is complete (S26).</summary>
    Task<Portfolio> PortfolioFor(Address address);
    /// <summary>Most recent operations touching the address, newest first.</summary>
    Task<IReadOnlyList<TezosTransaction>> RecentTransactionsFor(Address address, int limit);

    /// <summary>Forward Tezos Domains lookup: <c>name.tez</c> → address, or <c>null</c> if unregistered.</summary>
    Task<Address?> ResolveDomain(string name);
    /// <summary>Indexer-known identity for an address (TzProfiles via TzKT), or <c>null</c>.</summary>
    Task<AccountProfile?> AccountProfileFor(Address address);

    /// <summary>Cost of a transfer, computed without any secret key, with the limits to send it under.</summary>
    Task<TransferEstimate> EstimateTransfer(Wallet wallet, Address destination, decimal amount);
    /// <summary>Signs and injects a transfer with exactly the approved <c>limits</c> (S14).</summary>
    Task<SentOperation> SendTransfer(Wallet wallet, SigningKey signer, Address destination, decimal amount, OperationLimits limits);
    /// <summary>Sends the same signed bytes again after an unknown outcome; the hash cannot change (S15).</summary>
    Task<SentOperation?> PendingOperationFor(Address address);
    Task<SentOperation> Rebroadcast(SentOperation sent);
    /// <summary>Waits for the operation to be included. Returns the block level.</summary>
    Task<int> WaitForConfirmation(string operationHash);

    // Delegation, staking and baking.
    Task<DelegateInfo> DelegateInfoFor(Address address);
    /// <summary>Bakers to offer in the picker, most popular first.</summary>
    Task<IReadOnlyList<BakerCandidate>> Bakers(int limit);
    Task<TransferEstimate> EstimateStaking(StakingOperation operation, Wallet wallet);
    /// <summary>Signs and injects a staking-family operation with exactly the approved <c>limits</c> (S14).</summary>
    Task<SentOperation> PerformStaking(StakingOperation operation, Wallet wallet, SigningKey signer, OperationLimits limits);
    /// <summary>BLS proof of possession for one of our tz4 keys.</summary>
    Task<string> ProofOfPossession(SigningKey signer);

    /// <summary>
    /// Signs raw bytes (hex) with no watermark, e.g. a packed "Tezos Signed Message". Returns the
    /// signer's public key and the prefixed signature (edsig/spsig/p2sig…).
    /// </summary>
    Task<SignedPayload> SignPayload(SigningKey signer, string payloadHex);

    // Governance.
    Task<GovernanceInfo> GovernanceInfoFor(Address address);
    /// <summary>Injects an upvote or ballot. Returns the operation hash.</summary>
    Task<string> PerformGovernance(GovernanceOperation operation, Wallet wallet, SigningKey signer);
}

/// <summary>
/// Everything the indexer knows the account holds, and how much of it was fetched (S26): a
/// partial page or an unavailable indexer is said so, never shown as "no tokens".
/// </summary>
public sealed record Portfolio(IReadOnlyList<AssetBalance> Tokens, IReadOnlyList<NFT> Nfts, Portfolio.PortfolioState State)
{
    public abstract record PortfolioState
    {
        public sealed record Complete : PortfolioState;
        /// <summary>The indexer has more than was fetched; older holdings may be missing.</summary>
        public sealed record Partial(int Fetched) : PortfolioState;
        public sealed record Unavailable(string Reason) : PortfolioState;
    }

    public static readonly Portfolio Empty = new([], [], new PortfolioState.Complete());

    public bool Equals(Portfolio? other) =>
        other is not null && Tokens.SequenceEqual(other.Tokens) && Nfts.SequenceEqual(other.Nfts) && State == other.State;
    public override int GetHashCode() => HashCode.Combine(Tokens.Count, Nfts.Count, State);
}

public sealed record SignedPayload(string PublicKey, string Signature);

/// <summary>Errors the UI distinguishes from generic failures. Equal when kind and payload match, like the Swift enum.</summary>
public abstract class ChainError(string message) : Exception(message), IEquatable<ChainError>
{
    /// <summary>The node has no record of this account: it has never received tez on this network.</summary>
    public sealed class AccountNotOnChain(Address address) : ChainError("Key not found on chain")
    {
        public Address Address { get; } = address;
    }
    /// <summary>The password did not decrypt the wallet's key.</summary>
    public sealed class WrongPassphrase() : ChainError("Incorrect password for this account.");
    /// <summary>The user declined on the Ledger.</summary>
    public sealed class LedgerDeclined() : ChainError("Declined on the Ledger.");
    /// <summary>The Ledger is showing the dashboard or another app.</summary>
    public sealed class LedgerAppNotOpen() : ChainError("Open the Tezos app on your Ledger and try again.");
    /// <summary>The Ledger is locked (PIN screen).</summary>
    public sealed class LedgerLocked() : ChainError("Unlock your Ledger and try again.");
    /// <summary>No Ledger on USB.</summary>
    public sealed class LedgerNotConnected() : ChainError("No Ledger is connected. Plug it in, unlock it and open the Tezos app.");
    /// <summary>The connected Ledger derives a different key at this path (wrong device or seed).</summary>
    public sealed class LedgerWrongDevice(string detail) : ChainError(detail);
    /// <summary>The secret key on disk does not derive the account's address (edited or swapped directory).</summary>
    public sealed class KeyAddressMismatch(string detail) : ChainError(detail);
    /// <summary>The network has Etherlink off or no Etherlink node to ask.</summary>
    public sealed class NoEtherlinkNode() : ChainError("Etherlink is not enabled for this network.");
    /// <summary>A node answered with another chain's id than the one pinned for it (S12).</summary>
    public sealed class WrongChain(string expected, string found) : ChainError($"The node serves chain {found}, not {expected}.");

    public bool Equals(ChainError? other) =>
        other is not null && other.GetType() == GetType() && other.Message == Message &&
        (this as AccountNotOnChain)?.Address == (other as AccountNotOnChain)?.Address;
    public override bool Equals(object? obj) => Equals(obj as ChainError);
    public override int GetHashCode() => HashCode.Combine(GetType(), Message);

    /// <summary>
    /// Recognises the bridge's error text for the cases above, so every signing path maps them
    /// the same way (<c>TaquitoBridge.BridgeError.Typed</c> is the one place that applies it).
    /// </summary>
    public static ChainError? FromBridgeMessage(string message)
    {
        var lower = message.ToLowerInvariant();
        bool Has(params string[] parts) => parts.Any(lower.Contains);
        if (Has("decrypt", "passphrase")) return new WrongPassphrase();
        if (Has("0x6985", "denied by the user", "conditions_of_use_not_satisfied")) return new LedgerDeclined();
        if (Has("0x5515", "locked_device", "locked device")) return new LedgerLocked();
        if (Has("0x6e00", "0x6d00", "0x6511", "0x6e01", "0x6d02", "cla_not_supported", "ins_not_supported")) return new LedgerAppNotOpen();
        if (Has("no ledger is connected", "was unplugged")) return new LedgerNotConnected();
        if (Has("does not hold this key")) return new LedgerWrongDevice(message);
        if (Has("does not derive the account's address")) return new KeyAddressMismatch(message);
        return null;
    }
}
