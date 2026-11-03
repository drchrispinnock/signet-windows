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

    /// <summary>Where the token lives: 1 on Tezos, 2 on Etherlink's Michelson interface.</summary>
    public int Layer { get; init; } = 1;
    public bool IsLayer2 => Layer == 2;

    /// <summary>The same token tagged as Etherlink's, its id prefixed to stay distinct.</summary>
    public NFT OnLayer2() => this with { Layer = 2, Id = "l2:" + Id };

    // The image list is part of the value: an unchanged refresh must compare equal (no redraw).
    public bool Equals(NFT? other) =>
        other is not null && Id == other.Id && Layer == other.Layer && Contract == other.Contract && TokenId == other.TokenId && Name == other.Name &&
        Balance == other.Balance && ImageURLs.SequenceEqual(other.ImageURLs);
    public override int GetHashCode() => HashCode.Combine(Id, Name, Balance);
}
