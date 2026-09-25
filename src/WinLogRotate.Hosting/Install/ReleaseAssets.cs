namespace WinLogRotate.Hosting.Install;

/// <summary>
/// The file names a release publishes, as the release workflow spells them.
/// </summary>
/// <remarks>
/// Deterministic on purpose, and pinned here rather than discovered through the GitHub API:
/// the API is rate-limited per address, an office behind one NAT shares that limit, and the
/// names have not changed since the first release. A rename in <c>ci.yml</c> has to be made
/// here too, and <c>ReleaseAssetsTests</c> is the reminder.
/// </remarks>
public static class ReleaseAssets
{
    /// <summary>Every download's SHA-256, one per line, beside the downloads.</summary>
    public const string SumsFileName = "SHA256SUMS.txt";

    public static bool IsVariant(string? variant) => variant is "full" or "min";

    /// <summary>
    /// The installer that carries the given GUI build, for the given release.
    /// </summary>
    /// <remarks>
    /// The unsuffixed installer is never chosen. It ships the self-contained GUI exactly as
    /// <c>-full</c> does, plus a runtime bootstrap that build does not need, so <c>-full</c> is
    /// the same thing with less to download and nothing to prompt about.
    /// </remarks>
    public static string InstallerName(string variant, Version version)
    {
        if (!IsVariant(variant))
        {
            throw new ArgumentException($"'{variant}' is not a GUI build this product ships.", nameof(variant));
        }

        return $"WinLogRotate-Setup-{version.ToString(3)}-{variant}.exe";
    }

    /// <summary>The path under a release's download root: <c>v1.2.3/name</c>.</summary>
    public static string DownloadPath(Version version, string assetName) =>
        $"v{version.ToString(3)}/{assetName}";
}
