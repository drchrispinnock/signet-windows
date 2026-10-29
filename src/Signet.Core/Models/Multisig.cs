namespace Signet.Models;

// Multisig is not ported yet; only the contract alias exists, because the wallet directory's
// `contracts` file (where address-book KT1 entries live) is read and written by TezosClientStore.

/// <summary>A named multisig contract: an octez-client <c>contracts</c> alias pointing at a KT1 address.</summary>
public sealed record MultisigContract(string Alias, Address Address)
{
    public string Id => Alias;
}
