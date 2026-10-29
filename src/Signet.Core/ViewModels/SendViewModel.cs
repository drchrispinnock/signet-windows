using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Signet.Models;
using Signet.Services;

namespace Signet.ViewModels;

/// <summary>
/// Drives the Send sheet: pick a recipient, enter an amount, confirm, send, watch for inclusion.
/// Touched on the UI thread only.
/// </summary>
public sealed partial class SendViewModel : ObservableObject
{
    public abstract record Step
    {
        public sealed record Compose : Step;
        public sealed record Confirm : Step;
        public sealed record Sending : Step;
        public sealed record Sent(string Hash) : Step;
        /// <summary>The node's answer to the injection was lost: the operation may be on its way (S15).</summary>
        public sealed record Unknown(string Hash) : Step;
        public sealed record Confirmed(string Hash, int Level) : Step;
    }

    public abstract class SendError(string message) : Exception(message), IEquatable<SendError>
    {
        public sealed class NoRecipient() : SendError("Enter a valid address or pick one from your list.");
        public sealed class InvalidAmount() : SendError("Enter an amount in tez.");
        public sealed class InsufficientFunds(decimal available) : SendError($"Not enough spendable tez. Available: {AssetBalance.Format(available, "tz")}.");
        public sealed class NoSecretKey() : SendError("Signet has no usable secret key for this account.");
        public sealed class SendingToSelf() : SendError("That is this account's own address.");
        public sealed class PassphraseRequired() : SendError("Enter the password for this account's key.");
        public sealed class NotEstimated() : SendError("The fee has not been estimated yet; go back and try again.");
        public sealed class NotSeenYet(string hash)
            : SendError($"Operation {hash[..Math.Min(12, hash.Length)]}… has not appeared in a block yet. The node may still have it; wait, or send the same signed operation again. It cannot be paid twice.");

        public bool Equals(SendError? other) => other is not null && other.GetType() == GetType() && other.Message == Message;
        public override bool Equals(object? obj) => Equals(obj as SendError);
        public override int GetHashCode() => HashCode.Combine(GetType(), Message);
    }

    /// <summary>Who the recipient is, once resolved.</summary>
    /// <param name="Wallet">Set when the address is one of our wallets or address-book entries.</param>
    public sealed record RecipientInfo(Address Address, Wallet? Wallet)
    {
        public AccountProfile? Profile { get; init; }
        public IReadOnlyList<string> Domains { get; init; } = [];

        public bool IsVerified => Wallet is not null;
        /// <summary>Best display name: our alias, else the TzProfiles name, else the <c>.tez</c> name, else the short address.</summary>
        public string DisplayName => Wallet?.Alias ?? Profile?.Name ?? Domains.FirstOrDefault() ?? Address.Shortened();

        public bool Equals(RecipientInfo? other) =>
            other is not null && Address == other.Address && Wallet == other.Wallet && Profile == other.Profile && Domains.SequenceEqual(other.Domains);
        public override int GetHashCode() => HashCode.Combine(Address, Wallet);
    }

    public Wallet Sender { get; }
    private readonly IReadOnlyList<Wallet> wallets;
    private readonly IChainService chain;
    /// <summary>Etherlink's Michelson interface: only tz1, tz2 and tz3 accounts can be sent to there.</summary>
    public bool ImplicitAccountsOnly { get; }
    private readonly Func<Wallet, string?, SigningKey?> signerProvider;
    public decimal? Spendable { get; }

    public string RecipientText
    {
        get => recipientText;
        set
        {
            if (value == recipientText) return;
            recipientText = value;
            OnPropertyChanged();
            RecipientChanged();
        }
    }
    private string recipientText = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Amount), nameof(CanProceed))] private string amountText = "";
    /// <summary>Password for an encrypted sender key; asked for on the confirm step.</summary>
    [ObservableProperty] private string passphrase = "";
    public bool NeedsPassphrase => Sender.KeyKind == KeyKind.Encrypted;
    /// <summary>The sender's key is on a Ledger: the user approves on the device while we wait.</summary>
    public bool SignsOnLedger => Sender.KeyKind == KeyKind.Ledger;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanProceed))] private RecipientInfo? recipient;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanProceed))] private bool isResolving;
    [ObservableProperty] private IReadOnlyList<Wallet> suggestions = [];
    [ObservableProperty] private TransferEstimate? estimate;
    [ObservableProperty] private Step currentStep = new Step.Compose();
    [ObservableProperty] private string? errorMessage;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanProceed))] private bool isBusy;
    /// <summary>What was signed and handed to the node; kept so an unknown outcome can be watched or resent, never redone.</summary>
    [ObservableProperty] private SentOperation? sent;

    private CancellationTokenSource? resolveTask;

    public SendViewModel(Wallet sender, IReadOnlyList<Wallet> wallets, IChainService chain, decimal? spendable,
                         Func<Wallet, string?, SigningKey?> signerProvider, bool implicitAccountsOnly = false)
    {
        Sender = sender;
        this.wallets = wallets;
        this.chain = chain;
        Spendable = spendable;
        ImplicitAccountsOnly = implicitAccountsOnly;
        this.signerProvider = signerProvider;
        suggestions = Candidates("");
    }

    /// <summary>Whether <c>address</c> can receive here: anything on Tezos, tz1 to tz3 on Etherlink.</summary>
    public bool Accepts(Address address) => address.IsValidAccount && (!ImplicitAccountsOnly || address.IsUsableOnEtherlink);

    /// <summary>Recovery is checked before composing a replacement, including after restarting the app.</summary>
    public async Task RecoverPending()
    {
        if (CurrentStep is not Step.Compose) return;
        IsBusy = true;
        try
        {
            if (await chain.PendingOperationFor(Sender.Address) is { } recovered)
            {
                Sent = recovered;
                await Follow(recovered);
            }
        }
        catch (Exception e) { ErrorMessage = e.Message; }
        finally { IsBusy = false; }
    }

    // ---- Compose ---------------------------------------------------------------------------------

    public decimal? Amount
    {
        get
        {
            var text = AmountText.Trim().Replace(",", ".");
            return decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : null;
        }
    }

    public bool CanProceed => Recipient is not null && Amount is not null && !IsBusy && !IsResolving;

    /// <summary>Everything except a small reserve for the fee.</summary>
    public void UseMaximum()
    {
        if (Spendable is not > 0 || Spendable is not { } spendable) return;
        var value = Math.Max(0, spendable - 0.001m);
        AmountText = value.ToString(CultureInfo.InvariantCulture);
    }

    public void Choose(Wallet wallet) => RecipientText = wallet.Address.Value;

    private IReadOnlyList<Wallet> Candidates(string text)
    {
        var needle = text.Trim().ToLowerInvariant();
        return wallets
            .Where(w => w.Address != Sender.Address && Accepts(w.Address))
            .Where(w => needle.Length == 0 || w.Alias.ToLowerInvariant().Contains(needle) || w.Address.Value.ToLowerInvariant().StartsWith(needle, StringComparison.Ordinal))
            .ToList();
    }

    private async void RecipientChanged()
    {
        resolveTask?.Cancel();
        Recipient = null;
        ErrorMessage = null;
        var text = RecipientText.Trim();
        Suggestions = Candidates(text);

        var typed = new Address(text);
        if (typed.IsValidAccount && !Accepts(typed))
        {
            ErrorMessage = "On Etherlink, send only to tz1, tz2 or tz3 accounts.";
        }
        else if (typed.IsValidAccount)
        {
            Resolve(typed);
        }
        else if (text.ToLowerInvariant().EndsWith(".tez", StringComparison.Ordinal) && text.Length > 4)
        {
            IsResolving = true;
            var cancel = resolveTask = new CancellationTokenSource();
            try { await Task.Delay(350, cancel.Token); }
            catch (TaskCanceledException) { return; }
            Address? address = null;
            try { address = await chain.ResolveDomain(text); } catch (Exception) { }
            if (cancel.IsCancellationRequested) return;
            if (address is { } found && Accepts(found)) Resolve(found, text);
            else IsResolving = false;
        }
        else
        {
            IsResolving = false;
        }
    }

    private async void Resolve(Address address, string? domain = null)
    {
        var known = wallets.FirstOrDefault(w => w.Address == address);
        Recipient = new RecipientInfo(address, known) { Domains = domain is null ? [] : [domain.ToLowerInvariant()] };
        IsResolving = false;
        resolveTask?.Cancel();
        var cancel = resolveTask = new CancellationTokenSource();
        var profileTask = Try(() => chain.AccountProfileFor(address));
        var domainsTask = Try(() => chain.DomainsFor(address));
        var (profile, domains) = (await profileTask, await domainsTask);
        if (cancel.IsCancellationRequested || Recipient?.Address != address) return;
        Recipient = Recipient with { Profile = profile };
        if (domains is { Count: > 0 }) Recipient = Recipient with { Domains = domains };

        static async Task<T?> Try<T>(Func<Task<T>> load)
        {
            try { return await load(); }
            catch (Exception) { return default; }
        }
    }

    // ---- Confirm ---------------------------------------------------------------------------------

    public async Task ProceedToConfirm()
    {
        await RecoverPending();
        if (CurrentStep is not Step.Compose) return;
        ErrorMessage = null;
        if (Recipient is not { } recipient) { ErrorMessage = new SendError.NoRecipient().Message; return; }
        if (Amount is not { } amount) { ErrorMessage = new SendError.InvalidAmount().Message; return; }
        if (recipient.Address == Sender.Address) { ErrorMessage = new SendError.SendingToSelf().Message; return; }
        if (Spendable is { } spendable && amount > spendable) { ErrorMessage = new SendError.InsufficientFunds(spendable).Message; return; }

        IsBusy = true;
        try
        {
            var estimate = await chain.EstimateTransfer(Sender, recipient.Address, amount);
            if (Spendable is { } available && estimate.Total > available)
            {
                ErrorMessage = new SendError.InsufficientFunds(available).Message;
                return;
            }
            Estimate = estimate;
            CurrentStep = new Step.Confirm();
        }
        catch (Exception e) { ErrorMessage = e.Message; }
        finally { IsBusy = false; }
    }

    public void BackToCompose()
    {
        CurrentStep = new Step.Compose();
        Estimate = null;
        ErrorMessage = null;
    }

    // ---- Send ------------------------------------------------------------------------------------

    /// <summary>
    /// Signs and sends exactly what the confirm step showed: the approved estimate's limits are
    /// the operation's limits (S14). A lost reply leaves the sheet on the unknown-outcome step,
    /// where only watching the chain or resending the same bytes is possible (S15).
    /// </summary>
    public async Task Send()
    {
        if (CurrentStep is not Step.Confirm || Recipient is not { } recipient || Amount is not { } amount) return;
        ErrorMessage = null;
        IsBusy = true;
        try
        {
            if (NeedsPassphrase && Passphrase.Length == 0) throw new SendError.PassphraseRequired();
            if (Estimate?.Limits is not { } limits) throw new SendError.NotEstimated();
            var signer = signerProvider(Sender, NeedsPassphrase ? Passphrase : null) ?? throw new SendError.NoSecretKey();
            CurrentStep = new Step.Sending();
            var sent = await chain.SendTransfer(Sender, signer, recipient.Address, amount, limits);
            Sent = sent;
            await Follow(sent);
        }
        catch (Exception e)
        {
            ErrorMessage = e.Message;
            if (CurrentStep is Step.Sending) CurrentStep = new Step.Confirm();
        }
        finally { IsBusy = false; }
    }

    /// <summary>Watches the chain for the sent operation; an unknown outcome stays visible until it is seen.</summary>
    private async Task Follow(SentOperation sent)
    {
        CurrentStep = sent.Injected ? new Step.Sent(sent.Hash) : new Step.Unknown(sent.Hash);
        try
        {
            var level = await chain.WaitForConfirmation(sent.Hash);
            CurrentStep = new Step.Confirmed(sent.Hash, level);
        }
        catch (Exception e)
        {
            ErrorMessage = sent.Injected ? e.Message : new SendError.NotSeenYet(sent.Hash).Message;
        }
    }

    /// <summary>Sends the same signed bytes again after an unknown outcome. Nothing new is signed.</summary>
    public async Task Rebroadcast()
    {
        if (CurrentStep is not Step.Unknown || Sent is not { } sent || IsBusy) return;
        ErrorMessage = null;
        IsBusy = true;
        try
        {
            var again = await chain.Rebroadcast(sent);
            Sent = again;
            await Follow(again);
        }
        catch (Exception e) { ErrorMessage = e.Message; }
        finally { IsBusy = false; }
    }
}
