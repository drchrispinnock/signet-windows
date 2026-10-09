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
}
