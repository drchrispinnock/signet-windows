namespace Signet.ViewModels;

/// <summary>
/// Upstream's <c>ShieldSettingsSheet.plainHTTP</c>, kept in Core so the headless tests reach it
/// (as with <c>BuyWebViewPolicy</c>); the app's <c>ShieldSettingsSheet</c> shows it.
/// </summary>
public static class ShieldSettingsNotes
{
    /// <summary>
    /// What to say when the operator or the rollup node is reached over plain HTTP: the
    /// access token and every request travel unencrypted, and a network in between can
    /// change the answers. The shipped Shadownet deployment offers nothing else (November 2026).
    /// </summary>
    public static string? PlainHTTP(string operatorURL, string rollupNode)
    {
        var plain = new[] { ("operator", operatorURL), ("rollup node", rollupNode) }
            .Where(s => s.Item2.StartsWith("http://", StringComparison.OrdinalIgnoreCase)).Select(s => s.Item1).ToList();
        if (plain.Count == 0) return null;
        return $"The {string.Join(" and ", plain)} {(plain.Count == 1 ? "is" : "are")} reached over plain HTTP: the access token and every request travel unencrypted, and anything between you and the service can read or change them. Use https where the deployment offers it.";
    }
}
