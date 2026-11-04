using System.Net;
using System.Text;
using Signet.Models;
using Signet.Services;

namespace Signet.Tests;

/// <summary>
/// A server for <c>BoundedHTTPTests</c>, by path (upstream's <c>BoundedHTTPProtocol</c>). "Chunked"
/// replies carry no declared length; <c>/pending-*</c> replies never finish, and record when the
/// reader stops consuming them.
/// </summary>
internal sealed class BoundedHTTPHandler : HttpMessageHandler
{
    private static readonly HashSet<string> Stops = [];

    public static bool WasStopped(Uri url) { lock (Stops) return Stops.Contains(url.AbsoluteUri); }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!;
        HttpResponseMessage Respond(byte[] data, bool declared, HttpStatusCode status = HttpStatusCode.OK)
        {
            HttpContent content = declared ? new ByteArrayContent(data) : new StreamContent(new UndeclaredStream(new MemoryStream(data)));
            return new HttpResponseMessage(status) { Content = content };
        }
        var path = url.AbsolutePath;
        // A service under test appends its own path (TzKT's /v1/…), so the first segment picks the reply.
        var first = "/" + path.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var response = first switch
        {
            "/pending-large" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new PendingStream(url, 64 << 10, declared: 1 << 20)) },
            "/pending-chunked" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new PendingStream(url, 64 << 10, declared: null)) },
            "/declared-large" => Respond(new byte[1 << 20].Select(_ => (byte)0x20).ToArray(), declared: true),
            "/chunked-large" => Respond(new byte[1 << 20].Select(_ => (byte)0x20).ToArray(), declared: false),
            "/deep" => Respond(Encoding.UTF8.GetBytes(new string('[', 500) + new string(']', 500)), declared: true),
            "/small" => Respond("\"NetXdQprcVkpaWU\""u8.ToArray(), declared: true),
            "/rows" => Respond(Encoding.UTF8.GetBytes("[" + string.Join(",", Enumerable.Repeat(
                """{"type":"transaction","hash":"oo1","sender":{"address":"tz1a"},"target":{"address":"tz1b"},"amount":1,"timestamp":"2026-01-01T00:00:00Z","status":"applied"}""", 40)) + "]"), declared: true),
            _ => Respond("{}"u8.ToArray(), declared: true, HttpStatusCode.NotFound),
        };
        return Task.FromResult(response);
    }

    /// <summary>A body whose length is not known up front.</summary>
    private sealed class UndeclaredStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Sends <c>first</c> bytes, then waits forever; disposing it is the reader stopping.</summary>
    private sealed class PendingStream(Uri url, int first, long? declared) : Stream
    {
        private int sent;
        public override bool CanRead => true;
        public override bool CanSeek => declared is not null;
        public override bool CanWrite => false;
        public override long Length => declared ?? throw new NotSupportedException();
        public override long Position { get => sent; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (sent >= first) { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
            var n = Math.Min(count, first - sent);
            Array.Fill(buffer, (byte)0x20, offset, n);
            sent += n;
            return n;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (sent >= first) { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
            var n = Math.Min(buffer.Length, first - sent);
            buffer.Span[..n].Fill(0x20);
            sent += n;
            return n;
        }
        protected override void Dispose(bool disposing)
        {
            lock (Stops) Stops.Add(url.AbsoluteUri);
            base.Dispose(disposing);
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>S19: native readers take replies as a stream, capped, and JSON within a budget.</summary>
public class BoundedHTTPTests
{
    private static readonly HttpClient Session = new(new BoundedHTTPHandler()) { Timeout = Timeout.InfiniteTimeSpan };
    private static Uri Request(string path) => new("https://bounded.invalid" + path);

    [Fact]
    public async Task DeclaredAndUndeclaredOversizeRepliesAreRefused()
    {
        Assert.Equal(4096, (await Assert.ThrowsAsync<BoundedHTTP.Failure.TooLarge>(() => Http.Get(Session, Request("/declared-large"), limit: 4096))).Limit);
        Assert.Equal(4096, (await Assert.ThrowsAsync<BoundedHTTP.Failure.TooLarge>(() => Http.Get(Session, Request("/chunked-large"), limit: 4096))).Limit);
    }

    [Fact]
    public async Task ExcessiveNestingIsRefusedBeforeParsing()
    {
        await Assert.ThrowsAsync<BoundedHTTP.Failure.OverBudget>(() => Http.Get(Session, Request("/deep"), limit: 4096, json: BoundedHTTP.JSONLimits.Small));
        var raw = await Http.Get(Session, Request("/deep"), limit: 4096, json: BoundedHTTP.JSONLimits.Unchecked);
        Assert.Equal(1000, raw.Body.Length);
    }

    [Fact]
    public async Task FittingRepliesPassWithTheirStatus()
    {
        var reply = await Http.Get(Session, Request("/small"), limit: 4096);
        Assert.True(reply.IsSuccess && reply.Text == "\"NetXdQprcVkpaWU\"");
        var missing = await Http.Get(Session, Request("/nothing"), limit: 4096);
        Assert.True(missing.Status == 404 && !missing.IsSuccess);
    }

    [Fact]
    public async Task RefusalCancelsTheUnderlyingUnfinishedResponse()
    {
        foreach (var path in new[] { "/pending-large", "/pending-chunked" })
        {
            var url = Request(path + "?id=" + Guid.NewGuid());
            await Assert.ThrowsAsync<BoundedHTTP.Failure.TooLarge>(() => Http.Get(Session, url, TimeSpan.FromSeconds(3), limit: 4096));
            for (var i = 0; i < 50 && !BoundedHTTPHandler.WasStopped(url); i++) await Task.Delay(10, TestContext.Current.CancellationToken);
            Assert.True(BoundedHTTPHandler.WasStopped(url), "An over-budget response must stop even before the server finishes");
        }
    }

    [Fact]
    public async Task NativeReadersUseTheBoundedPath()
    {
        // TzKT: an account record over its cap fails before anything is parsed.
        var error = await Assert.ThrowsAsync<BoundedHTTP.Failure.TooLarge>(() =>
            new TzKTService(new Uri("https://bounded.invalid/declared-large"), Session).AccountProfileFor(new Address("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb")));
        Assert.Equal(TzKTService.RecordBytes, error.Limit);
        // A node's reply is refused when it is anything but small (the probe's cap; upstream reaches
        // NodeMonitor's probe on the shared session, which cannot be stubbed here).
        await Assert.ThrowsAsync<BoundedHTTP.Failure.TooLarge>(() => Http.Get(Session, Request("/declared-large"), limit: 16 << 10, json: BoundedHTTP.JSONLimits.Unchecked));
        // Rows beyond the requested page size are dropped even when the server sends more.
        var rows = await new TzKTService(new Uri("https://bounded.invalid/rows"), Session).RecentOperationsFor(new Address("tz1a"), 10);
        Assert.Equal(10, rows.Count);
    }
}

/// <summary>S11: every line the bridge records is redacted and capped, wherever it goes.</summary>
public class LoggingPolicyTests
{
    private const string Key = "edskRuR3azSfg5xTz6dLovR7yr7hgP9Qt6Ub8rgqJG7VTH6RhJ1vXXhfSwtxw1E2Lc3b3TvsgrnWXKVyLRQgm3Rx8cFXqQhd3";

    [Fact]
    public async Task ConsoleLinesFromJavaScriptAreRedactedAndCapped()
    {
        var hex = string.Concat(Enumerable.Repeat("ab", 64));
        var filler = new string('x', TaquitoBridge.MaxLineLength + 200);
        _ = await TaquitoBridge.Shared.Call("bridgeEcho", $"{Key} {hex} {filler}");
        var echoed = TaquitoBridge.Shared.ConsoleLines.Last(l => l.StartsWith("[log] echo: ", StringComparison.Ordinal));
        Assert.True(!echoed.Contains(Key) && !echoed.Contains(hex));
        Assert.True(echoed.Contains("edsk<redacted>") && echoed.Contains("<128 hex chars>"));
        Assert.True(echoed.Length <= TaquitoBridge.MaxLineLength + 1);
    }

    [Fact]
    public void BridgeNotesGoThroughTheSamePolicy()
    {
        var bridge = new TaquitoBridge(systemLog: _ => { });
        bridge.Note($"uncaught: {Key}");
        Assert.Equal("[app] uncaught: edsk<redacted>", bridge.ConsoleLines.LastOrDefault());
    }

    [Fact]
    public async Task SystemConsoleAndFileReceiveTheSameRedactedText()
    {
        var directory = Path.Combine(Path.GetTempPath(), "signet-logs-" + Guid.NewGuid().ToString("N"));
        try
        {
            var file = Path.Combine(directory, "bridge.log");
            var sink = new List<string>();
            var bridge = new TaquitoBridge(logPath: file, systemLog: line => { lock (sink) sink.Add(line); });
            var input = "edsk" + new string('R', 50) + " " + string.Concat(Enumerable.Repeat("ab", 64)) + " " + new string('x', 1200);
            _ = await bridge.Call("bridgeEcho", input);
            var console = bridge.ConsoleLines;
            lock (sink) Assert.Equal(console, sink);
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var text = new StreamReader(stream).ReadToEnd();
            foreach (var line in console)
            {
                Assert.DoesNotContain(new string('R', 50), line);
                Assert.DoesNotContain(string.Concat(Enumerable.Repeat("ab", 64)), line);
                Assert.True(line.Length <= TaquitoBridge.MaxLineLength + 1);
                Assert.Contains(line, text);
            }
        }
        finally { try { Directory.Delete(directory, recursive: true); } catch (IOException) { } }
    }

    /// <summary>Windows: the rotation itself (upstream's is checked by hand), with a full log planted first.</summary>
    [Fact]
    public void AFullLogIsRotatedToDotOne()
    {
        var directory = Path.Combine(Path.GetTempPath(), "signet-logs-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "bridge.log");
            File.WriteAllBytes(file, new byte[TaquitoBridge.MaxLogBytes + 1]);
            var bridge = new TaquitoBridge(logPath: file, systemLog: _ => { });
            bridge.Note("after rotation");
            Assert.Equal(TaquitoBridge.MaxLogBytes + 1, new FileInfo(file + ".1").Length);
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Assert.EndsWith("[app] after rotation\n", new StreamReader(stream).ReadToEnd());
        }
        finally { try { Directory.Delete(directory, recursive: true); } catch (IOException) { } }
    }
}

/// <summary>S30: every export that signs is declared as such (the JavaScript inventory is upstream's npm test).</summary>
public class SigningExportsTests
{
    [Fact]
    public async Task TheSigningEntryPointRefusesReads()
    {
        await Assert.ThrowsAsync<TaquitoBridge.BridgeError.NotASigningCall>(() => TaquitoBridge.Shared.Sign("version"));
        Assert.NotNull((await TaquitoBridge.Shared.Call("version")).StringValue);
    }
}

/// <summary>F02: every bridge failure goes through one conversion to <c>ChainError</c>.</summary>
public class BridgeErrorConversionTests
{
    [Fact]
    public async Task KnownMessagesBecomeChainErrorsAndEverythingElsePassesThrough()
    {
        (string Message, Type Expected)[] cases =
        [
            ("Failed to decrypt the key", typeof(ChainError.WrongPassphrase)), ("Invalid passphrase", typeof(ChainError.WrongPassphrase)),
            ("Ledger device: Condition of use not satisfied (denied by the user?) (0x6985)", typeof(ChainError.LedgerDeclined)),
            ("Ledger Device is busy (0x5515 LOCKED_DEVICE)", typeof(ChainError.LedgerLocked)),
            ("CLA_NOT_SUPPORTED (0x6e00)", typeof(ChainError.LedgerAppNotOpen)), ("INS_NOT_SUPPORTED", typeof(ChainError.LedgerAppNotOpen)),
            ("No Ledger is connected", typeof(ChainError.LedgerNotConnected)), ("the device was unplugged", typeof(ChainError.LedgerNotConnected)),
            ("Ledger does not hold this key", typeof(ChainError.LedgerWrongDevice)),
            ("the key does not derive the account's address tz1x", typeof(ChainError.KeyAddressMismatch)),
        ];
        foreach (var (message, expected) in cases)
        {
            Assert.IsType(expected, new TaquitoBridge.BridgeError.JavaScript(message).AsChainError);
            var thrown = await Assert.ThrowsAnyAsync<ChainError>(() => TaquitoBridge.BridgeError.Typed<int>(() => throw new TaquitoBridge.BridgeError.JavaScript(message)));
            Assert.IsType(expected, thrown);
        }
        Assert.Equal("Ledger does not hold this key", new TaquitoBridge.BridgeError.JavaScript("Ledger does not hold this key").AsChainError.Message);
        // Precedence is the message scan's: a decrypt failure wins over a Ledger code in the same text.
        Assert.IsType<ChainError.WrongPassphrase>(new TaquitoBridge.BridgeError.JavaScript("could not decrypt (0x6985)").AsChainError);
        // Unknown JavaScript text and every other bridge failure come back exactly as they were.
        TaquitoBridge.BridgeError[] others =
        [
            new TaquitoBridge.BridgeError.JavaScript("something else"), new TaquitoBridge.BridgeError.TimedOut("getHead", TimeSpan.FromSeconds(60)),
            new TaquitoBridge.BridgeError.NotAFunction("x"), new TaquitoBridge.BridgeError.BundleMissing(), new TaquitoBridge.BridgeError.LoadFailed("y"),
            new TaquitoBridge.BridgeError.NotASigningCall("z"),
        ];
        foreach (var error in others) Assert.Same(error, error.AsChainError);
        // Other errors never enter the conversion, and a success comes back untouched.
        await Assert.ThrowsAsync<ChainError.AccountNotOnChain>(() => TaquitoBridge.BridgeError.Typed<int>(() => throw new ChainError.AccountNotOnChain(new Address("tz1"))));
        Assert.Equal(7, await TaquitoBridge.BridgeError.Typed(() => Task.FromResult(7)));
    }
}
