using Signet.Models;

namespace Signet.Services;

/// <summary>
/// Identity data for an address. TzProfiles are mainnet identities, so pictures and names always
/// come from mainnet services no matter which network balances are being read from.
/// </summary>
public static class Profiles
{
    /// <summary>TzKT's avatar service: TzProfiles logo, known-account logo, or an identicon.</summary>
    public static readonly Uri AvatarService = new("https://services.tzkt.io/v1/avatars/");

    public static Uri? AvatarURL(Address address) =>
        Uri.TryCreate(AvatarService, address.Value, out var url) ? url : null;

    /// <summary>The indexer that carries TzProfiles data (<c>extras.profile</c>) for name lookups.</summary>
    public static Uri ProfileIndexer => Network.Mainnet.TzktURL!;
}
