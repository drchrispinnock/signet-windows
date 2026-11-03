using CommunityToolkit.Mvvm.ComponentModel;
using Signet.Models;
using Signet.Services;

namespace Signet.ViewModels;

/// <summary>
/// FA2 token transfers from the selected account (NFT Transfer and Burn, 0.9.13): estimated
/// in a context and sent only in that same context with the approved limits (S14, S31).
/// </summary>
public sealed partial class WalletViewModel
{
    /// <summary>The NFTs a Transfer or Burn was started on from the grid's context menu; the sheet shows while set.</summary>
    [ObservableProperty] private NFTOperation? nftOperation;
    /// <summary>The tokens whose artwork is being downloaded, while the Download sheet is up.</summary>
    [ObservableProperty] private NFTDownload? nftDownload;

    /// <summary>
    /// A token transfer estimate together with everything it was made for: the exact request,
    /// the layer, and the account, network and directory.
    /// </summary>
    public sealed record TokenTransferApproval(TokenTransferEstimate Estimate, TokenTransferRequest Request, int Layer, BridgeContextInfo Context);

    /// <summary>
    /// Whether the selected account can move its tokens on <c>layer</c>: a key Signet can sign for,
    /// and on Etherlink a tz1, tz2 or tz3 account with Layer 2 on.
    /// </summary>
    public bool CanTransferTokens(int layer)
    {
        if (SelectedWallet is not { } wallet || !wallet.KeyKind.CanSign()) return false;
        if (layer == 2) return Network.HasL2 && wallet.Address.IsUsableOnEtherlink && Layer2Chain is not null;
        return layer == 1;
    }

    /// <summary>The network tokens on <c>layer</c> live on, for explorer links.</summary>
    public Network TokenNetwork(int layer) => layer == 2 ? Network.Layer2 ?? Network : Network;

    private IChainService? TokenChain(int layer) => layer == 2 ? Layer2Chain : ChainService;

    /// <summary>Saves artwork into the <c>nfts</c> folder of the wallet directory in use.</summary>
    public NFTDownloader NftDownloader => new(NFTDownloader.DirectoryUnder(WalletDirectory ?? TezosClientStore.DefaultDirectory));

    public async Task<TokenTransferApproval> EstimateTokenTransfer(TokenTransferRequest request, int layer)
    {
        if (SelectedWallet is not { } wallet || BridgeContext is not { } context) throw new WalletError.EmptyAlias();
        if (!CanTransferTokens(layer) || TokenChain(layer) is not { } chain) throw new SendViewModel.SendError.NoSecretKey();
        if (!request.IsValid || !(layer == 1 || request.To.IsUsableOnEtherlink)) throw new ChainError.InvalidTokenTransfer();
        var estimate = await chain.EstimateTokenTransfer(request, wallet);
        // The account, network, node or directory may have changed while the node was asked.
        if (BridgeContext != context) throw new WalletError.ContextChanged();
        return new TokenTransferApproval(estimate, request, layer, context);
    }

    /// <summary>
    /// Sends the approved transfer with its approved limits and waits for its block. The
    /// estimate must have been made for exactly this request, on this layer, in the context the
    /// transfer runs in now (S31).
    /// </summary>
    public async Task<(string Hash, int Level)> TransferTokens(TokenTransferRequest request, int layer, TokenTransferApproval? approved, string? passphrase)
    {
        if (SelectedWallet is not { } wallet) throw new WalletError.EmptyAlias();
        if (approved is null) throw new WalletError.NotEstimated();
        if (approved.Request != request || approved.Layer != layer || approved.Context != BridgeContext) throw new WalletError.ContextChanged();
        if (!CanTransferTokens(layer) || TokenChain(layer) is not { } chain) throw new SendViewModel.SendError.NoSecretKey();
        if (!request.IsValid || !(layer == 1 || request.To.IsUsableOnEtherlink)) throw new ChainError.InvalidTokenTransfer();
        var signer = SigningKeyFor(wallet, passphrase) ?? throw new SendViewModel.SendError.NoSecretKey();
        var sent = await chain.SendTokenTransfer(request, wallet, signer, approved.Estimate.Limits);
        int level;
        try { level = await chain.WaitForConfirmation(sent.Hash); }
        catch (Exception) when (!sent.Injected) { throw new WalletError.OutcomeUnknown(sent.Hash); }
        await RefreshAfterOperation();
        return (sent.Hash, level);
    }
}
