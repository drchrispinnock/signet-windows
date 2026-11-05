using Signet.Models;

namespace Signet.Services;

/// <summary>
/// Saves an NFT's artwork to disk: the full artifact (the video, sound or full-size image the
/// token points at), or the display image when the metadata names no artifact. Content comes
/// through the pinned public client like the tiles do, one gateway after another, and lands
/// in the <c>nfts</c> folder of the wallet directory under a name made of the token's name, its
/// contract and its id, so every token has one file and a second download replaces the first.
/// </summary>
public sealed class NFTDownloader
{
    /// <summary>The largest artwork saved: videos run to tens of megabytes, generative pieces more.</summary>
    public const int MaxBytes = 256 << 20;
    public const string FolderName = "nfts";

    public abstract class Failure(string message) : Exception(message), IEquatable<Failure>
    {
        /// <summary>The metadata names neither an artifact nor an image.</summary>
        public sealed class NoArtwork() : Failure("The token's metadata names no artwork");
        /// <summary>The content is larger than <c>MaxBytes</c> on every gateway.</summary>
        public sealed class TooLarge() : Failure($"The artwork is larger than {MaxBytes >> 20} MB");
        /// <summary>No gateway served it.</summary>
        public sealed class Unavailable() : Failure("The artwork could not be fetched");

        public bool Equals(Failure? other) => other is not null && other.GetType() == GetType();
        public override bool Equals(object? obj) => Equals(obj as Failure);
        public override int GetHashCode() => GetType().GetHashCode();
    }

    public sealed record Fetched(byte[] Data, string? ContentType);

    /// <summary>Fetches one URL; throws <c>PublicHTTPClient.TooLargeError</c> past the cap.</summary>
    public delegate Task<Fetched> Fetch(Uri url, CancellationToken cancel);

    /// <summary>Where the files go.</summary>
    public string Directory { get; }
    private readonly Fetch fetch;

    /// <summary>The downloads folder beside a wallet directory's key files. (Upstream <c>directory(under:)</c>.)</summary>
    public static string DirectoryUnder(string walletDirectory) => Path.Combine(walletDirectory, FolderName);

    public NFTDownloader(string directory, Fetch? fetch = null)
    {
        Directory = directory;
        this.fetch = fetch ?? PublicFetch;
    }

    /// <summary>Downloads <c>nft</c>'s artwork and returns the file it was written to.</summary>
    public async Task<string> Download(NFT nft, CancellationToken cancel = default)
    {
        var usesArtifact = nft.ArtifactURLs.Count > 0;
        var candidates = usesArtifact ? nft.ArtifactURLs : nft.ImageURLs;
        if (candidates.Count == 0) throw new Failure.NoArtwork();
        Fetched? fetched = null;
        Uri? from = null;
        foreach (var url in candidates)
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                fetched = await fetch(url, cancel);
                from = url;
                break;
            }
            // The same content sits behind every gateway: another one will not be smaller.
            catch (PublicHTTPClient.TooLargeError) { throw new Failure.TooLarge(); }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
            catch (Exception) { }
        }
        if (fetched is null || from is null) throw new Failure.Unavailable();
        var name = FileName(nft, fetched.ContentType, usesArtifact ? nft.ArtifactMime : null, from);
        System.IO.Directory.CreateDirectory(Directory);
        var file = Path.Combine(Directory, name);
        // Artwork is the user's to share: the folder's own permissions, not a private DACL.
        AtomicFile.WriteAllBytes(file, fetched.Data, ownerOnly: false);
        return file;
    }

    // ---- Naming ----------------------------------------------------------------------------------

    /// <summary><c>&lt;name&gt; (&lt;contract&gt; &lt;id&gt;).&lt;ext&gt;</c>: readable, unique per token, and safe as a file name.</summary>
    public static string FileName(NFT nft, string? contentType, string? declaredType, Uri? url) =>
        $"{Sanitised(nft.Name)} ({nft.Contract} {nft.TokenId}).{FileExtension(contentType, declaredType, url)}";

    /// <summary>
    /// A token's name as a file name: no path separators or control characters, no leading
    /// dot, at most 80 characters, "NFT" when nothing is left. Windows also refuses
    /// <c>&lt; &gt; " | ? *</c> in a name, so those become "-" too.
    /// </summary>
    public static string Sanitised(string name)
    {
        var cleaned = new string(name.Select(c => c switch
        {
            '/' or ':' or '\\' => '-',
            '<' or '>' or '"' or '|' or '?' or '*' when OperatingSystem.IsWindows() => '-',
            _ when char.IsControl(c) => ' ',
            _ => c,
        }).ToArray());
        if (cleaned.Length > 80) cleaned = cleaned[..80];
        var result = cleaned.Trim().TrimStart('.');
        // Windows drops a trailing dot or space from a file name.
        if (OperatingSystem.IsWindows()) result = result.TrimEnd('.', ' ');
        return result.Length == 0 ? "NFT" : result;
    }

    /// <summary>
    /// The extension the content deserves: from the gateway's content type when it is a real
    /// one, else the type the metadata declares, else the URL's own extension, else <c>bin</c>.
    /// </summary>
    public static string FileExtension(string? contentType, string? declaredType, Uri? url)
    {
        foreach (var type in new[] { contentType, declaredType })
            if (Extensions.TryGetValue(Mime(type), out var ext)) return ext;
        var fromURL = url is null ? "" : Path.GetExtension(url.AbsolutePath).TrimStart('.').ToLowerInvariant();
        if (fromURL.Length is > 0 and <= 5 && fromURL.All(char.IsLetterOrDigit)) return fromURL;
        return "bin";
    }

    /// <summary><c>image/png; charset=binary</c> → <c>image/png</c>.</summary>
    private static string Mime(string? type) => (type ?? "").ToLowerInvariant().Split(';')[0].Trim();

    /// <summary>
    /// What NFTs on Tezos are made of; <c>application/octet-stream</c> is deliberately absent so a
    /// gateway that does not know the type falls through to the metadata and the URL.
    /// </summary>
    private static readonly Dictionary<string, string> Extensions = new()
    {
        ["image/png"] = "png", ["image/jpeg"] = "jpg", ["image/jpg"] = "jpg", ["image/gif"] = "gif", ["image/webp"] = "webp",
        ["image/svg+xml"] = "svg", ["image/avif"] = "avif", ["image/bmp"] = "bmp", ["image/tiff"] = "tiff", ["image/heic"] = "heic",
        ["video/mp4"] = "mp4", ["video/quicktime"] = "mov", ["video/webm"] = "webm", ["video/x-matroska"] = "mkv", ["video/ogg"] = "ogv",
        ["audio/mpeg"] = "mp3", ["audio/mp3"] = "mp3", ["audio/wav"] = "wav", ["audio/x-wav"] = "wav", ["audio/ogg"] = "ogg",
        ["audio/flac"] = "flac", ["audio/aac"] = "aac", ["audio/mp4"] = "m4a",
        ["model/gltf-binary"] = "glb", ["model/gltf+json"] = "gltf", ["application/pdf"] = "pdf", ["text/html"] = "html",
        ["application/x-directory"] = "zip", ["application/zip"] = "zip", ["application/json"] = "json", ["text/plain"] = "txt",
    };

    // ---- Network ---------------------------------------------------------------------------------

    /// <summary>The pinned public client, as the tiles use it, with the download's own size cap.</summary>
    private static async Task<Fetched> PublicFetch(Uri url, CancellationToken cancel)
    {
        var reply = await PublicHTTPClient.Data(url, MaxBytes, followRedirects: true, cancel);
        if (reply.Status is < 200 or >= 300) throw new HttpRequestException($"HTTP {reply.Status}");
        return new Fetched(reply.Data, reply.Headers.GetValueOrDefault("content-type"));
    }
}
