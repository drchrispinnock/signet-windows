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
