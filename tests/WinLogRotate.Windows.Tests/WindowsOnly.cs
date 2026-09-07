using Xunit;

namespace WinLogRotate.Windows.Tests;

/// <summary>
/// Every test in this project needs real Windows.
/// </summary>
/// <remarks>
/// The project targets net10.0-windows, which compiles anywhere but only runs here. Skipping
/// explicitly, rather than excluding the project from the Linux build, means a developer on
/// Linux running the whole solution sees "skipped: needs Windows" instead of a wall of
/// PlatformNotSupportedException - and, more importantly, never sees green for a suite that did
/// not run. CI runs this project only on the windows-2025 job.
/// </remarks>
internal static class WindowsOnly
{
    public static void Require() =>
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Needs Windows: real DPAPI, ACLs and registry.");
}
