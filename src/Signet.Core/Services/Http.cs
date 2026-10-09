using System.Net.Http.Headers;
using System.Text;

namespace Signet.Services;

/// <summary>
/// The shared HTTP client (upstream's <c>URLSession.shared</c>) and a send helper with a
/// per-request timeout, as <c>URLRequest.timeoutInterval</c> gives. Services take an
/// <see cref="HttpClient"/> so tests can substitute one.
/// </summary>
public static class Http
{
    /// <summary>No client-wide timeout: each request sets its own (URLSession's default is 60 s).</summary>
    public static HttpClient Shared { get; } = new() { Timeout = Timeout.InfiniteTimeSpan };

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    /// <summary>A received response: status and body. Anything below HTTP (DNS, TLS, timeout) throws.</summary>
    public sealed record Reply(int Status, byte[] Body)
    {
        public bool IsSuccess => Status is >= 200 and < 300;
        public string Text => Encoding.UTF8.GetString(Body);
    }

    public static Task<Reply> Get(HttpClient client, Uri url, TimeSpan? timeout = null) =>
        Send(client, new HttpRequestMessage(HttpMethod.Get, url), timeout);

    public static Task<Reply> PostJSON(HttpClient client, Uri url, string json, TimeSpan? timeout = null) =>
        Send(client, new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") }, timeout);

    public static async Task<Reply> Send(HttpClient client, HttpRequestMessage request, TimeSpan? timeout = null)
    {
        using var _ = request;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        // reloadIgnoringLocalCacheData
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var cancel = new CancellationTokenSource(timeout ?? DefaultTimeout);
        try
        {
            using var response = await client.SendAsync(request, cancel.Token);
            return new Reply((int)response.StatusCode, await response.Content.ReadAsByteArrayAsync(cancel.Token));
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            throw new TimeoutException($"The request to {request.RequestUri?.Host} timed out.");
        }
    }
}
