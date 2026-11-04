using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Signet.Models;

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

/// <summary>
/// Copies the wallet files into a timestamped generation folder and prunes old generations.
///
/// Only generations Signet made are ever deleted: each carries a <c>.signet-backup</c> manifest, and
/// pruning skips anything else in the folder (another wallet's backups, a user's documents, a
/// link). Generations without a valid ownership manifest are preserved.
/// </summary>
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

    public abstract class BackupError(string message) : Exception(message)
    {
        /// <summary>The backup folder is the wallet directory, inside it, or contains it.</summary>
        public sealed class OverlapsWallet(string backup, string wallet)
            : BackupError($"The backup folder {backup} overlaps the wallet directory {wallet}. Choose a separate folder for backups.");
    }

    /// <summary>What a generation folder is named: the stamp, optionally <c>-2</c>, <c>-3</c>… when one second holds several.</summary>
    public static readonly System.Text.RegularExpressions.Regex StampPattern = new(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{6}Z(-[0-9]+)?$");

    /// <summary>Written into every generation; its presence is what marks the folder as ours to prune.</summary>
    public const string ManifestFile = ".signet-backup";

    public sealed record Manifest(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("created")] DateTimeOffset Created,
        /// <summary>The wallet directory the generation was taken from.</summary>
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("files")] IReadOnlyList<string> Files)
    {
        public const int CurrentVersion = 1;
    }

    /// <summary>Generation folders are named so lexical order is chronological order.</summary>
    public static string Stamp(DateTimeOffset date) =>
        date.UtcDateTime.ToString("yyyy-MM-dd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// Files worth saving: the three octez-client wallet files, the contract aliases, Signet's state,
    /// the multisig proposals, the operation journal and the shielded wallets.
    /// </summary>
    public static readonly IReadOnlyList<string> Files =
        [.. TezosClientStore.WalletFiles, TezosClientStore.ContractsFile, "state", FileMultisigProposalStore.FileName, OperationJournal.FileName, .. ShieldedFiles];

    /// <summary>Any of these present means there is something to back up (<c>state</c> alone is not worth a generation).</summary>
    public static readonly IReadOnlyList<string> ValuableFiles =
        [.. TezosClientStore.WalletFiles, TezosClientStore.ContractsFile, FileMultisigProposalStore.FileName, OperationJournal.FileName, .. ShieldedFiles];

    /// <summary>
    /// The shielded (TzEL) wallet file of every network (master keys, notes, the operator
    /// token, the address book). The 2 MB signing trees beside it are left out; they are
    /// rebuilt from the master key.
    /// </summary>
    public static IReadOnlyList<string> ShieldedFiles => Network.All.Select(n => FileShieldedWalletStore.WalletFile(n.Chain)).ToList();

    /// <param name="force">Write a generation even if the latest one already matches (explicit user action).</param>
    public Outcome Run(DateTimeOffset? now = null, bool force = false)
    {
        var date = now ?? DateTimeOffset.UtcNow;
        // The files are read under the wallet lock, with an unfinished change rolled back first, so
        // a change in progress (ours or octez-client's) is never copied half done (S09).
        var snapshot = System.IO.Directory.Exists(WalletDirectory) ? TezosClientStore.SnapshotFiles(WalletDirectory, Files) : [];
        if (!snapshot.Any(s => ValuableFiles.Contains(s.File))) return new Outcome.NothingToBackUp();
        CheckSeparate(BackupDirectory, WalletDirectory);

        System.IO.Directory.CreateDirectory(BackupDirectory);

        var existing = GenerationsIn(BackupDirectory, WalletDirectory);
        if (!force && existing.Count > 0 && Fingerprint(snapshot).SequenceEqual(Fingerprint(existing[^1])))
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

        // Build in a folder of our own (a fresh name, so two jobs never share one) and rename it
        // into place, so a half-written generation never counts; a failed job removes it.
        var staging = Path.Combine(BackupDirectory, $".{Guid.NewGuid().ToString().ToUpperInvariant()}.partial");
        System.IO.Directory.CreateDirectory(staging);
        var moved = false;
        try
        {
            foreach (var (file, data) in snapshot) PrivateFile.Write(Path.Combine(staging, file), data);
            var manifest = new Manifest(Manifest.CurrentVersion, date, Normalized(WalletDirectory), snapshot.Select(s => s.File).ToList());
            PrivateFile.Write(Path.Combine(staging, ManifestFile), EncodeManifest(manifest));
            System.IO.Directory.Move(staging, target);
            moved = true;
        }
        finally
        {
            if (!moved) try { System.IO.Directory.Delete(staging, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        var created = Normalized(target);
        Prune([.. existing, created]);
        return new Outcome.Created(created);
    }

    /// <summary>
    /// The backup folder must be neither the wallet directory, nor inside it, nor around it:
    /// a generation inside the wallet would be backed up into itself, and pruning around the
    /// wallet could never be made safe.
    /// </summary>
    public static void CheckSeparate(string backup, string wallet)
    {
        var b = Normalized(backup).TrimEnd(Path.DirectorySeparatorChar);
        var w = Normalized(wallet).TrimEnd(Path.DirectorySeparatorChar);
        var sep = Path.DirectorySeparatorChar.ToString();
        if (b.Equals(w, StringComparison.OrdinalIgnoreCase)
            || b.StartsWith(w + sep, StringComparison.OrdinalIgnoreCase)
            || w.StartsWith(b + sep, StringComparison.OrdinalIgnoreCase))
            throw new BackupError.OverlapsWallet(backup, wallet);
    }

    /// <summary>Generation folders Signet owns (of <c>source</c>'s wallet when given), oldest first. Anything else in the folder is left alone.</summary>
    public static IReadOnlyList<string> GenerationsIn(string directory, string? source = null)
    {
        if (!System.IO.Directory.Exists(directory)) return [];
        return new DirectoryInfo(directory).GetDirectories()
            .Where(d => !d.Name.StartsWith('.') && !d.Attributes.HasFlag(FileAttributes.ReparsePoint) && IsGeneration(d.FullName, source))
            .Select(d => Normalized(d.FullName))
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Recognises only complete, supported manifests. Legacy folders have no provable owner.</summary>
    public static bool IsGeneration(string path, string? source = null)
    {
        if (!StampPattern.IsMatch(Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)))) return false;
        if (ManifestOf(path) is not { } manifest || manifest.Version != Manifest.CurrentVersion) return false;
        if (!Path.IsPathFullyQualified(manifest.Source) || manifest.Files.Count == 0 || manifest.Files.Distinct().Count() != manifest.Files.Count) return false;
        if (!manifest.Files.All(Files.Contains) || !manifest.Files.Any(ValuableFiles.Contains)) return false;
        if (source is not null && !PathUtility.SamePath(manifest.Source, Normalized(source))) return false;
        FileSystemInfo[] contents;
        try { contents = new DirectoryInfo(path).GetFileSystemInfos(); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        if (!contents.Select(c => c.Name).ToHashSet().SetEquals(manifest.Files.Append(ManifestFile))) return false;
        return contents.All(c => c is FileInfo && !c.Attributes.HasFlag(FileAttributes.ReparsePoint));
    }

    public static Manifest? ManifestOf(string generation)
    {
        try { return JsonSerializer.Deserialize<Manifest>(File.ReadAllBytes(Path.Combine(generation, ManifestFile))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }

    /// <summary>The manifest as the Mac writes it: pretty-printed, keys sorted, ISO 8601 date, LF line ends.</summary>
    public static byte[] EncodeManifest(Manifest manifest)
    {
        var node = new JsonObject
        {
            ["created"] = manifest.Created.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["files"] = new JsonArray(manifest.Files.Select(f => (JsonNode?)f).ToArray()),
            ["source"] = manifest.Source,
            ["version"] = manifest.Version,
        };
        var text = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        return Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n"));
    }

    /// <summary>A canonical spelling so generation paths compare equal.</summary>
    public static string Normalized(string path) => PathUtility.Standardized(path);

    private void Prune(IReadOnlyList<string> generations)
    {
        var sorted = generations.OrderBy(Path.GetFileName, StringComparer.Ordinal).ToList();
        if (sorted.Count <= Generations) return;
        var root = Normalized(BackupDirectory).TrimEnd(Path.DirectorySeparatorChar);
        foreach (var old in sorted.Take(sorted.Count - Generations))
        {
            // Re-check right before deleting: still directly under our folder, still a generation of ours.
            var info = new DirectoryInfo(old);
            if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            if (info.Parent is not { } parent || !PathUtility.SamePath(parent.FullName, root)) continue;
            if (!IsGeneration(old, WalletDirectory)) continue;
            System.IO.Directory.Delete(old, recursive: true);
        }
    }

    /// <summary>SHA-256 over each saved file's name and contents, so identical wallets compare equal.</summary>
    public static byte[] Fingerprint(string directory) =>
        Fingerprint(Files.Select(f => (f, PrivateFile.Read(Path.Combine(directory, f)))).Where(p => p.Item2 is not null).Select(p => (p.f, p.Item2!)).ToList());

    public static byte[] Fingerprint(IReadOnlyList<(string File, byte[] Data)> snapshot)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Files)
        {
            if (snapshot.FirstOrDefault(s => s.File == file).Data is not { } data) continue;
            hasher.AppendData(Encoding.UTF8.GetBytes(file));
            hasher.AppendData(data);
        }
        return hasher.GetHashAndReset();
    }
}
