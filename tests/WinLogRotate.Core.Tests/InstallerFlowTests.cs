using Shouldly;
using WinLogRotate.Hosting.Install;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// The installer's elevation flow, which no CI run can see: the runner already holds a full
/// administrator token, so the UAC handoffs never happen there. What can be held is the script's
/// shape - the same one the sibling installers have.
/// </summary>
public class InstallerFlowTests
{
    private static string Nsi() =>
        File.ReadAllText(Path.Combine(RepoRoot.Find().FullName, "packaging", "winlogrotate.nsi"))
            .Replace("\r\n", "\n");

    /// <summary>
    /// Opening the installer shows no UAC prompt: it runs asInvoker, after the MultiUser include
    /// whose "highest" it overrides, and asks for an administrator only when all-users is picked.
    /// </summary>
    [Fact]
    public void TheInstallerOpensWithoutAnAdministratorPrompt()
    {
        var nsi = Nsi();
        var include = nsi.IndexOf("\n!include \"MultiUser.nsh\"", StringComparison.Ordinal);
        // The directive, not a comment that names it.
        var level = nsi.IndexOf("\nRequestExecutionLevel user", StringComparison.Ordinal);

        include.ShouldBeGreaterThan(0);
        level.ShouldBeGreaterThan(include, "RequestExecutionLevel user must follow the include it overrides");
        nsi.ShouldContain("!define MULTIUSER_PAGE_CUSTOMFUNCTION_LEAVE ElevateForAllUsers");
    }

    /// <summary>
    /// Picking all-users starts an elevated copy. That copy continues at the run-host page
    /// instead of greeting the user again: the relaunch carries the original switches plus
    /// /AllUsers /RESUME, .onInit reads /RESUME, and the three pages the first copy showed skip
    /// themselves.
    /// </summary>
    [Fact]
    public void TheElevatedCopyContinuesWhereTheFirstOneStopped()
    {
        var nsi = Nsi();
        nsi.ShouldContain("ExecShell \"runas\" \"$EXEPATH\" \"$0 /AllUsers /RESUME\"");
        nsi.ShouldMatch(@"\$\{GetOptions\}\s+\$R0\s+""/RESUME""");

        foreach (var page in new[]
        {
            "!insertmacro MUI_PAGE_WELCOME",
            "!insertmacro MUI_PAGE_LICENSE",
            "!insertmacro MULTIUSER_PAGE_INSTALLMODE",
        })
        {
            var at = nsi.IndexOf(page, StringComparison.Ordinal);
            at.ShouldBeGreaterThan(0, page);
            var before = nsi[..at].Split('\n').Reverse().Take(4);
            before.ShouldContain(line => line.Contains("PRE SkipIfResumed"), $"{page} is not skipped in the elevated copy");
        }
    }

    /// <summary>
    /// An all-users uninstall hands over to an elevated copy of the registered uninstaller, with
    /// the switches it was given - so /S stays quiet and /PURGEDATA still purges.
    /// </summary>
    [Fact]
    public void AnAllUsersUninstallElevatesWithItsSwitches()
    {
        Nsi().ShouldContain("ExecShell \"runas\" \"$INSTDIR\\uninstall.exe\" \"$1\"");
    }

    /// <summary>
    /// The uninstaller sweeps the folders the in-app updater downloads into.
    /// </summary>
    [Fact]
    public void TheUninstallerSweepsTheUpdatersDownloads()
    {
        Nsi().ShouldContain($"\"$TEMP\\{UpdateEngine.DownloadDirectoryPrefix}*\"");
    }
}
