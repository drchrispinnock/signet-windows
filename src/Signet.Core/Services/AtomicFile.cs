using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace Signet.Services;

/// <summary>
/// Foundation's <c>.atomic</c> write: write a sibling temp file, then replace the target in one step,
/// so a crash never leaves a half-written wallet file. Text is UTF-8 without a BOM, as octez-client
/// writes it.
///
/// Windows difference (upstream S10's 0600, audit G07): the temp file is created with its own
/// protected DACL, full control for the current user and SYSTEM and nothing inherited, before a byte
/// is written, and the rename keeps that DACL. So a wallet directory the user points at a share or a
/// folder other accounts can read still gets private files.
/// </summary>
public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static void WriteAllText(string path, string contents) => WriteAllBytes(path, Utf8NoBom.GetBytes(contents));

    /// <param name="ownerOnly">False keeps the folder's inherited permissions (downloaded artwork, which is not private).</param>
    public static void WriteAllBytes(string path, byte[] contents, bool ownerOnly = true)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = Create(temp, ownerOnly))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static FileStream Create(string path, bool ownerOnly) =>
        ownerOnly && OperatingSystem.IsWindows()
            ? new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, OwnerOnlySecurity())
            : new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);

    /// <summary>A DACL that inherits nothing: the current user and SYSTEM, full control.</summary>
    [SupportedOSPlatform("windows")]
    public static FileSecurity OwnerOnlySecurity()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        using var identity = WindowsIdentity.GetCurrent();
        security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }
}
