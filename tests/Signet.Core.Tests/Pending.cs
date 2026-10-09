namespace Signet.Tests;

/// <summary>
/// Skip reasons for upstream tests whose code is not ported yet. Each skipped test keeps its
/// upstream name so <c>dotnet test</c> lists what is left; port it when its layer lands.
/// </summary>
internal static class Pending
{
    public const string ViewModel = "Pending port: WalletViewModel";
    public const string Bridge = "Pending port: TaquitoBridge";
    public const string ChainService = "Pending port: TaquitoChainService / MockChainService";
    public const string Store = "Pending port: TezosClientStore";
    public const string TzKT = "Pending port: TzKTService";
    public const string Ledger = "Pending port: LedgerHID / LedgerService";
}

/// <summary>Trait for tests that reach live nodes and indexers: <c>dotnet test --filter Category!=Network</c> skips them.</summary>
internal static class Category
{
    public const string Name = "Category";
    public const string Network = "Network";
}
