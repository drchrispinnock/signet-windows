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
    private readonly Dictionary<int, TaskCompletionSource<JSONValue>> calls = [];
    private int nextCallID;
    private readonly Lock gate = new();

    /// <summary>Backing store for the Octez Connect SDK. Set before the first call that loads the bundle.</summary>
    public IBridgeStorage Storage
    {
        get { lock (gate) return storage ??= new OctezConnectStorage(); }
        set { lock (gate) storage = value; }
    }
    private IBridgeStorage? storage;

    /// <summary>Receives JSON events pushed from JavaScript (dApp requests). Called on the bridge thread.</summary>
    public Action<string>? EventHandler
    {
        get { lock (gate) return eventHandler; }
        set { lock (gate) eventHandler = value; }
    }
    private Action<string>? eventHandler;

    public TaquitoBridge(HttpClient? http = null)
    {
        // URLSession's default request timeout, so a node that never answers still settles the promise.
        this.http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var thread = new Thread(() => { foreach (var work in queue.GetConsumingEnumerable()) work(); })
        {
            IsBackground = true,
            Name = "Signet Taquito bridge",
        };
        thread.Start();
    }

    // ---- Public API ------------------------------------------------------------------------------

    /// <summary>Calls <c>TaquitoBridge.&lt;name&gt;(...args)</c> and awaits the result, unwrapping promises.</summary>
    public Task<JSONValue> Call(string name, params object?[] args)
    {
        var completion = new TaskCompletionSource<JSONValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        var argsJSON = JsonSerializer.Serialize(args);
        Post(() =>
        {
            try
            {
                var context = LoadedEngine();
                var id = ++nextCallID;
                calls[id] = completion;
                context.Invoke("__signet_call", id, name, argsJSON);
            }
            catch (BridgeError e) { completion.TrySetException(e); }
            catch (ScriptEngineException e) { completion.TrySetException(new BridgeError.JavaScript(e.Message)); }
        });
        return completion.Task;
    }

    private void Post(Action work) => queue.Add(work);

    /// <summary>Called from JavaScript when a call settles: a JSON result or an error message.</summary>
    private void CallDone(int id, string? resultJSON, string? error)
    {
        if (!calls.Remove(id, out var completion)) return;
        if (error is not null) completion.TrySetException(new BridgeError.JavaScript(error));
        else completion.TrySetResult(JSONValue.Parse(resultJSON));
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
          function fmt(args) { return Array.prototype.map.call(args, function (a) {
            if (a instanceof Error) return a.message + (a.stack ? "\n" + a.stack : "");
            if (typeof a === "object") { try { return JSON.stringify(a); } catch (e) { return String(a); } }
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
          ledgerDevices: function () { return __signet_host.ledgerDevices(); },
          ledgerExchange: function (id, deviceId, apduHex, timeoutMs) {
            __signet_host.ledgerExchange(Number(id), String(deviceId), String(apduHex), Number(timeoutMs));
          },
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

        // Ledger: USB HID is native (LedgerHID); the bridge's Transport subclass calls these.
        [ScriptMember("ledgerDevices")]
        public string LedgerDevices() => JsonSerializer.Serialize(
            LedgerHID.Shared.Devices().Select(d => new { id = d.Id, name = d.Name, model = d.Model, productID = d.ProductID }));

        [ScriptMember("ledgerExchange")]
        public void LedgerExchange(double id, string deviceID, string apduHex, double timeoutMs) =>
            bridge.LedgerExchange((int)id, deviceID, apduHex, timeoutMs);
    }

    // ---- Timers ----------------------------------------------------------------------------------

    private void SetTimer(int id, double ms)
    {
        if (timers.Remove(id, out var old)) old.Dispose();
        timers[id] = new Timer(_ => Post(() =>
        {
            if (!timers.Remove(id, out var fired)) return;  // cleared meanwhile
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
        Trace($"fetch #{id} {method} {urlString}");
        if (!Uri.TryCreate(urlString, UriKind.Absolute, out var url))
        {
            FinishFetch(id, error: $"Invalid URL: {urlString}");
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
        _ = Task.Run(async () =>
        {
            try
            {
                using var response = await http.SendAsync(request, cancel.Token);
                var text = await response.Content.ReadAsStringAsync(cancel.Token);
                var responseHeaders = response.Headers.Concat(response.Content.Headers)
                    .GroupBy(h => h.Key.ToLowerInvariant())
                    .ToDictionary(g => g.Key, g => string.Join(", ", g.SelectMany(h => h.Value)));
                var status = (int)response.StatusCode;
                Post(() =>
                {
                    if (!fetches.Remove(id)) return;  // cancelled
                    FinishFetch(id, status, response.ReasonPhrase ?? "", JsonSerializer.Serialize(responseHeaders), text);
                });
            }
            catch (Exception e)
            {
                // Whatever went wrong, JavaScript must hear back or its promise never settles.
                Post(() =>
                {
                    if (!fetches.Remove(id)) return;  // cancelled
                    FinishFetch(id, error: e is HttpRequestException or TaskCanceledException ? e.Message : $"{e.GetType().Name}: {e.Message}");
                });
            }
            finally
            {
                request.Dispose();
            }
        });
    }

    private static Dictionary<string, string> TryParseHeaders(string json)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    private void FinishFetch(int id, int status = 0, string statusText = "", string headersJSON = "{}", string body = "", string? error = null)
    {
        var limit = body.Contains("next_batch") ? 3000 : 160;
        var preview = (body.Length > limit ? body[..limit] : body).TrimEnd();
        Trace($"fetch #{id} ← {error ?? $"{status} {preview}"}");
        Invoke("__signet_fetchDone", id, status, statusText, headersJSON, body, error);
    }

    // ---- Ledger ----------------------------------------------------------------------------------

    private void LedgerExchange(int id, string deviceID, string apduHex, double timeoutMs)
    {
        Trace($"ledger #{id} → {apduHex[..Math.Min(24, apduHex.Length)]}…");
        var apdu = Convert.FromHexString(apduHex.Length % 2 == 0 ? apduHex : apduHex[..^1]);
        LedgerHID.Shared.Exchange(deviceID, apdu, TimeSpan.FromMilliseconds(timeoutMs), (response, error) => Post(() =>
        {
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

    /// <summary>Rolling log of bridge activity for support: <c>%LOCALAPPDATA%\Signet\Logs\bridge.log</c>.</summary>
    public static string LogFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Signet", "Logs", "bridge.log");
    private StreamWriter? logWriter;

    /// <summary>Appends a line to <c>ConsoleLines</c> from C# (fetch tracing).</summary>
    private void Trace(string line) => Record($"[host] {line}");

    /// <summary>Lets app code leave a line in the bridge log next to the JavaScript traffic.</summary>
    public void Note(string line) => Record($"[app] {line}");

    private void Record(string line)
    {
        lock (gate)
        {
            consoleLines.Add(line);
            if (consoleLines.Count > 300) consoleLines.RemoveRange(0, consoleLines.Count - 300);
            try
            {
                logWriter ??= OpenLog();
                logWriter.WriteLine($"{DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} {line}");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static StreamWriter OpenLog()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogFilePath)!);
        var info = new FileInfo(LogFilePath);
        if (info.Exists && info.Length > 5_000_000) info.Delete();
        return new StreamWriter(new FileStream(LogFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
        {
            AutoFlush = true,
        };
    }
}
