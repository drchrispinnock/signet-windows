using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;

namespace Signet.Services;

/// <summary>
/// Hosts the bundled Taquito script (<c>taquito-bridge.js</c>, shared unchanged with the Mac app)
/// inside V8 and exposes its exported functions to C# as async calls.
///
/// V8, like JavaScriptCore, is single-threaded, so every touch of the engine happens on one
/// dedicated thread. The polyfills in <c>upstream/TaquitoBridge/src/polyfills.js</c> call back into the
/// <c>__signet</c> native object installed here for timers, HTTP and randomness; those run on the same
/// thread. <c>__signet</c> is a small JavaScript object forwarding to <see cref="Host"/>, so values cross
/// the boundary as plain strings and numbers.
///
/// Private keys enter the JavaScript runtime only for the one operation they sign.
/// </summary>
public sealed class TaquitoBridge
{
    public abstract class BridgeError(string message) : Exception(message)
    {
        public sealed class BundleMissing() : BridgeError("taquito-bridge.js is missing from the app (check out the upstream submodule)");
        public sealed class LoadFailed(string detail) : BridgeError($"Taquito bridge failed to load: {detail}");
        public sealed class JavaScript(string detail) : BridgeError(detail);
        public sealed class NotAFunction(string name) : BridgeError($"TaquitoBridge.{name} is not a function");
        public sealed class TimedOut(string name, TimeSpan after) : BridgeError($"TaquitoBridge.{name} did not finish within {after.TotalSeconds:0.##} s");
        public sealed class NotASigningCall(string name) : BridgeError($"TaquitoBridge.{name} is not a signing call");
        public sealed class InputTooLarge() : BridgeError("Bridge arguments exceed the input budget");
        /// <summary>A read still waiting when another read's deadline discarded the context.</summary>
        public sealed class ContextDiscarded(string name) : BridgeError($"TaquitoBridge.{name} was stopped: the bridge was reset after a timeout");

        /// <summary>
        /// The <see cref="ChainError"/> this failure stands for when the bridge's text is one every signing path
        /// words the same way (wrong password, Ledger trouble, a key for another address); otherwise
        /// the bridge error itself, untouched.
        /// </summary>
        public Exception AsChainError => this is JavaScript && ChainError.FromBridgeMessage(Message) is { } known ? known : this;

        /// <summary>
        /// Runs a bridge call and rethrows a bridge failure through <c>AsChainError</c>. Any other error
        /// passes through unchanged, so a typed catch keeps exactly its scope.
        /// </summary>
        public static async Task<T> Typed<T>(Func<Task<T>> body)
        {
            try { return await body(); }
            catch (BridgeError e) when (e.AsChainError is ChainError known) { throw known; }
        }
    }

    public static TaquitoBridge Shared { get; } = new();

    private readonly BlockingCollection<Action> queue = [];
    private readonly HttpClient http;
    private V8ScriptEngine? engine;
    private Exception? loadError;
    private readonly Dictionary<int, Timer> timers = [];
    private readonly Dictionary<int, CancellationTokenSource> fetches = [];
    /// <summary>A call waiting for JavaScript to settle it. Bridge thread only.</summary>
    private sealed record PendingCall(TaskCompletionSource<JSONValue> Completion, string Name, bool Signing, int Generation)
    {
        public Timer? Deadline { get; set; }
    }
    private readonly Dictionary<int, PendingCall> calls = [];
    private int nextCallID;
    /// <summary>Bumped when the context is discarded, so a late timer, fetch or Ledger reply for the old one is dropped. Bridge thread only.</summary>
    private int generation;
    private readonly Lock gate = new();

    /// <summary>
    /// Backing store for the Octez Connect SDK. In memory unless the app sets one: only the
    /// production composition picks the wallet directory's file (upstream's audit repair).
    /// </summary>
    public IBridgeStorage Storage
    {
        get { lock (gate) return storage; }
        set { lock (gate) storage = value; }
    }
    private IBridgeStorage storage = new InMemoryBridgeStorage();

    /// <summary>The most a single HTTP reply may carry into JavaScript (S19).</summary>
    public const int MaxResponseBytes = 32 * 1024 * 1024;

    /// <summary>Every fetch goes to public HTTPS only, through <see cref="PublicHTTPClient"/> (the dApp bridge, S12).</summary>
    public bool RequirePublicEndpoints { get; }

    /// <summary>
    /// Where signed operations are recorded before they are injected (S15). In memory by default;
    /// the app points it at the wallet folder so an unknown outcome survives a restart.
    /// </summary>
    public OperationJournal OperationJournal
    {
        get { lock (gate) return operationJournal; }
        set { lock (gate) operationJournal = value; }
    }
    private OperationJournal operationJournal = new();

    /// <summary>Receives JSON events pushed from JavaScript (dApp requests). Called on the bridge thread.</summary>
    public Action<string>? EventHandler
    {
        get { lock (gate) return eventHandler; }
        set { lock (gate) eventHandler = value; }
    }
    private Action<string>? eventHandler;

    /// <param name="logPath">The bridge log; <c>LogFilePath</c> unless a test names another.</param>
    /// <param name="systemLog">Also receives every recorded line (upstream's system log; the debugger's output here).</param>
    public TaquitoBridge(HttpClient? http = null, bool requirePublicEndpoints = false, string? logPath = null, Action<string>? systemLog = null)
    {
        RequirePublicEndpoints = requirePublicEndpoints;
        this.logPath = logPath ?? LogFilePath;
        this.systemLog = systemLog ?? (line => System.Diagnostics.Debug.WriteLine(line));
        // Each fetch has URLSession's default 60 s timeout, so a node that never answers still settles the promise.
        this.http = http ?? (requirePublicEndpoints ? PublicHTTPClient.Shared : new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
        var thread = new Thread(() => { foreach (var work in queue.GetConsumingEnumerable()) work(); })
        {
            IsBackground = true,
            Name = "Signet Taquito bridge",
        };
        thread.Start();
    }

    // ---- Public API ------------------------------------------------------------------------------

    /// <summary>
    /// What a bridge export may do, which decides how its call is treated (S30): a read gets a
    /// deadline; a signing call runs to its outcome.
    /// </summary>
    public enum CallKind { Read, Signing }

    /// <summary>
    /// Every export that signs, injects or holds a Ledger session. A call to one of these has no
    /// deadline and is never abandoned: abandoning it could lose an operation outcome or release
    /// a device still signing. Upstream's <c>npm test</c> (signing-exports.cjs) checks the JavaScript
    /// sources against this list.
    /// </summary>
    public static readonly IReadOnlySet<string> SigningCalls = new HashSet<string>
    {
        "sendTransfer", "sendStakingOperation", "sendRawOperation", "sendGovernanceOperation", "sendEtherlinkDeposit", "sendEtherlinkLink", "sendEtherlinkGovernance", "sendTokenTransfer", "sendShieldDeposit", "sendOutboxExecution",
        "injectSignedBytes", "multisigOriginate", "multisigSubmit", "multisigSign", "signPayload", "provePossession",
        "octezConnectExecute", "octezConnectSign", "ledgerGetAddress", "ledgerAppVersion",
    };

    public static CallKind KindOf(string name) => SigningCalls.Contains(name) ? CallKind.Signing : CallKind.Read;

    /// <summary>A read's deadline when the caller names none (upstream's 60 s).</summary>
    public static readonly TimeSpan DefaultReadDeadline = TimeSpan.FromSeconds(60);

    /// <summary>The most the string arguments of one call may add up to, in UTF-8 bytes.</summary>
    public const int MaxInputBytes = 2 << 20;

    /// <summary>
    /// The entry point for a signing or injecting call: <c>name</c> must be declared in
    /// <c>SigningCalls</c>, so a caller cannot sign through a read with a deadline by mistake.
    /// </summary>
    public Task<JSONValue> Sign(string name, params object?[] args) =>
        KindOf(name) == CallKind.Signing ? Call(name, args) : Task.FromException<JSONValue>(new BridgeError.NotASigningCall(name));

    /// <summary>
    /// Calls <c>TaquitoBridge.&lt;name&gt;(...args)</c> and awaits the result, unwrapping promises.
    /// A read has <c>DefaultReadDeadline</c>; signing, injection and Ledger sessions (<c>SigningCalls</c>)
    /// have none, whichever entry point they come through.
    /// </summary>
    public Task<JSONValue> Call(string name, params object?[] args) => CallWithin(null, name, args);

    /// <summary>
    /// <c>Call</c> with a read deadline of <c>timeout</c> (upstream's <c>call(_:_:timeout:)</c>). When a read's
    /// deadline passes its HTTP work is cancelled and the context discarded before reuse, so the
    /// next call starts clean (S30). Windows: calls are not serialised as upstream's are, so a
    /// read that times out while a signing call is in flight fails alone and the context is kept.
    /// </summary>
    public Task<JSONValue> CallWithin(TimeSpan? timeout, string name, params object?[] args)
    {
        var completion = new TaskCompletionSource<JSONValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (args.Sum(a => a is string s ? Encoding.UTF8.GetByteCount(s) : 0) > MaxInputBytes)
            return Task.FromException<JSONValue>(new BridgeError.InputTooLarge());
        var signing = KindOf(name) == CallKind.Signing;
        var deadline = signing ? (TimeSpan?)null : timeout ?? DefaultReadDeadline;
        var argsJSON = JsonSerializer.Serialize(args);
        Post(() =>
        {
            try
            {
                var context = LoadedEngine();
                var id = ++nextCallID;
                var pending = new PendingCall(completion, name, signing, generation);
                calls[id] = pending;
                if (deadline is { } after)
                    pending.Deadline = new Timer(_ => Post(() => Expire(id, after)), null, after, Timeout.InfiniteTimeSpan);
                context.Invoke("__signet_call", id, name, argsJSON);
            }
            catch (BridgeError e) { Abandon(completion); completion.TrySetException(e); }
            catch (ScriptEngineException e) { Abandon(completion); completion.TrySetException(new BridgeError.JavaScript(e.Message)); }
        });
        return completion.Task;
    }

    private void Post(Action work) => queue.Add(work);

    /// <summary>Tests only: runs <c>script</c> in the loaded context on the bridge thread.</summary>
    internal Task ExecuteForTesting(string script)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try { LoadedEngine().Execute(script); completion.TrySetResult(); }
            catch (Exception e) { completion.TrySetException(e); }
        });
        return completion.Task;
    }

    /// <summary>Forgets a call that never reached JavaScript, so its deadline cannot discard the context.</summary>
    private void Abandon(TaskCompletionSource<JSONValue> completion)
    {
        foreach (var (id, pending) in calls.Where(c => c.Value.Completion == completion).ToList())
        {
            pending.Deadline?.Dispose();
            calls.Remove(id);
        }
    }

    /// <summary>Called from JavaScript when a call settles: a JSON result or an error message.</summary>
    private void CallDone(int id, string? resultJSON, string? error)
    {
        if (!calls.Remove(id, out var pending)) return;
        pending.Deadline?.Dispose();
        if (error is not null) pending.Completion.TrySetException(new BridgeError.JavaScript(error));
        else pending.Completion.TrySetResult(JSONValue.Parse(resultJSON));
    }

    /// <summary>A read's deadline passed: it fails, and the context is discarded unless a signing call still needs it.</summary>
    private void Expire(int id, TimeSpan after)
    {
        if (!calls.TryGetValue(id, out var pending) || pending.Generation != generation) return;
        calls.Remove(id);
        pending.Deadline?.Dispose();
        Note($"{pending.Name} timed out after {after.TotalSeconds:0.##} s");
        pending.Completion.TrySetException(new BridgeError.TimedOut(pending.Name, after));
        if (calls.Values.Any(c => c.Signing)) return;
        DiscardContext();
    }

    /// <summary>
    /// Drops the JavaScript context and everything it had running: HTTP requests are cancelled,
    /// timers stopped, Ledger leases released, waiting reads failed. The next call loads a fresh
    /// context. Bridge thread only.
    /// </summary>
    private void DiscardContext()
    {
        generation++;
        foreach (var (_, cancel) in fetches) cancel.Cancel();
        fetches.Clear();
        foreach (var (_, timer) in timers) timer.Dispose();
        timers.Clear();
        foreach (var (_, lease) in ledgerLeases) LedgerSessions.Shared.Release(lease.Lease, lease.Device);
        ledgerLeases.Clear();
        foreach (var (_, pending) in calls)
        {
            pending.Deadline?.Dispose();
            pending.Completion.TrySetException(new BridgeError.ContextDiscarded(pending.Name));
        }
        calls.Clear();
        var old = engine;
        engine = null;
        try { old?.Dispose(); } catch (Exception) { }
        EventHandler?.Invoke("{\"kind\":\"stopped\",\"message\":\"The dApp connection timed out; reconnect before signing.\"}");
    }

    // ---- Loading ---------------------------------------------------------------------------------

    private V8ScriptEngine LoadedEngine()
    {
        if (engine is not null) return engine;
        if (loadError is not null) throw loadError;
        try
        {
            engine = MakeEngine();
            return engine;
        }
        catch (Exception e)
        {
            loadError = e as BridgeError ?? new BridgeError.LoadFailed(e.Message);
            throw loadError;
        }
    }

    private V8ScriptEngine MakeEngine()
    {
        using var stream = typeof(TaquitoBridge).Assembly.GetManifestResourceStream("taquito-bridge.js")
            ?? throw new BridgeError.BundleMissing();
        var source = new StreamReader(stream, Encoding.UTF8).ReadToEnd();

        var context = new V8ScriptEngine("Signet", V8ScriptEngineFlags.None);
        context.AddHostObject("__signet_host", new Host(this));
        context.Execute("signet-host.js", HostScript);
        try
        {
            context.Execute("taquito-bridge.js", source);
        }
        catch (ScriptEngineException e)
        {
            context.Dispose();
            throw new BridgeError.LoadFailed(e.ErrorDetails ?? e.Message);
        }
        if (context.Evaluate("typeof TaquitoBridge") as string != "object")
        {
            context.Dispose();
            throw new BridgeError.LoadFailed("TaquitoBridge global not defined after evaluating bundle");
        }
        return context;
    }

    /// <summary>
    /// Installed before the bundle: the console, the <c>__signet</c> native object the polyfills expect
    /// (forwarding to the C# host), and <c>__signet_call</c>, which runs one exported function and
    /// reports its settled result back as JSON.
    /// </summary>
    private const string HostScript = """
        var console = (function () {
          // Objects are never serialised as they are: the dApp SDK hands its transport (whose
          // client holds the communication key pair) and other state to the console, and a
          // key written as decimal bytes carries no prefix the line redactor would catch. So
          // fields whose names say secret are dropped, byte containers become their length,
          // and depth, width and the total number of nodes are capped (S11).
          var SENSITIVE = /secret|private|seed|keypair|key_pair|passphrase|password|mnemonic|^sk$|token/i;
          function isByte(x) { return typeof x === "number" && x >= 0 && x <= 255 && x === (x | 0); }
          function scrub(a, depth, budget) {
            if (a === null || typeof a !== "object") return a;
            if (budget.nodes-- <= 0) return "<…>";
            if (depth > 6) return "<nested>";
            if (a instanceof ArrayBuffer || ArrayBuffer.isView(a)) return "<" + a.byteLength + " bytes>";
            if (Array.isArray(a)) {
              if (a.length >= 16 && a.every(isByte)) return "<" + a.length + " bytes>";
              var items = a.slice(0, 64).map(function (x) { return scrub(x, depth + 1, budget); });
              if (a.length > 64) items.push("<" + (a.length - 64) + " more>");
              return items;
            }
            var keys = Object.keys(a);
            if (keys.length >= 16 && keys.every(function (k) { return /^\d+$/.test(k); })) return "<" + keys.length + " bytes>";
            var out = {};
            for (var i = 0; i < keys.length; i++) {
              if (i >= 64) { out["…"] = "<" + (keys.length - 64) + " more>"; break; }
              var k = keys[i];
              out[k] = SENSITIVE.test(k) ? "<redacted>" : scrub(a[k], depth + 1, budget);
            }
            return out;
          }
          function fmt(args) { return Array.prototype.map.call(args, function (a) {
            if (a instanceof Error) return a.message + (a.stack ? "\n" + a.stack : "");
            if (typeof a === "object") { try { return JSON.stringify(scrub(a, 0, { nodes: 2000 })); } catch (e) { return "<object>"; } }
            return String(a);
          }).join(" "); }
          function log(level) { return function () { __signet_host.log(level, fmt(arguments)); }; }
          return {
            log: log("log"), info: log("info"), warn: log("warn"), error: log("error"), debug: log("debug"),
            trace: log("trace"), group: log("group"), groupCollapsed: log("group"), groupEnd: function () {},
            table: log("table"), dir: log("dir"),
            assert: function (ok) { if (!ok) __signet_host.log("assert", fmt(Array.prototype.slice.call(arguments, 1))); },
            count: function () {}, countReset: function () {}, time: function () {}, timeEnd: function () {}, timeLog: function () {},
          };
        })();

        globalThis.__signet = {
          setTimer: function (id, ms) { __signet_host.setTimer(Number(id), Number(ms)); },
          clearTimer: function (id) { __signet_host.clearTimer(Number(id)); },
          randomBytes: function (count) {
            var hex = __signet_host.randomBytes(Number(count)), bytes = new Array(hex.length / 2);
            for (var i = 0; i < bytes.length; i++) bytes[i] = parseInt(hex.substr(i * 2, 2), 16);
            return bytes;
          },
          fetch: function (id, url, method, headersJSON, body) {
            __signet_host.fetch(Number(id), String(url), String(method), String(headersJSON), body == null ? null : String(body));
          },
          cancelFetch: function (id) { __signet_host.cancelFetch(Number(id)); },
          storageGet: function (key) { var v = __signet_host.storageGet(String(key)); return v == null ? null : v; },
          storageSet: function (key, value) { __signet_host.storageSet(String(key), String(value)); },
          storageDelete: function (key) { __signet_host.storageDelete(String(key)); },
          octezConnectEvent: function (json) { __signet_host.octezConnectEvent(String(json)); },
          operationRead: function () { return __signet_host.operationRead(); },
          operationSave: function (json) { return __signet_host.operationSave(String(json)); },
          operationRemove: function (hash) { return __signet_host.operationRemove(String(hash)); },
          ledgerDevices: function () { return __signet_host.ledgerDevices(); },
          ledgerExchange: function (id, deviceId, apduHex, timeoutMs) {
            __signet_host.ledgerExchange(Number(id), String(deviceId), String(apduHex), Number(timeoutMs));
          },
          ledgerSessionBegin: function (id, deviceId) { __signet_host.ledgerSessionBegin(Number(id), String(deviceId)); },
          ledgerSessionEnd: function (id) { __signet_host.ledgerSessionEnd(Number(id)); },
        };

        globalThis.__signet_call = function (id, name, argsJSON) {
          function done(json, error) { __signet_host.callDone(id, json, error); }
          try {
            var api = globalThis.TaquitoBridge, fn = api && api[name];
            if (typeof fn !== "function") { done(null, "TaquitoBridge." + name + " is not a function"); return; }
            Promise.resolve(fn.apply(api, JSON.parse(argsJSON))).then(function (value) {
              var json;
              try { json = JSON.stringify(value === undefined ? null : value); }
              catch (e) { done(null, "unserialisable result from " + name + ": " + e.message); return; }
              done(json, null);
            }, function (reason) {
              done(null, (reason && reason.message) || String(reason) || "unknown error");
            });
          } catch (e) {
            done(null, String(e));
          }
        };
        """;

    /// <summary>What JavaScript reaches as <c>__signet_host</c>. Every member runs on the bridge thread.</summary>
    public sealed class Host(TaquitoBridge bridge)
    {
        [ScriptMember("log")]
        public void Log(string level, string text) => bridge.Record($"[{level}] {text}");

        [ScriptMember("callDone")]
        public void CallDone(double id, string? json, string? error) => bridge.CallDone((int)id, json, error);

        [ScriptMember("setTimer")]
        public void SetTimer(double id, double ms) => bridge.SetTimer((int)id, ms);

        [ScriptMember("clearTimer")]
        public void ClearTimer(double id)
        {
            if (bridge.timers.Remove((int)id, out var timer)) timer.Dispose();
        }

        [ScriptMember("randomBytes")]
        public string RandomBytes(double count) =>
            Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(Math.Max(0, (int)count)));

        [ScriptMember("fetch")]
        public void Fetch(double id, string url, string method, string headersJSON, string? body) =>
            bridge.StartFetch((int)id, url, method, headersJSON, body);

        [ScriptMember("cancelFetch")]
        public void CancelFetch(double id)
        {
            if (bridge.fetches.Remove((int)id, out var cancel)) cancel.Cancel();
        }

        [ScriptMember("storageGet")]
        public string? StorageGet(string key) => bridge.Storage.Get(key);

        [ScriptMember("storageSet")]
        public void StorageSet(string key, string value) => bridge.Storage.Set(key, value);

        [ScriptMember("storageDelete")]
        public void StorageDelete(string key) => bridge.Storage.Delete(key);

        [ScriptMember("octezConnectEvent")]
        public void OctezConnectEvent(string json) => bridge.EventHandler?.Invoke(json);

        // The operation journal (S15): read returns the records or an error object; save and
        // remove return "" on success, else the message the bridge throws.
        [ScriptMember("operationRead")]
        public string OperationRead()
        {
            try { return bridge.OperationJournal.Read(); }
            catch (Exception) { return "{\"error\":\"Operation recovery storage cannot be read\"}"; }
        }

        [ScriptMember("operationSave")]
        public string OperationSave(string json)
        {
            try { bridge.OperationJournal.Save(json); return ""; }
            catch (Exception) { return "Could not persist the signed operation; nothing was injected"; }
        }

        [ScriptMember("operationRemove")]
        public string OperationRemove(string hash)
        {
            try { bridge.OperationJournal.Remove(hash); return ""; }
            catch (Exception) { return "Could not update operation recovery storage"; }
        }

        // Ledger: USB HID is native (LedgerHID); the bridge's Transport subclass calls these.
        [ScriptMember("ledgerDevices")]
        public string LedgerDevices() => JsonSerializer.Serialize(
            LedgerHID.Shared.Devices().Select(d => new { id = d.Id, name = d.Name, productID = d.ProductID }));

        [ScriptMember("ledgerExchange")]
        public void LedgerExchange(double id, string deviceID, string apduHex, double timeoutMs) =>
            bridge.LedgerExchange((int)id, deviceID, apduHex, timeoutMs);

        [ScriptMember("ledgerSessionBegin")]
        public void LedgerSessionBegin(double id, string deviceID) => bridge.LedgerSessionBegin((int)id, deviceID);

        [ScriptMember("ledgerSessionEnd")]
        public void LedgerSessionEnd(double id) => bridge.LedgerSessionEnd((int)id);
    }

    // ---- Timers ----------------------------------------------------------------------------------

    private void SetTimer(int id, double ms)
    {
        if (timers.Remove(id, out var old)) old.Dispose();
        var owner = generation;
        timers[id] = new Timer(_ => Post(() =>
        {
            if (owner != generation || !timers.Remove(id, out var fired)) return;  // cleared or discarded meanwhile
            fired.Dispose();
            Invoke("__signet_timerFired", id);
        }), null, TimeSpan.FromMilliseconds(Math.Max(0, ms)), Timeout.InfiniteTimeSpan);
    }

    /// <summary>Calls a <c>__signet_*</c> callback; a throwing callback is logged, never propagated.</summary>
    private void Invoke(string function, params object?[] args)
    {
        try { engine?.Invoke(function, args); }
        catch (ScriptEngineException e) { Record($"[host] {function} threw: {e.ErrorDetails ?? e.Message}"); }
    }

    // ---- fetch -----------------------------------------------------------------------------------

    private void StartFetch(int id, string urlString, string method, string headersJSON, string? body)
    {
        Trace($"fetch #{id} {method} {Redacted(urlString)}");
        if (!Uri.TryCreate(urlString, UriKind.Absolute, out var url))
        {
            FinishFetch(id, error: "Invalid URL");
            return;
        }
        if (RequirePublicEndpoints)
        {
            try { PublicHTTPClient.Check(url); }
            catch (PublicHTTPClient.PublicEndpointError e) { FinishFetch(id, error: e.Message); return; }
        }
        if (fetches.Count >= 32)
        {
            FinishFetch(id, error: "Too many active bridge HTTP requests");
            return;
        }
        var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8);
        var headers = TryParseHeaders(headersJSON);
        foreach (var (key, value) in headers)
        {
            if (request.Headers.TryAddWithoutValidation(key, value)) continue;
            if (request.Content is not null)
            {
                request.Content.Headers.Remove(key);
                request.Content.Headers.TryAddWithoutValidation(key, value);
            }
        }

        var cancel = new CancellationTokenSource();
        fetches[id] = cancel;
        var owner = generation;
        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                var text = await ReadLimited(response.Content, timeout.Token);
                var responseHeaders = response.Headers.Concat(response.Content.Headers)
                    .GroupBy(h => h.Key.ToLowerInvariant())
                    .ToDictionary(g => g.Key, g => string.Join(", ", g.SelectMany(h => h.Value)));
                var status = (int)response.StatusCode;
                Post(() =>
                {
                    if (owner != generation || !fetches.Remove(id)) return;  // cancelled or discarded
                    FinishFetch(id, status, response.ReasonPhrase ?? "", JsonSerializer.Serialize(responseHeaders), text);
                });
            }
            catch (Exception e)
            {
                // Whatever went wrong, JavaScript must hear back or its promise never settles.
                Post(() =>
                {
                    if (owner != generation || !fetches.Remove(id)) return;  // cancelled or discarded
                    FinishFetch(id, error: e is HttpRequestException or TaskCanceledException ? e.Message : $"{e.GetType().Name}: {e.Message}");
                });
            }
            finally
            {
                request.Dispose();
            }
        });
    }

    /// <summary>The reply body, refused once it passes <c>MaxResponseBytes</c> (S19) so an endless body never reaches JavaScript.</summary>
    private static async Task<string> ReadLimited(HttpContent content, CancellationToken cancel)
    {
        if (content.Headers.ContentLength is > MaxResponseBytes) throw new HttpRequestException(TooLarge);
        await using var stream = await content.ReadAsStreamAsync(cancel);
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancel)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes) throw new HttpRequestException(TooLarge);
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private const string TooLarge = "The server's reply was larger than 32 MB and was dropped.";

    private static Dictionary<string, string> TryParseHeaders(string json)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    private void FinishFetch(int id, int status = 0, string statusText = "", string headersJSON = "{}", string body = "", string? error = null)
    {
        // Status and size only: bodies can hold relay traffic and account data (S11).
        Trace($"fetch #{id} ← {error ?? $"{status} {Encoding.UTF8.GetByteCount(body)} bytes"}");
        Invoke("__signet_fetchDone", id, status, statusText, headersJSON, body, error);
    }

    // ---- Ledger ----------------------------------------------------------------------------------

    private void LedgerExchange(int id, string deviceID, string apduHex, double timeoutMs)
    {
        Trace($"ledger #{id} → {apduHex[..Math.Min(8, apduHex.Length)]} {apduHex.Length / 2} bytes");
        var apdu = Convert.FromHexString(apduHex.Length % 2 == 0 ? apduHex : apduHex[..^1]);
        var owner = generation;
        LedgerHID.Shared.Exchange(deviceID, apdu, TimeSpan.FromMilliseconds(timeoutMs), (response, error) => Post(() =>
        {
            if (owner != generation) return;
            if (response is not null)
            {
                var hex = Convert.ToHexString(response).ToLowerInvariant();
                Trace($"ledger #{id} ← {hex[^Math.Min(4, hex.Length)..]}");
                Invoke("__signet_ledgerDone", id, hex, null);
            }
            else
            {
                Trace($"ledger #{id} ← {error}");
                Invoke("__signet_ledgerDone", id, "", error ?? "Ledger exchange failed");
            }
        }));
    }

    /// <summary>Leases held by this context's Ledger sessions: JavaScript session id → (lease, device). Bridge thread only.</summary>
    private readonly Dictionary<int, (Guid Lease, string Device)> ledgerLeases = [];

    /// <summary>
    /// A Ledger session (one whole signing or derivation, many APDUs) waits for the process-wide
    /// device lease, so another bridge context cannot interleave its own APDUs (<c>LedgerSessions</c>).
    /// </summary>
    private void LedgerSessionBegin(int id, string deviceID)
    {
        var owner = generation;
        _ = Task.Run(async () =>
        {
            try
            {
                var lease = await LedgerSessions.Shared.Acquire(deviceID);
                Post(() =>
                {
                    if (owner != generation)
                    {
                        LedgerSessions.Shared.Release(lease, deviceID);
                        return;
                    }
                    ledgerLeases[id] = (lease, deviceID);
                    Invoke("__signet_ledgerSessionReady", id, null);
                });
            }
            catch (Exception e)
            {
                Post(() => { if (owner == generation) Invoke("__signet_ledgerSessionReady", id, e.Message); });
            }
        });
    }

    private void LedgerSessionEnd(int id)
    {
        if (ledgerLeases.Remove(id, out var lease)) LedgerSessions.Shared.Release(lease.Lease, lease.Device);
    }

    // ---- Log -------------------------------------------------------------------------------------

    /// <summary>
    /// Last few hundred console lines from JavaScript, newest last. Handy when no debugger is
    /// attached (tests, support).
    /// </summary>
    public IReadOnlyList<string> ConsoleLines
    {
        get { lock (gate) return consoleLines.ToList(); }
    }
    private readonly List<string> consoleLines = [];

    /// <summary>
    /// Rolling log of bridge activity for support: <c>%LOCALAPPDATA%\Signet\Logs\bridge.log</c>, rotated to
    /// <c>bridge.log.1</c> once it passes <c>MaxLogBytes</c> (S11).
    /// </summary>
    public static string LogFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Signet", "Logs", "bridge.log");
    public const long MaxLogBytes = 5_000_000;
    private readonly string logPath;
    private readonly Action<string> systemLog;
    private StreamWriter? logWriter;
    private long logBytes;

    /// <summary>Appends a line to <c>ConsoleLines</c> from C# (fetch tracing).</summary>
    private void Trace(string line) => Record($"[host] {line}");

    /// <summary>Lets app code leave a line in the bridge log next to the JavaScript traffic.</summary>
    public void Note(string line) => Record($"[app] {line}");

    /// <summary>
    /// A URL as it may appear in the log: scheme, host and path only. Query strings and
    /// fragments can carry tokens (the relay's sync endpoint does) and are dropped (S11).
    /// </summary>
    public static string Redacted(string urlString)
    {
        if (!Uri.TryCreate(urlString, UriKind.Absolute, out var url) || url.Host.Length == 0) return "<url>";
        var path = url.AbsolutePath == "/" ? "" : url.AbsolutePath;
        if (path.Length > 80) path = path[..80] + "…";
        return $"{url.Scheme}://{url.Host}{path}";
    }

    public const int MaxLineLength = 1024;

    private static readonly System.Text.RegularExpressions.Regex LongHex = new("[0-9a-fA-F]{48,}");
    private static readonly System.Text.RegularExpressions.Regex Secrets =
        new(@"\b(edsk|edesk|spsk|spesk|p2sk|p2esk|BLsk|BLesk|mdsk|mdesk|edsig|spsig1|p2sig|BLsig|mdsig|sig)[1-9A-HJ-NP-Za-km-z]{20,}");
    private static readonly System.Text.RegularExpressions.Regex Uuid =
        new(@"\b([0-9a-fA-F]{8})-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b");
    private static readonly System.Text.RegularExpressions.Regex ByteObject =
        new(@"\{(?:\s*""\d+""\s*:\s*\d{1,3}\s*,){15,}\s*""\d+""\s*:\s*\d{1,3}\s*\}");
    private static readonly System.Text.RegularExpressions.Regex ByteArray =
        new(@"\[(?:\s*\d{1,3}\s*,){15,}\s*\d{1,3}\s*\]");

    /// <summary>
    /// A log line with anything that could be a secret or private content kept out: at most
    /// <c>MaxLineLength</c> characters; hex or base58 runs long enough to be keys, signatures or
    /// payloads replaced by their length; UUIDs cut to their first group (the dApp SDK derives
    /// a replacement communication key from a UUID seed it prints when the relay account is
    /// deactivated, and message ids are UUIDs too, so the stub keeps lines tellable apart);
    /// and runs of decimal bytes, how a key serialises as an object or array, replaced by
    /// their count (S11).
    /// </summary>
    public static string RedactedLine(string line)
    {
        var text = LongHex.Replace(line, m => $"<{m.Length} hex chars>");
        text = Secrets.Replace(text, m => $"{m.Groups[1].Value}<redacted>");
        text = Uuid.Replace(text, m => $"<uuid {m.Groups[1].Value}…>");
        text = ByteObject.Replace(text, m => $"<{m.Value.Split(':').Length - 1} bytes>");
        text = ByteArray.Replace(text, m => $"<{m.Value.Split(',').Length} bytes>");
        if (text.Length > MaxLineLength) text = text[..MaxLineLength] + "…";
        return text;
    }

    /// <summary>
    /// The one way a line reaches any sink: the system log, <c>ConsoleLines</c> and the log file all
    /// get the same redacted, capped text (S11).
    /// </summary>
    private void Record(string rawLine)
    {
        var line = RedactedLine(rawLine);
        systemLog(line);
        lock (gate)
        {
            consoleLines.Add(line);
            if (consoleLines.Count > 300) consoleLines.RemoveRange(0, consoleLines.Count - 300);
            try
            {
                RotateLogIfNeeded();
                logWriter ??= OpenLog();
                var text = $"{DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} {line}\n";
                logWriter.Write(text);
                logBytes += Encoding.UTF8.GetByteCount(text);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private StreamWriter OpenLog()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        var info = new FileInfo(logPath);
        if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException($"{logPath} is a link; not logging to it.");
        logBytes = info.Exists ? info.Length : 0;
        return new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false))
        {
            AutoFlush = true,
        };
    }

    /// <summary>Moves a full log to <c>bridge.log.1</c> (replacing the previous one) so the next line starts a new file.</summary>
    private void RotateLogIfNeeded()
    {
        if (logWriter is null && File.Exists(logPath)) logBytes = new FileInfo(logPath).Length;
        if (logBytes <= MaxLogBytes) return;
        logWriter?.Dispose();
        logWriter = null;
        File.Move(logPath, logPath + ".1", overwrite: true);
        logBytes = 0;
    }
}
