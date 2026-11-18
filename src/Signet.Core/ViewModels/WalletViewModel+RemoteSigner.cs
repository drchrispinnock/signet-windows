using Signet.Models;
using Signet.Services;

namespace Signet.ViewModels;

/// <summary>
/// Connect Remote Signer: an account whose key octez-signer holds, reached over HTTP, HTTPS or
/// TCP. Like a Ledger account, Signet keeps only the address, the public key and where the key
/// is; the alias is written in octez-client's form, so octez-client can use it too.
/// </summary>
public sealed partial class WalletViewModel
{
    /// <summary>What the signer says about one of its keys, checked here.</summary>
    /// <param name="AcceptsUnauthenticated"><c>false</c> when the signer was started with <c>--require-authentication</c>.</param>
    public sealed record RemoteSignerCheck(RemoteSignerLocator Signer, string PublicKey, bool AcceptsUnauthenticated);

    /// <summary>The keys a signer lists, when it lists them (octez-signer 22 and later).</summary>
    public Task<IReadOnlyList<Address>> RemoteSignerKeys(RemoteSignerLocator signer) => RemoteSigners.KnownKeys(signer);

    /// <summary>
    /// Asks the signer for the key's public key and checks it is the address's own: the address is
    /// derived from the key here, never taken from the signer.
    /// </summary>
    public async Task<RemoteSignerCheck> CheckRemoteSigner(RemoteSignerLocator signer)
    {
        if (!signer.Address.IsImplicitAccount) throw new WalletError.InvalidAddress();
        var publicKey = await RemoteSigners.PublicKey(signer);
        var derived = await AddressForScannedPublicKey(publicKey);
        if (derived != signer.Address) throw new RemoteSignerError.KeyMismatch(signer.Address, derived);
        var authorized = await RemoteSigners.AuthorizedKeys(signer);
        return new RemoteSignerCheck(signer, publicKey, authorized is null);
    }

    /// <summary>
    /// Connect Remote Signer: checks the signer again (<c>CheckRemoteSigner</c>) and adds the account
    /// with the signer's locator in <c>secret_keys</c>.
    /// </summary>
    public async Task ConnectRemoteSigner(string alias, RemoteSignerLocator signer)
    {
        var name = alias.Trim();
        if (name.Length == 0) throw new WalletError.EmptyAlias();
        if (Wallets.Any(w => w.Alias == name)) throw new WalletError.AliasExists(name);
        if (Wallets.FirstOrDefault(w => w.Address == signer.Address) is { } existing) throw new WalletError.AddressExists(existing.Alias);
        var check = await CheckRemoteSigner(signer);
        // The list may have changed while the signer was asked.
        if (Wallets.Any(w => w.Alias == name)) throw new WalletError.AliasExists(name);
        if (Wallets.FirstOrDefault(w => w.Address == signer.Address) is { } again) throw new WalletError.AddressExists(again.Alias);
        var wallet = new Wallet(name, signer.Address, publicKey: check.PublicKey, keyKind: KeyKind.Remote, remoteSigner: signer);
        walletStore.Add(wallet, signer.Locator);
        try { Wallets = walletStore.Load(); }
        catch (Exception) { Wallets = [.. Wallets, wallet]; }
        Select(Wallets.FirstOrDefault(w => w.Alias == name) ?? wallet);
        await BackUp();
    }
}
