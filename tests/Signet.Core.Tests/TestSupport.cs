namespace Signet.Tests;

internal static class TestSupport
{
    /// <summary>A fresh path under the temp folder (not created), like upstream's <c>temporaryDirectory/signet-…-UUID</c>.</summary>
    public static string TempDir(string label) =>
        Path.Combine(Path.GetTempPath(), $"signet-{label}-{Guid.NewGuid():N}");

    /// <summary>A fresh, existing directory.</summary>
    public static string MakeDir(string label) => Directory.CreateDirectory(TempDir(label)).FullName;

    /// <summary>Writes fixture text the way <c>String.write(to:atomically:encoding: .utf8)</c> does: UTF-8, no BOM.</summary>
    public static void WriteText(string directory, string file, string text) =>
        File.WriteAllText(Path.Combine(directory, file), text, new System.Text.UTF8Encoding(false));

    // The Mac tests also assert chmod 600 on wallet files. Windows has no POSIX modes; files under the
    // user profile inherit an ACL limited to the user, so those assertions are dropped in the ports.
}
