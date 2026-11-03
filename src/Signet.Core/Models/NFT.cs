namespace Signet.Models;

/// <summary>A non-fungible token owned by the selected wallet. Only what the thumbnail grid needs.</summary>
/// <param name="Id"><c>&lt;contract&gt;:&lt;tokenId&gt;</c> (prefixed <c>l2:</c> on Etherlink)</param>
/// <param name="Balance">How many editions of this token the wallet holds (usually 1).</param>
/// <param name="ImageURLs">HTTPS URLs to try for the tile image, best first (one per IPFS gateway).</param>
public sealed record NFT(string Id, string Contract, string TokenId, string Name, decimal Balance, IReadOnlyList<Uri> ImageURLs)
{
    public NFT(string id, string contract, string tokenId, string name, decimal balance, Uri? thumbnailURL)
        : this(id, contract, tokenId, name, balance, thumbnailURL is null ? [] : [thumbnailURL]) { }

    public Uri? ThumbnailURL => ImageURLs.Count > 0 ? ImageURLs[0] : null;

    /// <summary>When the holding last changed, as the indexer reports it; the grid is newest first.</summary>
    public DateTimeOffset? Date { get; init; }
    /// <summary>The artifact's MIME type, when the metadata gives one (<c>video/mp4</c>, <c>image/png</c>, …).</summary>
    public string? ArtifactMime { get; init; }
    /// <summary>
    /// The artifact is a video or a sound, so the tile shows a still, often a marketplace's
    /// placeholder, and wears a play badge to say so.
    /// </summary>
    public bool IsMoving => ArtifactMime is { } mime && (mime.StartsWith("video/", StringComparison.Ordinal) || mime.StartsWith("audio/", StringComparison.Ordinal));

    /// <summary>HTTPS URLs for the full artifact (one per IPFS gateway), for "View artwork"; empty when unknown.</summary>
    public IReadOnlyList<Uri> ArtifactURLs { get; init; } = [];
    /// <summary>
    /// TZIP-21 <c>isTransferable</c> (true unless the metadata says otherwise): a soulbound badge or
    /// a proof of attendance cannot be transferred or burnt, so the menu offers neither.
    /// </summary>
    public bool IsTransferable { get; init; } = true;

    /// <summary>Where the token lives: 1 on Tezos, 2 on Etherlink's Michelson interface.</summary>
    public int Layer { get; init; } = 1;
    public bool IsLayer2 => Layer == 2;

    /// <summary>The same token tagged as Etherlink's, its id prefixed to stay distinct.</summary>
    public NFT OnLayer2() => this with { Layer = 2, Id = "l2:" + Id };

    // The image list is part of the value: an unchanged refresh must compare equal (no redraw).
    public bool Equals(NFT? other) =>
        other is not null && Id == other.Id && Layer == other.Layer && Contract == other.Contract && TokenId == other.TokenId && Name == other.Name &&
        Balance == other.Balance && Date == other.Date && ArtifactMime == other.ArtifactMime && IsTransferable == other.IsTransferable &&
        ImageURLs.SequenceEqual(other.ImageURLs) && ArtifactURLs.SequenceEqual(other.ArtifactURLs);
    public override int GetHashCode() => HashCode.Combine(Id, Name, Balance);
}

/// <summary>Tokens whose artwork is being saved to disk, shown by the Download sheet.</summary>
public sealed record NFTDownload(IReadOnlyList<NFT> Nfts)
{
    public string Id => string.Join(",", Nfts.Select(n => n.Id));

    public bool Equals(NFTDownload? other) => other is not null && Nfts.SequenceEqual(other.Nfts);
    public override int GetHashCode() => Nfts.Count;
}
