using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Signet.Services;

/// <summary>Backup preferences. Like the wallet directory pointer these live in the app's preferences.</summary>
public sealed class BackupSettings(UserDefaults? defaults = null)
{
    public const string DirectoryKey = "backupDirectory";
    public const string GenerationsKey = "backupGenerations";
    public const int DefaultGenerations = 10;
    public const int MinGenerations = 1, MaxGenerations = 100;

    public UserDefaults Defaults { get; } = defaults ?? UserDefaults.Standard;

    public string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".signet_backups");

    public string Directory =>
        Defaults.String(DirectoryKey) is { Length: > 0 } path ? PathUtility.ExpandTilde(path) : DefaultDirectory;

    public int Generations
    {
        get
        {
            var value = Defaults.Integer(GenerationsKey);
            return value is >= MinGenerations and <= MaxGenerations ? value : DefaultGenerations;
        }
    }

    public void SetDirectory(string path)
    {
        if (PathUtility.SamePath(path, DefaultDirectory)) Defaults.RemoveObject(DirectoryKey);
        else Defaults.Set(DirectoryKey, PathUtility.Standardized(path));
    }

    public void SetGenerations(int count)
    {
        var clamped = Math.Clamp(count, MinGenerations, MaxGenerations);
        if (clamped == DefaultGenerations) Defaults.RemoveObject(GenerationsKey);
        else Defaults.Set(GenerationsKey, clamped);
    }
}

/// <summary>Copies the wallet files into a timestamped generation folder and prunes old generations.</summary>
public sealed record WalletBackup(string WalletDirectory, string BackupDirectory, int Generations)
{
    public abstract record Outcome
    {
        /// <summary>A new generation was written here.</summary>
        public sealed record Created(string Generation) : Outcome;
        /// <summary>The latest generation already matches the wallet files byte for byte.</summary>
        public sealed record Unchanged(string Latest) : Outcome;
        /// <summary>The wallet directory has no wallet files yet.</summary>
        public sealed record NothingToBackUp : Outcome;
    }

    /// <summary>Generation folders are named so lexical order is chronological order.</summary>
    public static string Stamp(DateTimeOffset date) =>
        date.UtcDateTime.ToString("yyyy-MM-dd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// Files worth saving: the three octez-client wallet files, the contract aliases, Signet's state,
    /// the multisig proposals and the operation journal. This list is HEAD's (without the shielded
    /// files); the rest of this class is still the <c>3b33fe1</c> version.
    /// </summary>
    public static readonly IReadOnlyList<string> Files =
        [.. TezosClientStore.WalletFiles, TezosClientStore.ContractsFile, "state", FileMultisigProposalStore.FileName, OperationJournal.FileName];

    /// <param name="force">Write a generation even if the latest one already matches (explicit user action).</param>
    public Outcome Run(DateTimeOffset? now = null, bool force = false)
    {
        var date = now ?? DateTimeOffset.UtcNow;
        var present = Files.Where(f => File.Exists(Path.Combine(WalletDirectory, f))).ToList();
        if (!present.Contains(TezosClientStore.PublicKeyHashesFile)) return new Outcome.NothingToBackUp();

        System.IO.Directory.CreateDirectory(BackupDirectory);

        var existing = GenerationsIn(BackupDirectory);
        if (!force && existing.Count > 0 && Fingerprint(WalletDirectory).SequenceEqual(Fingerprint(existing[^1])))
        {
            Prune(existing);
            return new Outcome.Unchanged(existing[^1]);
        }

        var stamp = Stamp(date);
        var target = Path.Combine(BackupDirectory, stamp);
        for (var suffix = 2; System.IO.Directory.Exists(target); suffix++)
        {
            stamp = $"{Stamp(date)}-{suffix}";
            target = Path.Combine(BackupDirectory, stamp);
        }

        // Build in a temporary folder and rename into place so a half-written generation never counts.
        var staging = Path.Combine(BackupDirectory, $".{stamp}.partial");
        if (System.IO.Directory.Exists(staging)) System.IO.Directory.Delete(staging, recursive: true);
        System.IO.Directory.CreateDirectory(staging);
        foreach (var file in present)
            File.Copy(Path.Combine(WalletDirectory, file), Path.Combine(staging, file));
        System.IO.Directory.Move(staging, target);

        var created = Normalized(target);
        Prune([.. existing, created]);
        return new Outcome.Created(created);
    }

    /// <summary>Generation folders, oldest first. Hidden (<c>.</c>-prefixed) folders such as a half-built one are skipped.</summary>
    public static IReadOnlyList<string> GenerationsIn(string directory)
    {
        if (!System.IO.Directory.Exists(directory)) return [];
        return System.IO.Directory.GetDirectories(directory)
            .Where(d => !Path.GetFileName(d).StartsWith('.'))
            .Select(Normalized)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>A canonical spelling so generation paths compare equal.</summary>
    public static string Normalized(string path) => PathUtility.Standardized(path);

    private void Prune(IReadOnlyList<string> generations)
    {
        var sorted = generations.OrderBy(Path.GetFileName, StringComparer.Ordinal).ToList();
        if (sorted.Count <= Generations) return;
        foreach (var old in sorted.Take(sorted.Count - Generations))
            System.IO.Directory.Delete(old, recursive: true);
    }

    /// <summary>SHA-256 over each saved file's name and contents, so identical wallets compare equal.</summary>
    public static byte[] Fingerprint(string directory)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Files)
        {
            var path = Path.Combine(directory, file);
            if (!File.Exists(path)) continue;
            hasher.AppendData(Encoding.UTF8.GetBytes(file));
            hasher.AppendData(File.ReadAllBytes(path));
        }
        return hasher.GetHashAndReset();
    }
}
