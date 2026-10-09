namespace Signet.Models;

/// <summary>Helpers that make <c>System.Uri</c> read like Foundation's <c>URL</c>, so ported code compares the same.</summary>
public static class UriExtensions
{
    /// <summary>
    /// Swift's <c>absoluteString</c>: the URL as written. <c>Uri.AbsoluteUri</c> adds a <c>/</c> to a bare
    /// authority (<c>https://rpc.tzbeta.net/</c>); this drops it again when there is no path, query or fragment.
    /// </summary>
    public static string AbsoluteString(this Uri url) =>
        url.AbsolutePath == "/" && url.Query.Length == 0 && url.Fragment.Length == 0 && !url.OriginalString.EndsWith('/')
            ? url.GetLeftPart(UriPartial.Authority)
            : url.AbsoluteUri;

    /// <summary>
    /// Foundation's <c>appendingPathComponent</c>: <c>https://api.tzkt.io</c> + <c>v1/tokens</c> →
    /// <c>https://api.tzkt.io/v1/tokens</c>, with exactly one <c>/</c> between, keeping any base path.
    /// </summary>
    public static Uri AppendingPathComponent(this Uri url, string path) =>
        new($"{url.GetLeftPart(UriPartial.Path).TrimEnd('/')}/{path.TrimStart('/')}");

    /// <summary>Appends a query built like <c>URLComponents.queryItems</c>: names and values percent-encoded, order kept.</summary>
    public static Uri WithQuery(this Uri url, params (string Name, string Value)[] items) =>
        new($"{url.AbsoluteString()}?{string.Join("&", items.Select(i => $"{Uri.EscapeDataString(i.Name)}={Uri.EscapeDataString(i.Value)}"))}");
}
