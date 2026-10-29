namespace Signet.Models;

/// <summary>One line of account history, as the indexer reports it.</summary>
/// <param name="Counterparty">The other party: recipient for outgoing, sender for incoming, new baker for delegations.</param>
/// <param name="CounterpartyAlias">Name the indexer knows for the counterparty (TzProfiles / known accounts), if any.</param>
/// <param name="Amount">Whole tez moved by this operation (0 for contract calls without a transfer).</param>
/// <param name="Fee">Baker fee paid, in tez; only meaningful for outgoing operations.</param>
/// <param name="Entrypoint">Entrypoint for contract calls.</param>
public sealed record TezosTransaction(
    string Id,
    string Hash,
    int Level,
    DateTimeOffset Timestamp,
    TezosTransaction.TransactionKind Kind,
    TezosTransaction.TransactionDirection Direction,
    Address? Counterparty,
    string? CounterpartyAlias,
    decimal Amount,
    decimal Fee,
    string? Entrypoint,
    bool IsApplied)
{
    public enum TransactionKind { Transaction, Delegation, Origination, Other }
    public enum TransactionDirection { Incoming, Outgoing, SelfTransfer }

    /// <summary>
    /// The contract that carried this out on our behalf: an internal operation a multisig (or any
    /// contract we called) emitted, which the indexer lists under our account with the contract
    /// as sender and us as initiator. <c>null</c> for operations we sent ourselves.
    /// </summary>
    public Address? Via { get; init; }
    /// <summary>Delegations only: where the delegator was pointing before.</summary>
    public Address? PreviousDelegate { get; init; }
    /// <summary>Delegations only: the delegator's balance at the time (what moved in or out of our stake).</summary>
    public decimal? DelegatedBalance { get; init; }
    /// <summary>Delegations only: the baker the delegator now points at (null when they undelegated).</summary>
    public Address? NewDelegate { get; init; }
    /// <summary>Our own address, recorded at parse time so roles can be derived without more context.</summary>
    public Address? DelegationTarget { get; init; }

    /// <summary>What a delegation row means from our point of view.</summary>
    public enum DelegationRoleKind { WeDelegated, WeUndelegated, DelegatorJoined, DelegatorLeft, DelegatorMoved }

    public DelegationRoleKind? DelegationRole(Address us)
    {
        if (Kind != TransactionKind.Delegation) return null;
        if (Direction != TransactionDirection.Incoming)
            return Counterparty is null ? DelegationRoleKind.WeUndelegated : DelegationRoleKind.WeDelegated;
        if (NewDelegateIsUs) return DelegationRoleKind.DelegatorJoined;
        return PreviousDelegate == us && NewDelegate is not null ? DelegationRoleKind.DelegatorMoved : DelegationRoleKind.DelegatorLeft;
    }

    private bool NewDelegateIsUs => NewDelegate is not null && NewDelegate == DelegationTarget;

    public bool IsContractCall => Counterparty?.IsContract == true;
}
