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
