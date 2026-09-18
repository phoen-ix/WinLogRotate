using System.Runtime.Versioning;
using Microsoft.Win32;
using WinLogRotate.Core;

namespace WinLogRotate.Hosting.Install;

/// <summary>
/// What the installer recorded about this copy: where it is, for whom, which run host it was
/// set up with, and which GUI build it carries.
/// </summary>
/// <remarks>
/// <para>
/// Read from the Add/Remove Programs key the installer writes, in whichever hive holds it. A
/// per-machine entry is looked for in both registry views: the installer is a 32-bit program,
/// so its HKLM writes land under WOW6432Node, and a 64-bit reader that opens only its own view
/// never sees them. <c>CliRunner.Resolve</c> learned that first; this is the one reader every
/// other consumer now goes through.
/// </para>
/// <para>
/// This is what <c>update apply</c> needs and nothing more: the scope decides whether the
/// installer must be run elevated and which switch to re-run it with, the host kind is passed
/// back so a silent upgrade keeps it, and the variant picks which release asset to fetch. A
/// portable copy has no key and no record, and is refused rather than guessed about.
/// </para>
/// </remarks>
public sealed record InstallRecord
{
    /// <summary>Per-machine or per-user; never Portable, which has no record.</summary>
    public required InstallScope Scope { get; init; }

    /// <summary>The directory holding winlogrotate.exe and the GUI.</summary>
    public required string Location { get; init; }

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

    /// <summary>Reads the record, or null for a portable copy or a platform without a registry.</summary>
    public static InstallRecord? Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        return ReadWindows();
    }

    [SupportedOSPlatform("windows")]
    private static InstallRecord? ReadWindows()
    {
        try
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = hive.OpenSubKey(Names.UninstallKey);

                if (From(key, InstallScope.PerMachine) is { } machine)
                {
                    return machine;
                }
            }

            using var user = Registry.CurrentUser.OpenSubKey(Names.UninstallKey);
            return From(user, InstallScope.PerUser);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // A key that cannot be read is indistinguishable from no key, and the caller's
            // answer to "no key" - this is a portable copy, update it by hand - is also the
            // right answer to "I cannot tell".
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static InstallRecord? From(RegistryKey? key, InstallScope scope)
    {
        if (key?.GetValue("InstallLocation") is not string location || location.Length == 0)
        {
            return null;
        }

        var hostKind = key.GetValue(Names.HostKindValue) as string ?? "none";
        var recorded = key.GetValue(Names.BuildVariantValue) as string;

        return new InstallRecord
        {
            Scope = scope,
            Location = location,
            HostKind = hostKind,
            BuildVariant = ResolveVariant(recorded, GuiBytes(location)),
        };
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
