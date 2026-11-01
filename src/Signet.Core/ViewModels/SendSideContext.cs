using Signet.Models;
using Signet.Services;

namespace Signet.ViewModels;

/// <summary>
/// Everything a side of the Send sheet needs, captured together when the sheet opened (S16, S31):
/// the network shown and linked to is the one the chain service talks to, whatever Settings does
/// later, and neither side ever borrows the other's service. Upstream's <c>SendSheet.SideContext</c>
/// and <c>SendSheet.capture</c>, kept in Core here so the headless tests reach them.
/// </summary>
public sealed record SendSideContext(Network Network, IChainService Chain, decimal? Spendable, bool ImplicitAccountsOnly)
{
    /// <summary>
    /// Both sides as they are now; the Etherlink side exists only when its network and service
    /// are both there, and the pair never changes afterwards.
    /// </summary>
    public static (SendSideContext Layer1, SendSideContext? Layer2) Capture(WalletViewModel model)
    {
        var layer1 = new SendSideContext(model.Network, model.ChainService, model.TezBalance?.Spendable, false);
        if (model.Network.Layer2 is not { } network || model.Layer2Chain is not { } chain) return (layer1, null);
        return (layer1, new SendSideContext(network, chain, model.EtherlinkSpendable, true));
    }
}
