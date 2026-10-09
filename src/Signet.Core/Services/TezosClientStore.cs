namespace Signet.Services;

/// <summary>
/// Reads and writes the wallet directory in octez-client's format.
/// Only the directory locations are ported so far; the store itself comes with the storage layer.
/// </summary>
public sealed partial class TezosClientStore
{
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary><c>%USERPROFILE%\.signet</c> (upstream <c>~/.signet</c>).</summary>
    public static string DefaultDirectory => Path.Combine(Home, ".signet");

    /// <summary>Where octez-client keeps its wallet: <c>%USERPROFILE%\.tezos-client</c>.</summary>
    public static string OctezClientDirectory => Path.Combine(Home, ".tezos-client");
}
