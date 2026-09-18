using WinLogRotate.Core;

namespace WinLogRotate.Hosting.Install;

/// <summary>
/// The command line an in-app update hands the installer.
/// </summary>
/// <remarks>
/// <para>
/// Every switch is explicit, because the installer's defaults are for a person: without
/// <c>/AllUsers</c> or <c>/CurrentUser</c> it goes by the token, without <c>/HOST=</c> it
/// goes by what it finds recorded, and both are exactly the values this process has already
/// read. Saying them again costs nothing and makes the hand-over a complete sentence in the
/// installer's log.
/// </para>
/// <para>
/// <c>/NORUNTIME</c> because the runtime was there before the update and is still there after
/// it; a silent bootstrap could otherwise spend minutes on winget for nothing. <c>/RESTART</c>
/// only when the caller is a window that wants to come back - a prompt does not.
/// </para>
/// </remarks>
public static class InstallerArguments
{
    public static IReadOnlyList<string> For(InstallScope scope, string hostKind, bool restartGui)
    {
        if (scope == InstallScope.Portable)
        {
            throw new ArgumentException("A portable copy has no installer to re-run.", nameof(scope));
        }

        if (hostKind is not ("task" or "none"))
        {
            throw new ArgumentException($"'{hostKind}' is not a run host the installer accepts.", nameof(hostKind));
        }

        var arguments = new List<string>
        {
            "/S",
            scope == InstallScope.PerMachine ? "/AllUsers" : "/CurrentUser",
            $"/HOST={hostKind}",
            "/NORUNTIME",
        };

        if (restartGui)
        {
            arguments.Add("/RESTART");
        }

        return arguments;
    }
}
