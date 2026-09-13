using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// Rules about the window, asserted on the leg that cannot open one.
/// </summary>
/// <remarks>
/// <para>
/// Every rule here is a text scan, for the reason every GUI rule in <c>ArchitectureTests</c>
/// is: a test project that references <c>WinLogRotate.Gui</c> inherits
/// <c>Microsoft.WindowsDesktop.App</c> and cannot run on the Linux leg at all. What a scan can
/// do is keep a decision that was made once from being unmade by the next hurried fix, and say
/// why in the failure message.
/// </para>
/// <para>
/// Its own file rather than more rules in <c>ArchitectureTests</c>, which is three thousand
/// lines about the product; these are about one window in it.
/// </para>
/// </remarks>
public sealed partial class GuiArchitectureTests
{
    private static string Gui => Path.Combine(RepoRoot.Find().FullName, "src", "WinLogRotate.Gui");

    private static IEnumerable<string> GuiFiles() =>
        Directory
            .EnumerateFiles(Gui, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.Combine("obj", ""), StringComparison.Ordinal))
            .Where(f => !f.Contains(Path.Combine("bin", ""), StringComparison.Ordinal));

    /// <summary>A file's code lines, without its comments.</summary>
    /// <remarks>
    /// A rule that says "this call must not appear here" would otherwise be broken by the doc
    /// comment explaining why it must not appear here.
    /// </remarks>
    private static string[] CodeLines(string path) =>
        File.ReadAllLines(path)
            .Where(l => !l.TrimStart().StartsWith("///", StringComparison.Ordinal))
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .ToArray();

    private static string Code(string path) => string.Join('\n', CodeLines(path));

    [GeneratedRegex(@"""job"",\s*(?:""(?:add|set|enable|disable|remove)""|on\s*\?)", RegexOptions.Compiled)]
    private static partial Regex RunsAJobVerb();

    [GeneratedRegex(@"^\s*(?:private|public|internal|protected)\b.*\(.*\)\s*$", RegexOptions.Compiled)]
    private static partial Regex MethodSignature();

    /// <summary>
    /// A <c>job</c> verb's answer is read by the projection written for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every refused edit read "Could not read the response from winlogrotate.exe." The pages fed
    /// the answers of <c>job add</c>, <c>set</c>, <c>enable</c>, <c>disable</c> and <c>remove</c>
    /// to <c>ConfigCheckProjection</c>, which requires <c>result.errors</c> and
    /// <c>result.warnings</c>. A <c>JobEditResult</c> carries neither, and a refusal carries no
    /// result at all - so the real reason, sitting in the same envelope, was shown as a generic
    /// sentence with the truth under an expander.
    /// </para>
    /// <para>
    /// Two halves. A file that runs a job verb - by spelling its words, or through the model's
    /// argument builders - must read the answer with <c>JobEditProjection</c>. And
    /// <c>ConfigCheckProjection</c> may be called only from a method that runs
    /// <c>config check</c>, which is the one verb whose payload has that shape.
    /// </para>
    /// </remarks>
    [Fact]
    public void AJobVerbsAnswerIsReadByTheJobEditProjection()
    {
        var files = GuiFiles().Select(f => (Name: Path.GetFileName(f), Path: f, Code: Code(f))).ToArray();

        var runners = files
            .Where(f => RunsAJobVerb().IsMatch(f.Code)
                || f.Code.Contains("JobEditorModel.SaveArgs(", StringComparison.Ordinal)
                || f.Code.Contains("JobEditorModel.ValidateArgs(", StringComparison.Ordinal))
            .ToArray();

        // Self-check: the editor and the Jobs page both run job verbs, or the scan has stopped
        // recognising the spelling.
        runners.Select(f => f.Name).ShouldContain("JobEditor.cs");
        runners.Select(f => f.Name).ShouldContain("JobsPage.cs");

        runners
            .Where(f => !f.Code.Contains("JobEditProjection.From(", StringComparison.Ordinal))
            .Select(f => f.Name)
            .ShouldBeEmpty("a job verb's answer is a JobEditResult, or a refusal with no result at all; "
                + "read it with JobEditProjection");

        var misread = new List<string>();
        var checks = 0;

        foreach (var file in files)
        {
            var lines = CodeLines(file.Path);

            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("ConfigCheckProjection.From(", StringComparison.Ordinal))
                {
                    continue;
                }

                checks++;

                var start = i;
                while (start > 0 && !MethodSignature().IsMatch(lines[start]))
                {
                    start--;
                }

                var method = string.Join('\n', lines[start..(i + 1)]);

                if (!method.Contains("\"config\", \"check\"", StringComparison.Ordinal))
                {
                    misread.Add($"{file.Name}:{i + 1}");
                }
            }
        }

        // Self-check: the Jobs page's own configuration check still goes through it.
        checks.ShouldBeGreaterThan(0, "nothing calls ConfigCheckProjection any more, so this checks nothing");

        misread.ShouldBeEmpty(
            "ConfigCheckProjection reads result.errors and result.warnings, which only "
            + "'config check' produces; every other verb's answer needs its own projection");
    }

    /// <summary>
    /// The job editor takes every position from the layout the tests can see.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The form gave its advanced grid the height left over after eight rows of fields, which
    /// was -52, and nothing could say so: the arithmetic lived beside the controls, in a project
    /// no test on this leg can load. <c>JobEditorLayout</c> is that arithmetic as a record, and
    /// <c>JobEditorLayoutTests</c> asserts every box positive, disjoint and inside the window.
    /// </para>
    /// <para>
    /// Which is worth nothing if the form types a rectangle of its own beside it. So it may not:
    /// a position the layout does not know is a position no rule can check.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheJobEditorTakesEveryPositionFromTheLayout()
    {
        var editor = Code(Path.Combine(Gui, "Pages", "JobEditor.cs"));

        editor.ShouldContain("JobEditorLayout.Compute(", customMessage: "the form must ask the layout");
        editor.ShouldNotContain("new Rectangle(", customMessage: "a hand-typed position is one the layout tests cannot see");
        editor.ShouldContain("layout.ClientHeight", customMessage: "the client size is the layout's too");
    }
}
