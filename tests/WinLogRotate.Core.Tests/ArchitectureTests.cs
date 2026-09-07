using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// Rules that are easy to state, easy to break by accident, and invisible in review.
/// </summary>
public partial class ArchitectureTests
{
    [GeneratedRegex(@"\bConsole\s*\.\s*(Write|WriteLine|Error|Out|In|Read)", RegexOptions.Compiled)]
    private static partial Regex BareConsole();

    /// <summary>
    /// Nothing outside the output sinks may write to the console.
    /// <para>
    /// Every verb has to render three ways - human text, one JSON envelope, and an NDJSON
    /// stream the GUI tails - and a single stray Console.WriteLine corrupts two of them. In
    /// the JSON case it is worse than cosmetic: an unexpected line makes the whole envelope
    /// unparseable, so the GUI reports a broken CLI rather than the actual result.
    /// </para>
    /// </summary>
    [Fact]
    public void OnlyTheOutputSinksTouchTheConsole()
    {
        var cli = Path.Combine(RepoRoot.Find().FullName, "src", "WinLogRotate.Cli");

        var offenders = Directory
            .EnumerateFiles(cli, "*.cs", SearchOption.AllDirectories)
            .Where(f => !Path.GetDirectoryName(f)!.EndsWith("Output", StringComparison.Ordinal))
            .Where(f => !f.Contains(Path.Combine("obj", ""), StringComparison.Ordinal))
            .Select(f => (File: Path.GetFileName(f), Text: File.ReadAllText(f)))
            .Where(x => BareConsole().IsMatch(x.Text))
            .Select(x => x.File)
            .ToArray();

        offenders.ShouldBeEmpty(
            "everything user-facing goes through IOutputSink; see WinLogRotate.Cli/Output");
    }

    /// <summary>
    /// The GUI must never call <c>MessageBox</c>.
    /// <para>
    /// It renders light regardless of Application.SetColorMode, so a single call ruins a
    /// dark-themed window - and it has never had a way to copy the error text out, which is
    /// the first thing anyone needs when reporting a problem. LrDialog replaces it and adds
    /// both. This is exactly the kind of rule that is obvious in review and forgotten in the
    /// one hurried fix, so it is enforced.
    /// </para>
    /// </summary>
    [Fact]
    public void TheGuiNeverCallsMessageBox()
    {
        var gui = Path.Combine(RepoRoot.Find().FullName, "src", "WinLogRotate.Gui");

        var offenders = Directory
            .EnumerateFiles(gui, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.Combine("obj", ""), StringComparison.Ordinal))
            .Where(f => File.ReadAllLines(f).Any(line =>
                line.Contains("MessageBox.Show", StringComparison.Ordinal)))
            .Select(Path.GetFileName)
            .ToArray();

        offenders.ShouldBeEmpty("use LrDialog instead - see WinLogRotate.Gui/Ui/LrDialog.cs");
    }

    /// <summary>
    /// Every capability must be reachable from the command line.
    /// <para>
    /// WinForms does not run on Server Core, which is where IIS is most often installed. One
    /// GUI-only feature therefore strands exactly the users who need this tool most, with no
    /// workaround at all. The GUI shelling out for everything is what keeps that true, so this
    /// asserts it structurally: the GUI reaches the engine only through CliRunner.
    /// </para>
    /// </summary>
    [Fact]
    public void TheGuiReachesTheEngineOnlyThroughTheCommandLine()
    {
        var gui = Path.Combine(RepoRoot.Find().FullName, "src", "WinLogRotate.Gui");

        var offenders = Directory
            .EnumerateFiles(gui, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.Combine("obj", ""), StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f) is var text
                        && (text.Contains("RotationRunner", StringComparison.Ordinal)
                            || text.Contains("PlanExecutor", StringComparison.Ordinal)
                            || text.Contains("ManageJobPlanner", StringComparison.Ordinal)
                            || text.Contains("RotateJobPlanner", StringComparison.Ordinal)))
            .Select(Path.GetFileName)
            .ToArray();

        offenders.ShouldBeEmpty(
            "the GUI must shell out to winlogrotate.exe rather than driving the engine directly, "
            + "so that every capability stays reachable on Server Core where WinForms cannot run");
    }

    /// <summary>
    /// Only a small, named set of files may read a secret's value.
    /// <para>
    /// <c>SecretString.Reveal</c> is deliberately ugly so that writing it feels like a
    /// decision rather than a getter, and this is what makes that more than a convention. The
    /// value has exactly one legitimate destination - the transport that authenticates with it
    /// - and every other caller is a leak waiting to be discovered in a support bundle.
    /// </para>
    /// <para>
    /// The allowlist is spelled out rather than derived from a folder, because "anything under
    /// Secrets/ may reveal" is the rule that stops meaning anything the first time somebody
    /// adds a convenience helper there.
    /// </para>
    /// </summary>
    [Fact]
    public void OnlyTheAllowlistedFilesRevealASecret()
    {
        string[] allowed =
        [
            // Where a secret is encrypted and decrypted.
            "SecretStore.cs",
            // The type itself.
            "SecretString.cs",
            // The tests that prove the redaction works, and that a stored secret round-trips.
            "SecretStringTests.cs",
            "SecretStoreTests.cs",
        ];

        // Assembled rather than written out, so that this file - which scans every file under
        // src/ and tests/, including itself - does not match its own search string. The
        // alternative, excluding this file, would make it the one place a violation could hide.
        var needle = "." + "Reveal" + "()";

        var root = RepoRoot.Find().FullName;

        var offenders = new[] { "src", "tests" }
            .Select(d => Path.Combine(root, d))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains(Path.Combine("obj", ""), StringComparison.Ordinal))
            .Where(f => !f.Contains(Path.Combine("bin", ""), StringComparison.Ordinal))
            .Where(f => !allowed.Contains(Path.GetFileName(f), StringComparer.Ordinal))
            .Where(f => File.ReadAllText(f).Contains(needle, StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToArray();

        offenders.ShouldBeEmpty(
            "a secret's value has one destination - the transport that authenticates with it. "
            + "If a new transport needs it, add that file to the allowlist deliberately.");
    }

    /// <summary>
    /// The engine's platform-neutral half must stay neutral.
    /// <para>
    /// Marking the whole Core assembly [SupportedOSPlatform("windows")] is the tempting
    /// shortcut - it silences CA1416 in one line. It also makes CA1416 fire on every call
    /// site in the neutral CLI and test projects, and it strands the pure suite, which runs
    /// on Linux precisely because glob matching, schedule arithmetic and version comparison
    /// genuinely are portable. Annotate the Win32 types instead.
    /// </para>
    /// </summary>
    [Fact]
    public void CoreIsNotMarkedWindowsOnlyAtAssemblyScope()
    {
        var core = Path.Combine(RepoRoot.Find().FullName, "src", "WinLogRotate.Core");

        var offenders = Directory
            .EnumerateFiles(core, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.Combine("obj", ""), StringComparison.Ordinal))
            .Where(f => File.ReadAllLines(f).Any(l =>
                l.TrimStart().StartsWith("[assembly: SupportedOSPlatform", StringComparison.Ordinal)))
            .Select(Path.GetFileName)
            .ToArray();

        offenders.ShouldBeEmpty();
    }
}
