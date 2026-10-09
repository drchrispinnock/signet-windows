namespace Signet.Services;

/// <summary>
/// Where the wallet directory is. This pointer cannot live inside the directory it points to,
/// so it is the one thing kept in the app's preferences rather than in <c>.signet\state</c>.
/// </summary>
public sealed class WalletDirectorySettings(UserDefaults? defaults = null)
{
    public const string Key = "walletDirectory";

    public UserDefaults Defaults { get; } = defaults ?? UserDefaults.Standard;

    public string DefaultDirectory => TezosClientStore.DefaultDirectory;

    public string Current =>
        Defaults.String(Key) is { Length: > 0 } path ? PathUtility.ExpandTilde(path) : DefaultDirectory;

    /// <summary>Stores <c>path</c>, or clears the override when it is the default directory.</summary>
    public void Set(string path)
    {
        if (PathUtility.SamePath(path, DefaultDirectory)) Defaults.RemoveObject(Key);
        else Defaults.Set(Key, PathUtility.Standardized(path));
    }
}
