using Signet.Models;

namespace Signet.ViewModels;

public static class ActivityTabExtensions
{
    /// <summary>The tab's title, upstream's raw value.</summary>
    public static string RawValue(this WalletViewModel.ActivityTab tab) => tab switch
    {
        WalletViewModel.ActivityTab.Transactions => "Activity",
        WalletViewModel.ActivityTab.Assets => "Assets",
        WalletViewModel.ActivityTab.Nfts => "NFTs",
        WalletViewModel.ActivityTab.Domains => "Domains",
        _ => throw new ArgumentOutOfRangeException(nameof(tab)),
    };
}

/// <summary>
/// The Domains tab: the Tezos Domains names the selected account owns or that point at it.
/// Read when the tab is shown rather than on every refresh, since only mainnet has them.
/// </summary>
public sealed partial class WalletViewModel
{
    /// <summary>The tabs the dashboard offers: Domains only where Tezos Domains exists (Mainnet).</summary>
    public IReadOnlyList<ActivityTab> ActivityTabs =>
        Enum.GetValues<ActivityTab>().Where(t => t != ActivityTab.Domains || Network.TezosDomainsURL is not null).ToList();

    /// <summary>The tab to show: the chosen one, or Activity when the network does not offer it.</summary>
    public ActivityTab ShownActivityTab => ActivityTabs.Contains(ActivityTabSelection) ? ActivityTabSelection : ActivityTab.Transactions;

    /// <summary>Which account on which network a list belongs to, so one never shows for another.</summary>
    public string? DomainListKey => SelectedWallet is { } wallet ? $"{Network.Name}|{wallet.Address.Value}" : null;

    /// <summary>The list for the selected account, if it was read for it.</summary>
    public DomainListState? CurrentDomainList =>
        DomainListKey is { } key && DomainList is { } list && list.Key == key ? list.State : null;

    public async Task LoadDomainList()
    {
        if (SelectedWallet is not { } wallet || DomainListKey is not { } key) return;
        if (CurrentDomainList is DomainListState.Loaded) return;
        DomainList = new DomainListEntry(key, new DomainListState.Loading());
        DomainListState state;
        try
        {
            state = await chain.TezosDomainsFor(wallet.Address) is { } names
                ? new DomainListState.Loaded(names)
                : new DomainListState.Unavailable();
        }
        catch (Exception e)
        {
            state = new DomainListState.Failed(e.Message);
        }
        // The account or network may have changed while Tezos Domains answered.
        if (DomainListKey != key) return;
        DomainList = new DomainListEntry(key, state);
    }

    public async Task ReloadDomainList()
    {
        DomainList = null;
        await LoadDomainList();
    }
}
