using System.Runtime.Versioning;
using Microsoft.Win32;
using WinLogRotate.Core;

namespace WinLogRotate.Hosting.Install;

/// <summary>
/// What the installer recorded about the copy that is running: for whom it was installed, which
/// run host it was set up with, and which GUI build it carries.
/// </summary>
/// <remarks>
/// <para>
/// Read from the Add/Remove Programs key the installer writes, and an entry only counts if its
/// <c>InstallLocation</c> is the folder this executable runs from. An unzipped copy on a machine
/// that also has an installed one is still portable, and updating "it" through the installer
/// would update the other copy instead - which is what the first reader did, taking the first
/// entry it found wherever it pointed.
/// </para>
/// <para>
/// HKCU first, the order the installer's own previous-install probe uses, then HKLM in both
/// registry views: the installer is a 32-bit program, so its HKLM writes land under
/// WOW6432Node, and a 64-bit reader that opens only its own view never sees them.
/// </para>
/// <para>
/// This is what <c>update apply</c> needs and nothing more: the scope decides whether the
/// installer must be run elevated and which switch to re-run it with, the host kind is passed
/// back so a silent upgrade keeps it, and the variant picks which release asset to fetch.
/// </para>
/// </remarks>
public sealed record InstallRecord
{
    public required InstallScope Scope { get; init; }

    /// <summary>The directory holding winlogrotate.exe and the GUI; null for a portable copy.</summary>
    public required string? Location { get; init; }

    /// <summary><c>task</c> or <c>none</c>, as the installer spells it. Unknown values are kept
    /// as read, so the caller can decline to pass them back.</summary>
    public required string HostKind { get; init; }

    /// <summary><c>full</c> or <c>min</c>: which GUI build is installed, and therefore which
    /// installer an update fetches.</summary>
    public required string BuildVariant { get; init; }

    /// <summary>The name the installer gives the GUI, and the file whose size stands in for a
    /// missing variant record.</summary>
    public const string GuiFileName = "winlogrotate-gui.exe";

    /// <summary>
    /// Above this, the GUI carries its own runtime.
    /// </summary>
    /// <remarks>
    /// The self-contained build is tens of megabytes; the framework-dependent one is about one.
    /// Nothing in between has ever been shipped, so the threshold does not need to be exact -
    /// it needs to be nowhere near either.
    /// </remarks>
    public const long FullBuildMinimumBytes = 20L * 1024 * 1024;

    /// <summary>
    /// The variant of an install that predates the record.
    /// </summary>
    /// <remarks>
    /// Every install made before <c>BuildVariant</c> was written - which is every install the
    /// first in-app update will run on - has no record, and the file on disk is the only evidence.
    /// Pure, so the rule is tested where the registry is not.
    /// </remarks>
    public static string InferVariant(long? guiBytes) =>
        guiBytes is >= FullBuildMinimumBytes ? "full" : "min";

    /// <summary>A recorded variant if it is one we ship, else the inference.</summary>
    public static string ResolveVariant(string? recorded, long? guiBytes) =>
        recorded is "full" or "min" ? recorded : InferVariant(guiBytes);

    /// <summary>A copy no installer put where it is, so there is no installer to re-run.</summary>
    public static InstallRecord Portable(string buildVariant) => new()
    {
        Scope = InstallScope.Portable,
        Location = null,
        HostKind = "none",
        BuildVariant = buildVariant,
    };

    /// <summary>The record for the copy in <paramref name="exeFolder"/>; portable when no entry
    /// names that folder, or on a platform without a registry.</summary>
    public static InstallRecord Detect(string exeFolder)
    {
        var guiBytes = GuiBytes(exeFolder);

        return OperatingSystem.IsWindows()
            ? DetectWindows(exeFolder, guiBytes)
            : Portable(InferVariant(guiBytes));
    }

    /// <summary>Which entry, if either, was made for the copy in <paramref name="exeFolder"/>.</summary>
    public static InstallScope ScopeOf(string exeFolder, string? userLocation, string? machineLocation)
    {
        if (!string.IsNullOrWhiteSpace(userLocation) && SameFolder(userLocation, exeFolder))
        {
            return InstallScope.PerUser;
        }

        if (!string.IsNullOrWhiteSpace(machineLocation) && SameFolder(machineLocation, exeFolder))
        {
            return InstallScope.PerMachine;
        }

        return InstallScope.Portable;
    }

    /// <summary>Case-insensitive and blind to a trailing separator, as Windows paths are.</summary>
    public static bool SameFolder(string a, string b)
    {
        try
        {
            return string.Equals(Canonical(a), Canonical(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        // Both separators: the registry holds Windows paths, and the tests run on Linux too.
        static string Canonical(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');
    }

    [SupportedOSPlatform("windows")]
    private static InstallRecord DetectWindows(string exeFolder, long? guiBytes)
    {
        var user = ReadEntry(RegistryHive.CurrentUser, RegistryView.Default);

        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            var machine = ReadEntry(RegistryHive.LocalMachine, view);
            var scope = ScopeOf(exeFolder, user?.Location, machine?.Location);

            if ((scope == InstallScope.PerUser ? user : scope == InstallScope.PerMachine ? machine : null) is { } entry)
            {
                return new InstallRecord
                {
                    Scope = scope,
                    Location = exeFolder,
                    HostKind = entry.HostKind ?? "none",
                    BuildVariant = ResolveVariant(entry.Variant, guiBytes),
                };
            }
        }

        return Portable(InferVariant(guiBytes));
    }

    private sealed record Entry(string? Location, string? HostKind, string? Variant);

    [SupportedOSPlatform("windows")]
    private static Entry? ReadEntry(RegistryHive hive, RegistryView view)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(Names.UninstallKey);

            return key is null
                ? null
                : new Entry(
                    key.GetValue("InstallLocation") as string,
                    key.GetValue(Names.HostKindValue) as string,
                    key.GetValue(Names.BuildVariantValue) as string);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // A key that cannot be read is indistinguishable from no key, and the caller's
            // answer to "no key" - this is a portable copy, update it by hand - is also the
            // right answer to "I cannot tell".
            return null;
        }
    }

    private static long? GuiBytes(string location)
    {
        try
        {
            var gui = new FileInfo(Path.Combine(location, GuiFileName));
            return gui.Exists ? gui.Length : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
