using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Signet.Services;

namespace Signet.Tests;

/// <summary>
/// Windows-only: the 5 November 2026 audit's G07 (private ACLs), which upstream has no suite
/// for. The rest of the audit is upstream's <c>AuditFixTests</c>.
/// </summary>
public class StorageAuditRepairTests
{
    private static string TempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "signet-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// G07: a wallet file gets its own protected DACL (the user and SYSTEM), whatever the folder
    /// would hand down, and keeps it when it replaces an older file.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void WalletFilesGetAnOwnerOnlyDACL()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = TempDirectory();
        try
        {
            // A folder anyone may read, as a share might be.
            var folder = new DirectoryInfo(directory);
            var open = folder.GetAccessControl();
            open.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            folder.SetAccessControl(open);

            var path = Path.Combine(directory, "secret_keys");
            File.WriteAllText(path, "[]");
            Assert.Contains(Rules(path), r => r.IdentityReference.Value == new SecurityIdentifier(WellKnownSidType.WorldSid, null).Value);
            AtomicFile.WriteAllText(path, "[]");
            var security = new FileInfo(path).GetAccessControl();
            Assert.True(security.AreAccessRulesProtected);
            using var me = WindowsIdentity.GetCurrent();
            var allowed = Rules(path).Select(r => r.IdentityReference.Value).ToHashSet();
            Assert.Equal(new HashSet<string> { me.User!.Value, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value }, allowed);

            // Artwork keeps the folder's permissions.
            var art = Path.Combine(directory, "art.png");
            AtomicFile.WriteAllBytes(art, [1, 2, 3], ownerOnly: false);
            Assert.False(new FileInfo(art).GetAccessControl().AreAccessRulesProtected);
        }
        finally { try { Directory.Delete(directory, recursive: true); } catch (IOException) { } }

        [SupportedOSPlatform("windows")]
        static IEnumerable<FileSystemAccessRule> Rules(string path) =>
            new FileInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>();
    }
}
