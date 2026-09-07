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
