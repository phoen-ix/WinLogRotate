using System.Runtime.Versioning;
using System.Security.Principal;

namespace WinLogRotate.Core;

/// <summary>Questions about the token this process is running under.</summary>
public static class Privilege
{
    /// <summary>
    /// True when this process holds an elevated administrator token.
    /// <para>
    /// Note what this does NOT mean. An administrator running unelevated returns false here
    /// even though they could elevate - that is the point, because what matters for a file
    /// operation is the token we actually hold, not the one we could ask for. It is also
    /// false on non-Windows, so the pure test suite can call it.
    /// </para>
    /// </summary>
    public static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        return IsElevatedWindows();
    }

    [SupportedOSPlatform("windows")]
    private static bool IsElevatedWindows()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
