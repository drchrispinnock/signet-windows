using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using Signet.Models;

namespace Signet.Services;

/// <summary>
/// Owns the Octez Connect session: starts the SDK in the bridge, surfaces incoming dApp
/// requests one at a time for the UI to approve, and sends the responses.
///
/// The bridge only forwards requests it has rebuilt from authenticated peer state and checked
/// against the stored grants (S04, S08); this side checks the grant again right before signing,
/// keeps a bounded queue (S19), and never treats a reply that failed to reach the dApp as done
/// (S17): the signed outcome is kept and only the reply is retried, so nothing is signed or
/// injected twice. Touched on the UI thread only; bridge events are posted back to it.
/// </summary>
public sealed partial class DAppConnectionManager : ObservableObject
{
    public abstract class ConnectError(string message) : Exception(message)
    {
        public sealed class NotOurWallet(Address address) : ConnectError($"The dApp asked for {address.Shortened()}, which is not one of your accounts.");
        public sealed class NoUsableKey(string alias) : ConnectError($"Signet cannot sign with “{alias}” (no key on disk or Ledger).");
        public sealed class UnsupportedNetwork(string type) : ConnectError($"The dApp wants the “{type}” network, which Signet does not have.");
        public sealed class RefusedPayload(string why) : ConnectError(why);
        public sealed class NotGranted(string why) : ConnectError(why);
    }

    /// <summary>Where a pairing stands: the dApp has to join the relay room before it can ask anything.</summary>
    public abstract record PairingState
    {
        public sealed record Idle : PairingState;
        public sealed record Waiting(string Name) : PairingState;
        public sealed record Paired(string Name) : PairingState;
        public sealed record Failed(string Reason) : PairingState;
    }

    /// <summary>The most requests that may wait for the user at once; later ones are refused.</summary>
    public const int MaxPending = 16;

    [ObservableProperty] private bool isStarted;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Current))] private IReadOnlyList<DAppRequest> pending = [];
    [ObservableProperty] private IReadOnlyList<DAppPermission> permissions = [];
    [ObservableProperty] private string? lastError;
    /// <summary>Something to show briefly after a request completes, e.g. "Sent ooABC…".</summary>
    [ObservableProperty] private string? lastOutcome;
    [ObservableProperty] private PairingState pairing = new PairingState.Idle();
    /// <summary>
    /// Requests whose work is done (operation injected, message signed, permission chosen) but
    /// whose reply the dApp has not received yet: id → the reply to resend (S17).
    /// </summary>
    [ObservableProperty] private IReadOnlyDictionary<string, UndeliveredReply> undelivered = new Dictionary<string, UndeliveredReply>();

    /// <param name="Summary">What was done, for the user: an operation hash, "signed", "connected".</param>
    public sealed record UndeliveredReply(string ResponseJSON, string Summary, int Attempts);

    private readonly TaquitoBridge bridge;
    private readonly IBridgeStorage storage;
    private readonly Func<IReadOnlyList<Wallet>> walletsProvider;
    private readonly Func<Task<IReadOnlyList<DAppPermission>>>? permissionLoader;
    private readonly Func<Wallet, string?, SigningKey?> signerProvider;
    private readonly SynchronizationContext? ui = SynchronizationContext.Current;
    private Task? startTask;
    private Task? stopTask;
    private Guid sessionGeneration = Guid.NewGuid();
    private bool invalidated;
    /// <summary>Only when the user turned developer diagnostics on (S11): the SDK logs decrypted traffic.</summary>
    public bool Diagnostics { get; }

    public DAppRequest? Current => Pending.FirstOrDefault();

    public DAppConnectionManager(IBridgeStorage storage, Func<IReadOnlyList<Wallet>> wallets, Func<Wallet, string?, SigningKey?> signer,
                                 TaquitoBridge? bridge = null, bool diagnostics = false,
                                 Func<Task<IReadOnlyList<DAppPermission>>>? permissionLoader = null)
    {
        this.bridge = bridge ?? new TaquitoBridge(requirePublicEndpoints: true);
        this.storage = storage;
        Diagnostics = diagnostics;
        this.permissionLoader = permissionLoader;
        walletsProvider = wallets;
        signerProvider = signer;
    }

    /// <summary>A bridge call with upstream's read deadline. The call itself runs on; only waiting for it stops.</summary>
    private Task<JSONValue> Call(string name, int seconds, params object?[] args) =>
        bridge.Call(name, args).WaitAsync(TimeSpan.FromSeconds(seconds));

    // ---- Lifecycle -------------------------------------------------------------------------------

    /// <summary>Starts the SDK. Concurrent calls share one attempt (S18).</summary>
    public async Task Start()
    {
        if (stopTask is not null) await stopTask;
        if (IsStarted) return;
        if (startTask is not null) { await startTask; return; }
        invalidated = false;
        startTask = StartNow();
        try { await startTask; }
        finally { startTask = null; }
    }

    private async Task StartNow()
    {
        bridge.Storage = storage;
        bridge.EventHandler = json =>
        {
            if (ui is null) Handle(json);
            else ui.Post(_ => Handle(json), null);
        };
        try
        {
            bridge.Note("Octez Connect starting");
            await Call("octezConnectStart", 60, "Signet", "https://raw.githubusercontent.com/drchrispinnock/signet/main/docs/images/signet-256.png", Diagnostics);
            IsStarted = true;
            LastError = null;
            bridge.Note("Octez Connect started");
            await ReloadPermissions();
        }
        catch (Exception e)
        {
            IsStarted = false;
            LastError = $"Octez Connect failed to start: {e.Message}";
            bridge.Note($"Octez Connect failed to start: {e.Message}");
        }
    }

    /// <summary>
    /// Stops the SDK client and drops every waiting request (each dApp gets an abort). Used when
    /// the wallet directory changes: the next directory gets its own manager and storage (S08).
    /// </summary>
    public void Invalidate()
    {
        invalidated = true;
        sessionGeneration = Guid.NewGuid();
        Permissions = [];
    }

    public async Task Stop()
    {
        Invalidate();
        if (stopTask is not null) { await stopTask; return; }
        stopTask = StopNow();
        try { await stopTask; }
        finally { stopTask = null; }
    }

    private async Task StopNow()
    {
        if (startTask is not null) await startTask;
        foreach (var request in Pending) await RespondError(request.Id, "ABORTED_ERROR");
        Pending = [];
        Undelivered = new Dictionary<string, UndeliveredReply>();
        try { await Call("octezConnectStop", 60); } catch (Exception) { }
        if (bridge.Storage == storage) bridge.EventHandler = null;
        IsStarted = false;
    }

    /// <summary>Stops and restarts the SDK client, e.g. after a failed start.</summary>
    public async Task Restart()
    {
        await Stop();
        await Start();
    }

    /// <summary>
    /// Pairs with a dApp from its pairing code. Returns the dApp's name; the pairing completes in
    /// the background and <c>Pairing</c> reports when the dApp has answered (S18).
    /// </summary>
    public async Task<string> Pair(string code)
    {
        if (!IsStarted) await Start();
        if (!IsStarted) throw new ConnectError.RefusedPayload(LastError ?? "Octez Connect is not running.");
        bridge.Note($"Pairing with a {code.Length}-character code");
        var result = await Call("octezConnectPair", 30, code);
        var name = result["name"]?.StringValue ?? "dApp";
        Pairing = new PairingState.Waiting(name);
        return name;
    }

    /// <summary>Test seam: grants to use instead of the SDK's stored ones (the SDK needs the relay to start).</summary>
    public IReadOnlyList<DAppPermission>? PermissionsOverride { get; set; }

    public async Task ReloadPermissions()
    {
        if (PermissionsOverride is { } overridden) { Permissions = overridden; return; }
        try { Permissions = await CurrentPermissions(); }
        catch (Exception e) { Permissions = []; LastError = e.Message; }
    }

    private async Task<IReadOnlyList<DAppPermission>> CurrentPermissions()
    {
        if (PermissionsOverride is { } overridden) return overridden;
        if (permissionLoader is not null) return await permissionLoader();
        var list = (await Call("octezConnectPermissions", 30)).ArrayValue
            ?? throw new ConnectError.NotGranted("Current dApp permissions could not be verified.");
        return list.Select(PermissionFrom).OfType<DAppPermission>().ToList();
    }

    public static DAppPermission? PermissionFrom(JSONValue item)
    {
        if (item["accountIdentifier"]?.StringValue is not { } account || item["senderId"]?.StringValue is not { } sender) return null;
        var meta = item["appMetadata"];
        return new DAppPermission(
            account, sender,
            item["address"]?.StringValue ?? "",
            meta?["name"]?.StringValue ?? "dApp",
            Uri.TryCreate(meta?["icon"]?.StringValue, UriKind.Absolute, out var icon) ? icon : null,
            item["network"]?["type"]?.StringValue ?? "mainnet",
            (item["scopes"]?.ArrayValue ?? []).Select(s => s.StringValue).OfType<string>().ToList(),
            item["connectedAt"]?.DoubleValue is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms) : null,
            Uri.TryCreate(item["network"]?["rpcUrl"]?.StringValue, UriKind.Absolute, out var rpc) ? rpc : null);
    }

    /// <summary>
    /// Revokes a grant. Requests that grant covered are refused too, so revocation takes effect
    /// on what is already queued (S08).
    /// </summary>
    public async Task Disconnect(DAppPermission permission)
    {
        try { await Call("octezConnectRemovePermission", 30, permission.AccountIdentifier, permission.SenderId); } catch (Exception) { }
        await ReloadPermissions();
        await RejectPending(r => r.App.SenderId == permission.SenderId && !r.IsPermission);
    }

    public async Task DisconnectAll()
    {
        try { await Call("octezConnectRemoveAll", 30); } catch (Exception) { }
        await ReloadPermissions();
        await RejectPending(_ => true);
    }

    private async Task RejectPending(Func<DAppRequest, bool> matches)
    {
        foreach (var request in Pending.Where(matches).ToList())
        {
            await RespondError(request.Id, "NOT_GRANTED_ERROR");
            Finish(request);
        }
    }

    // ---- Incoming --------------------------------------------------------------------------------

    /// <summary>Handles one event pushed by the bridge (<c>request</c>, <c>paired</c>, <c>refused</c>, <c>error</c>, <c>stopped</c>).</summary>
    public void Handle(string eventJSON)
    {
        if (eventJSON.Length > 384 * 1024) return;
        JsonObject? @event;
        try { @event = JsonNode.Parse(eventJSON) as JsonObject; }
        catch (JsonException) { return; }
        if (@event?["kind"] is not JsonValue k || !k.TryGetValue<string>(out var kind)) return;
        string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        // Only the shape is logged: request contents are the dApp's business (S11).
        bridge.Note($"dApp event {kind} {Str((@event["message"] as JsonObject)?["type"])}");
        switch (kind)
        {
            case "stopped":
                IsStarted = false;
                Permissions = [];
                Pending = [];
                LastError = Str(@event["message"]);
                break;
            case "error":
                LastError = Str(@event["message"]);
                if (Pairing is PairingState.Waiting) Pairing = new PairingState.Failed(LastError ?? "Pairing failed");
                return;
            case "paired":
                Pairing = new PairingState.Paired(Str(@event["name"]) ?? "dApp");
                LastOutcome = $"Paired with {Str(@event["name"]) ?? "the dApp"}";
                return;
            case "refused":
                LastError = $"Refused a request from a dApp: {Str(@event["reason"]) ?? "not allowed"}";
                return;
            case "request":
                break;
            default:
                return;
        }
        if (DAppRequest.Parse(eventJSON) is not { } request) return;
        if (request is DAppRequest.Unsupported unsupported)
        {
            LastError = $"Unsupported dApp request: {unsupported.Type}";
            _ = RespondError(request.Id, "NOT_GRANTED_ERROR");
            return;
        }
        if (request.SignRefusal is { } why)
        {
            LastError = $"Refused a signing request from “{request.App.Name}”: {why}";
            _ = RespondError(request.Id, "SIGNATURE_TYPE_NOT_SUPPORTED");
            return;
        }
        if (Pending.Count >= MaxPending)
        {
            LastError = $"Too many dApp requests are waiting; “{request.App.Name}” was refused.";
            _ = RespondError(request.Id, "ABORTED_ERROR");
            return;
        }
        if (Pending.Any(r => r.Id == request.Id)) return;
        Pending = [.. Pending, request];
        _ = ReloadPermissions();
    }

    private void Finish(DAppRequest request)
    {
        Pending = Pending.Where(r => r.Id != request.Id).ToList();
        Undelivered = Undelivered.Where(p => p.Key != request.Id).ToDictionary(p => p.Key, p => p.Value);
    }

    // ---- Grants (S08) ----------------------------------------------------------------------------

    /// <summary>The grant that covers this request right now, read fresh from the SDK, or why there is none.</summary>
    public async Task<DAppPermission> GrantFor(DAppRequest request)
    {
        var generation = sessionGeneration;
        if (invalidated) throw new ConnectError.NotGranted("This wallet's dApp session has ended.");
        var fresh = await CurrentPermissions();
        if (invalidated || generation != sessionGeneration) throw new ConnectError.NotGranted("The dApp session changed while checking permissions.");
        Permissions = fresh;
        return GrantFor(request, fresh);
    }

    public static DAppPermission GrantFor(DAppRequest request, IReadOnlyList<DAppPermission> permissions)
    {
        Address source;
        string scope;
        (string Type, Uri? RpcURL)? network = null;
        switch (request)
        {
            case DAppRequest.Operation op:
                (source, scope, network) = (op.Source, "operation_request", (op.NetworkType, op.RpcURL));
                break;
            case DAppRequest.SignPayload sign:
                (source, scope) = (sign.Source, "sign");
                break;
            default:
                throw new ConnectError.RefusedPayload("not a request that needs a grant");
        }
        if (request.App.SenderId.Length == 0) throw new ConnectError.NotGranted("The request carries no authenticated sender.");
        var candidates = permissions.Where(p => p.SenderId == request.App.SenderId && p.Address == source.Value && p.Scopes.Contains(scope)).ToList();
        if (candidates.Count == 0)
            throw new ConnectError.NotGranted($"“{request.App.Name}” has no permission to {(scope == "sign" ? "sign messages" : "request operations")} for {source.Shortened()}. Connect it again first.");
        if (network is { } wanted)
        {
            return candidates.FirstOrDefault(p => p.NetworkType == wanted.Type && (p.NetworkType != "custom" || DAppRequest.SameEndpoint(p.NetworkRPCURL, wanted.RpcURL)))
                ?? throw new ConnectError.NotGranted($"“{request.App.Name}” is connected on {candidates[0].NetworkType}, not {wanted.Type}.");
        }
        return candidates[0];
    }

    // ---- Answers ---------------------------------------------------------------------------------

    public async Task ApprovePermission(DAppRequest request, Wallet wallet)
    {
        if (request is not DAppRequest.Permission permission) return;
        if (Undelivered.TryGetValue(permission.Id, out var reply)) { await Redeliver(request, reply); return; }
        if (wallet.PublicKey is not { } publicKey)
        {
            LastError = $"“{wallet.Alias}” has no public key, so it cannot be shared with a dApp.";
            await RespondError(permission.Id, "NOT_GRANTED_ERROR");
            Finish(request);
            return;
        }
        var network = new JsonObject { ["type"] = permission.NetworkType };
        if (permission.RpcURL is { } rpc) network["rpcUrl"] = rpc.AbsoluteString();
        var granted = permission.Scopes.Where(s => s is "sign" or "operation_request").Select(s => (JsonNode?)s).ToArray();
        var response = new JsonObject
        {
            ["type"] = "permission_response", ["id"] = permission.Id,
            ["network"] = network, ["scopes"] = new JsonArray(granted),
            ["publicKey"] = publicKey, ["address"] = wallet.Address.Value,
            ["walletType"] = "implicit",
        };
        await Deliver(request, response, $"Connected “{request.App.Name}” to {wallet.Alias}");
        await ReloadPermissions();
    }

    /// <summary>
    /// Simulates the request's batch from its source account so the sheet can show exact fees,
    /// burn, totals and effects. Nothing is signed; a read-only signer stands in for the account.
    /// </summary>
    public async Task<DAppPreparedBatch> Prepare(DAppRequest request)
    {
        if (request is not DAppRequest.Operation op) throw new ConnectError.RefusedPayload("not an operation request");
        await GrantFor(request);
        var wallet = WalletFor(op.Source) ?? throw new ConnectError.NotOurWallet(op.Source);
        if (wallet.PublicKey is not { } publicKey) throw new ConnectError.NoUsableKey(wallet.Alias);
        var network = DAppRequest.NetworkFor(op.NetworkType, op.RpcURL) ?? throw new ConnectError.UnsupportedNetwork(op.NetworkType);
        var r = await Call("octezConnectPrepare", 120, network.RpcURL.AbsoluteString(), op.Source.Value, publicKey, op.OperationsJSON, network.ExpectedChainID);
        if (r["prepared"]?.StringValue is not { } prepared || r["operations"]?.ArrayValue is not { } list)
            throw new TaquitoBridge.BridgeError.JavaScript($"unexpected prepare payload: {r}");
        return new DAppPreparedBatch(
            op.Id, list.Select(Summary).ToList(),
            Mutez.ToTez(r["reveal"]?["feeMutez"]?.StringValue),
            Mutez.ToTez(r["reveal"]?["burnMutez"]?.StringValue),
            Mutez.ToTez(r["totalAmountMutez"]?.StringValue) ?? 0,
            Mutez.ToTez(r["totalFeeMutez"]?.StringValue) ?? 0,
            Mutez.ToTez(r["totalBurnMutez"]?.StringValue) ?? 0,
            Mutez.ToTez(r["totalDebitMutez"]?.StringValue) ?? 0,
            prepared);
    }

    /// <summary>Signs and sends the batch exactly as it was prepared and shown (<c>prepared</c> must be for this request).</summary>
    public async Task ApproveOperation(DAppRequest request, DAppPreparedBatch prepared, string? passphrase)
    {
        if (request is not DAppRequest.Operation op) return;
        if (Undelivered.TryGetValue(op.Id, out var reply)) { await Redeliver(request, reply); return; }
        try
        {
            if (prepared.RequestID != op.Id) throw new ConnectError.RefusedPayload("The prepared batch is not for this request.");
            await GrantFor(request);
            var (wallet, signer) = SignerFor(op.Source, passphrase);
            var network = DAppRequest.NetworkFor(op.NetworkType, op.RpcURL) ?? throw new ConnectError.UnsupportedNetwork(op.NetworkType);
            var result = await bridge.Call("octezConnectExecute", network.RpcURL.AbsoluteString(), signer.BridgeSpec, prepared.PreparedJSON, network.ExpectedChainID);
            if (result["hash"]?.StringValue is not { Length: > 0 } hash) throw new TaquitoBridge.BridgeError.JavaScript("no operation hash");
            var injected = result["injected"]?.BoolValue ?? true;
            var summary = injected
                ? $"Sent {hash[..Math.Min(12, hash.Length)]}… for “{request.App.Name}” from {wallet.Alias}"
                : $"Signed {hash[..Math.Min(12, hash.Length)]}… for “{request.App.Name}”; the node's answer was lost, so check the explorer before retrying";
            await Deliver(request, new JsonObject { ["type"] = "operation_response", ["id"] = op.Id, ["transactionHash"] = hash }, summary);
        }
        catch (Exception e)
        {
            LastError = Friendly(e);
            // Wrong password or Ledger not ready: leave the request up so the user can retry.
            if (IsRetryable(e)) return;
            await RespondError(op.Id, e is ConnectError ? "NOT_GRANTED_ERROR" : "TRANSACTION_INVALID_ERROR");
            Finish(request);
        }
    }

    public async Task ApproveSignature(DAppRequest request, string? passphrase)
    {
        if (request is not DAppRequest.SignPayload sign) return;
        if (Undelivered.TryGetValue(sign.Id, out var reply)) { await Redeliver(request, reply); return; }
        try
        {
            if (request.SignRefusal is { } why) throw new ConnectError.RefusedPayload(why);
            await GrantFor(request);
            var (_, signer) = SignerFor(sign.Source, passphrase);
            var result = await bridge.Call("octezConnectSign", signer.BridgeSpec, sign.Payload, sign.SigningType);
            if (result["signature"]?.StringValue is not { } signature) throw new TaquitoBridge.BridgeError.JavaScript("no signature");
            await Deliver(request, new JsonObject
            {
                ["type"] = "sign_payload_response", ["id"] = sign.Id, ["signingType"] = sign.SigningType, ["signature"] = signature,
            }, $"Signed a message for “{request.App.Name}”");
        }
        catch (Exception e)
        {
            LastError = Friendly(e);
            if (IsRetryable(e)) return;
            await RespondError(sign.Id, e is ConnectError ? "NOT_GRANTED_ERROR" : "SIGNATURE_TYPE_NOT_SUPPORTED");
            Finish(request);
        }
    }

    public async Task Reject(DAppRequest request)
    {
        if (Undelivered.ContainsKey(request.Id))
        {
            // The work was done; the user gives up on telling the dApp. Nothing is undone.
            Finish(request);
            return;
        }
        await RespondError(request.Id, request.IsPermission ? "NOT_GRANTED_ERROR" : "ABORTED_ERROR");
        Finish(request);
    }

    /// <summary>Sends the reply the request's work produced again (S17). Never signs or injects anything.</summary>
    public async Task ResendReply(DAppRequest request)
    {
        if (Undelivered.TryGetValue(request.Id, out var reply)) await Redeliver(request, reply);
    }

    public void ClearMessages()
    {
        LastError = null;
        LastOutcome = null;
        if (Pairing is PairingState.Paired or PairingState.Failed) Pairing = new PairingState.Idle();
    }

    // ---- Delivery (S17) --------------------------------------------------------------------------

    /// <summary>
    /// Sends a completed request's reply. On failure the reply is kept with the request so it can
    /// be resent; the request stays in the queue, marked undelivered, and is never redone.
    /// </summary>
    private Task Deliver(DAppRequest request, JsonObject message, string summary) =>
        Redeliver(request, new UndeliveredReply(message.ToJsonString(), summary, 0));

    private async Task Redeliver(DAppRequest request, UndeliveredReply reply)
    {
        try
        {
            await Call("octezConnectRespond", 60, reply.ResponseJSON);
            LastOutcome = reply.Summary;
            Finish(request);
        }
        catch (Exception e)
        {
            var kept = reply with { Attempts = reply.Attempts + 1 };
            Undelivered = new Dictionary<string, UndeliveredReply>(Undelivered) { [request.Id] = kept };
            LastError = $"{reply.Summary}. The dApp did not get the reply ({e.Message}); you can send it again without redoing anything.";
            bridge.Note($"reply to {request.Id} not delivered: {e.Message}");
        }
    }

    // ---- Helpers ---------------------------------------------------------------------------------

    /// <summary>The wallet for <c>source</c> and what signs for it.</summary>
    private (Wallet, SigningKey) SignerFor(Address source, string? passphrase)
    {
        var wallet = WalletFor(source) ?? throw new ConnectError.NotOurWallet(source);
        if (!wallet.KeyKind.CanSign() || signerProvider(wallet, passphrase) is not { } signer) throw new ConnectError.NoUsableKey(wallet.Alias);
        return (wallet, signer);
    }

    public Wallet? WalletFor(Address source) => walletsProvider().FirstOrDefault(w => w.Address == source);

    /// <summary>What the batch does, without the node (no fees yet). Shown while <c>Prepare</c> runs.</summary>
    public async Task<IReadOnlyList<DAppOperationSummary>> Summaries(string operationsJSON)
    {
        try { return ((await Call("octezConnectDescribe", 30, operationsJSON)).ArrayValue ?? []).Select(Summary).ToList(); }
        catch (Exception) { return []; }
    }

    public static DAppOperationSummary Summary(JSONValue item) => new(
        item["kind"]?.StringValue ?? "?",
        item["destination"]?.StringValue,
        Mutez.ToTez(item["amountMutez"]?.StringValue ?? item["balanceMutez"]?.StringValue),
        item["entrypoint"]?.StringValue,
        item["delegate"]?.StringValue,
        item["parameters"]?.StringValue,
        (item["effects"]?.ArrayValue ?? [])
            .Select(e => e["text"]?.StringValue is { } text ? new DAppOperationSummary.Effect(text, e["warning"]?.BoolValue ?? false) : null)
            .OfType<DAppOperationSummary.Effect>().ToList(),
        item["opaque"]?.BoolValue ?? false,
        Mutez.ToTez(item["feeMutez"]?.StringValue),
        Mutez.ToTez(item["burnMutez"]?.StringValue),
        Mutez.ToTez(item["requestedFeeMutez"]?.StringValue));

    private async Task RespondError(string id, string errorType)
    {
        var json = new JsonObject { ["type"] = "error", ["id"] = id, ["errorType"] = errorType }.ToJsonString();
        try { await Call("octezConnectRespond", 60, json); }
        catch (Exception e) { bridge.Note($"error reply to {id} not delivered: {e.Message}"); }
    }

    private static ChainError? Known(Exception error) => error switch
    {
        ChainError chain => chain,
        TaquitoBridge.BridgeError.JavaScript js => ChainError.FromBridgeMessage(js.Message),
        _ => null,
    };

    /// <summary>Errors the user can fix and try again without the dApp hearing about it.</summary>
    private static bool IsRetryable(Exception error) => Known(error) is ChainError.WrongPassphrase or ChainError.LedgerDeclined
        or ChainError.LedgerAppNotOpen or ChainError.LedgerLocked or ChainError.LedgerNotConnected;

    private static string Friendly(Exception error) => Known(error)?.Message ?? error.Message;
}
