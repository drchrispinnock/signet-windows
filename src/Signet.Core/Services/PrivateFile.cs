namespace Signet.Services;

/// <summary>
/// Files that hold secrets or wallet state (S10). Upstream creates them 0600 under a 0700
/// folder with <c>O_EXCL | O_NOFOLLOW</c>; on Windows <c>AtomicFile</c> creates them with an
/// owner-only DACL that inherits nothing (audit G07) and replaces the target atomically, and the
/// read refuses links and oversized files, so a planted link cannot redirect a read elsewhere.
/// </summary>
public static class PrivateFile
{
    public const string TemporarySuffix = ".tmp";

    /// <summary>The most a secret or state file may hold unless a caller says otherwise.</summary>
    public const int MaxBytes = 32 << 20;
    /// <summary>The bound for the shielded wallet files, which carry the scanned commitments beside the keys.</summary>
    public const int LargeStateBytes = 128 << 20;

    /// <summary>
    /// The bound for a wallet-directory file taken by name: the shielded wallet files
    /// (<c>tzel-&lt;chain&gt;.json</c>) get <c>LargeStateBytes</c>, everything else <c>MaxBytes</c>. The
    /// shielded store, the backup's snapshot and its fingerprint all read through this, so no file
    /// the store writes is one the backup refuses.
    /// </summary>
    public static int ReadLimit(string name) =>
        name.StartsWith("tzel-", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal) ? LargeStateBytes : MaxBytes;

    /// <summary>Writes <c>data</c> to <c>path</c> atomically, creating the folder if needed.</summary>
    public static void Write(string path, byte[] data)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } directory) Directory.CreateDirectory(directory);
        AtomicFile.WriteAllBytes(path, data);
    }

    /// <summary>
    /// The file's bytes, null when it does not exist. A link is refused, so a planted link cannot
    /// redirect a read elsewhere, and so is anything over <c>limit</c> bytes.
    /// </summary>
    public static byte[]? Read(string path, int limit = MaxBytes)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return null;
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException($"{path} is a link; refusing to read it.");
        if (info.Length > limit) throw new IOException($"{path} is too large.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new MemoryStream();
        var buffer = new byte[32 * 1024];
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            // The file may have grown since it was measured.
            if (bytes.Length + read > limit) throw new IOException($"{path} is too large.");
            bytes.Write(buffer, 0, read);
        }
        return bytes.ToArray();
    }
}
