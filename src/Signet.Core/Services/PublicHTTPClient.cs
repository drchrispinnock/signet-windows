using System.Net;
using System.Net.Sockets;

namespace Signet.Services;

/// <summary>
/// Untrusted URLs (a dApp's relay and RPC traffic) only reach public addresses (S12, S13). Upstream
/// runs the system curl with a pinned, validated DNS answer; here <see cref="SocketsHttpHandler.ConnectCallback"/>
/// does the same job inside .NET: it resolves the host itself, refuses the connection if any answer
/// is on this machine or a private network (DNS rebinding, split horizon), and connects to the
/// checked address. TLS still validates the certificate against the original hostname (SNI is
/// unchanged). Proxies and automatic redirects are disabled; replies are capped by the caller.
/// </summary>
public static class PublicHTTPClient
{
    public sealed class PublicEndpointError(string message) : HttpRequestException(message);
    /// <summary>
    /// The content passes the caller's byte cap, declared or while it streams (upstream:
    /// <c>URLError(.dataLengthExceedsMaximum)</c>, curl's exit 63). It is too large wherever it is
    /// fetched from, which callers may treat differently from a server that could not be reached.
    /// </summary>
    public sealed class TooLargeError(int limit) : HttpRequestException($"The content is larger than {limit} bytes.");

    /// <summary>A reply read in full: body, status and headers (lower-case names).</summary>
    public sealed record Reply(byte[] Data, int Status, IReadOnlyDictionary<string, string> Headers);

    /// <summary>At most this many untrusted requests run at once (upstream's six-slot gate).</summary>
    private static readonly SemaphoreSlim Gate = new(6);

    /// <summary>
    /// GETs <c>url</c> through the pinned public client, reading at most <c>limit</c> bytes. With
    /// <c>followRedirects</c>, up to five redirects are followed, each to a public HTTPS address
    /// checked again (artwork requests carry no credentials, cookies or bodies); otherwise a
    /// redirect is refused. 15 s to connect and 60 s overall, as upstream's curl.
    /// </summary>
    public static async Task<Reply> Data(Uri url, int limit, bool followRedirects = false, CancellationToken cancel = default)
    {
        await Gate.WaitAsync(cancel);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            var current = url;
            for (var hop = 0; hop <= 5; hop++)
            {
                Check(current);
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                using var response = await Shared.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                var status = (int)response.StatusCode;
                if (status is >= 300 and < 400)
                {
                    if (!followRedirects || hop == 5 || response.Headers.Location is not { } location) throw new HttpRequestException("Refused a redirect.");
                    var target = location.IsAbsoluteUri ? location : new Uri(current, location);
                    if (!EndpointPolicy.IsPublicHTTPS(target)) throw new PublicEndpointError($"Refused a redirect to {target.Host}: only public HTTPS endpoints are allowed.");
                    current = target;
                    continue;
                }
                if (response.Content.Headers.ContentLength is { } declared && declared > limit) throw new TooLargeError(limit);
                var headers = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var header in response.Headers.Concat(response.Content.Headers))
                    headers[header.Key.ToLowerInvariant()] = string.Join(", ", header.Value);
                await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
                using var body = new MemoryStream();
                var buffer = new byte[32 * 1024];
                int read;
                while ((read = await stream.ReadAsync(buffer, deadline.Token)) > 0)
                {
                    if (body.Length + read > limit) throw new TooLargeError(limit);
                    body.Write(buffer, 0, read);
                }
                return new Reply(body.ToArray(), status, headers);
            }
            throw new HttpRequestException("Too many redirects.");
        }
        finally { Gate.Release(); }
    }

    /// <summary>The shared client for untrusted destinations; requests set their own timeouts.</summary>
    public static HttpClient Shared { get; } = new(Handler()) { Timeout = Timeout.InfiniteTimeSpan };

    private static SocketsHttpHandler Handler() => new()
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        ConnectCallback = Connect,
    };

    /// <summary>Whether a request may be sent at all: public HTTPS, no credentials (checked again on connect).</summary>
    public static void Check(Uri url)
    {
        if (EndpointPolicy.Check(url) is not EndpointPolicy.Verdict.Allowed)
            throw new PublicEndpointError($"Refused a connection to {url.Host}: only public HTTPS endpoints are allowed.");
    }

    /// <summary>The public addresses <c>host</c> resolves to, or nothing when any answer is local or private.</summary>
    public static async Task<IReadOnlyList<IPAddress>> PublicAddresses(string host, CancellationToken cancel)
    {
        if (EndpointPolicy.IsLocalOrPrivateHost(host)) return [];
        IPAddress[] answers;
        try { answers = await Dns.GetHostAddressesAsync(host, cancel); }
        catch (SocketException) { return []; }
        // One private answer spoils the lot: a name that points at the LAN is refused outright.
        if (answers.Length == 0 || answers.Any(a => EndpointPolicy.IsLocalOrPrivateHost(Text(a)))) return [];
        return answers;
    }

    private static string Text(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4().ToString() : address.ToString();

    private static async ValueTask<Stream> Connect(SocketsHttpConnectionContext context, CancellationToken cancel)
    {
        var endpoint = context.DnsEndPoint;
        var addresses = await PublicAddresses(endpoint.Host, cancel);
        if (addresses.Count == 0) throw new PublicEndpointError($"Refused a connection to {endpoint.Host}: it is not a public address.");
        Exception? last = null;
        foreach (var address in addresses.Take(8))
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), cancel);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                last = e;
                if (e is OperationCanceledException) throw;
            }
        }
        throw new HttpRequestException($"Could not connect to {endpoint.Host}.", last);
    }
}
