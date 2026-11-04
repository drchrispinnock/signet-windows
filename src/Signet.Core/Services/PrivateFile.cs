namespace Signet.Services;

/// <summary>
/// Files that hold secrets or wallet state (S10). Upstream creates them 0600 under a 0700
/// folder with <c>O_EXCL | O_NOFOLLOW</c>; on Windows the files inherit the user profile's
/// private ACL (the wallet and cache folders live under <c>%USERPROFILE%</c> and
/// <c>%LOCALAPPDATA%</c>), so what remains is the atomic replace (<c>AtomicFile</c>) and a read
/// that refuses links and oversized files, so a planted link cannot redirect a read elsewhere.
/// </summary>
public static class PrivateFile
{
    public const string TemporarySuffix = ".tmp";
    private const long MaxBytes = 32 << 20;

    /// <summary>Writes <c>data</c> to <c>path</c> atomically, creating the folder if needed.</summary>
    public static void Write(string path, byte[] data)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } directory) Directory.CreateDirectory(directory);
        AtomicFile.WriteAllBytes(path, data);
    }

    /// <summary>The file's bytes, null when it does not exist; a link or a file over 32 MB is refused.</summary>
    public static byte[]? Read(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return null;
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException($"{path} is a link; refusing to read it.");
        if (info.Length > MaxBytes) throw new IOException($"{path} is too large.");
        return File.ReadAllBytes(path);
    }
}
