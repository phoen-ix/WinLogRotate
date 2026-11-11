using Microsoft.Win32;

namespace WinLogRotate.Hosting.Install;

/// <summary>
/// Whether the .NET Desktop Runtime a <c>-min</c> console needs is installed.
/// </summary>
/// <remarks>
/// <para>
/// The same question the <c>-min</c> installer's <c>DotnetPresent</c> asks, the same way: where
/// the host says .NET lives - <c>InstallLocation</c> in the 32-bit registry view, which is where
/// .NET records it - else <c>%ProgramFiles%\dotnet</c>, and then whether
/// <c>shared\Microsoft.WindowsDesktop.App</c> holds a version of the major in question. Only that
/// major: the default roll-forward moves within a major, never across one.
/// </para>
/// <para>
/// The major is the one this build runs on. An update to a release on the same major needs what
/// this copy needs; a release that moves to the next major is refused by its own installer,
/// which an update starts with <c>/NORUNTIME</c> (exit code 4, nothing installed).
/// </para>
/// </remarks>
public static class DesktopRuntime
{
    /// <summary>The runtime major this build targets.</summary>
    public static int Major => Environment.Version.Major;

    /// <summary>Where the runtime can be downloaded by hand.</summary>
    public static string DownloadPage => $"https://dotnet.microsoft.com/download/dotnet/{Major}.0";

    /// <summary>The winget package that installs it.</summary>
    public static string WingetId => $"Microsoft.DotNet.DesktopRuntime.{Major}";

    public static bool IsInstalled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        string? location = null;

        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var key = root.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64");
            location = key?.GetValue("InstallLocation") as string;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Unreadable reads as unrecorded: the usual folder is still looked at.
        }

        if (string.IsNullOrWhiteSpace(location))
        {
            location = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
        }

        return HasMajor(location, Major);
    }

    /// <summary>Whether <paramref name="dotnetRoot"/> holds a Desktop Runtime of <paramref name="major"/>.</summary>
    public static bool HasMajor(string dotnetRoot, int major)
    {
        try
        {
            return Directory.EnumerateDirectories(
                Path.Combine(dotnetRoot, "shared", "Microsoft.WindowsDesktop.App"), $"{major}.*").Any();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
