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

    [GeneratedRegex(@"new\s+Font\s*\(|\bFont\s+\w+\s*=\s*new\s*\(", RegexOptions.Compiled)]
    private static partial Regex CreatesAFont();

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
    /// Every window takes its positions from a layout the tests can see.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three windows typed their positions beside their controls, in a project no test on this
    /// leg can load, and all three were wrong: the job editor gave its grid a height of -52, the
    /// credential prompt left four pixels of its Store button inside the window, and the message
    /// dialog painted its details over its OK button. Each has a layout record in Gui.Model now,
    /// and <c>JobEditorLayoutTests</c> and <c>DialogLayoutTests</c> assert every box positive,
    /// disjoint and inside the window.
    /// </para>
    /// <para>
    /// Which is worth nothing if a window types a rectangle of its own beside the layout. So no
    /// file in the project may: a position the layout does not know is a position no rule can
    /// check. The three windows that have a layout must also ask it.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryWindowTakesItsPositionsFromALayout()
    {
        GuiFiles()
            // Boxes.cs is the bridge: the one place a layout's Box becomes a Rectangle, and every
            // rectangle it builds is a box the layout tests have already seen.
            .Where(f => Path.GetFileName(f) != "Boxes.cs")
            .Where(f => Code(f).Contains("new Rectangle(", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ShouldBeEmpty("a hand-typed position is one the layout tests cannot see");

        var layouts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Path.Combine("Pages", "JobEditor.cs")] = "JobEditorLayout.Compute(",
            [Path.Combine("Ui", "LrDialog.cs")] = "DialogLayout.Compute(",
            [Path.Combine("Ui", "SecretPrompt.cs")] = "SecretPromptLayout.Compute(",
        };

        foreach (var (file, layout) in layouts)
        {
            var code = Code(Path.Combine(Gui, file));

            code.ShouldContain(layout, customMessage: $"{file} must ask its layout");
            code.ShouldContain("layout.ClientHeight", customMessage: $"{file}'s client size is the layout's too");
        }
    }

    [GeneratedRegex(@":\s*(?:Form|UserControl)\b|new\s+Form\b", RegexOptions.Compiled)]
    private static partial Regex IsAWindow();

    /// <summary>
    /// Every window and every page scales with the monitor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ApplicationHighDpiMode</c> is PerMonitorV2, which scales the fonts, and no form set an
    /// <c>AutoScaleMode</c>, so nothing scaled the pixels: at 125 per cent a 24-pixel box on a
    /// 30-pixel pitch held 30-pixel text, and an 18-pixel caption clipped its own descenders.
    /// </para>
    /// <para>
    /// Pages as well as forms, and for a reason rather than for symmetry: a page is created after
    /// the main window has scaled, so the window does not scale it, and it has to scale itself.
    /// Dimensions of 96 are what say that every number in this project is a logical pixel at 100
    /// per cent.
    /// </para>
    /// <para>
    /// The order is the mechanism, so the order is what is asserted. The framework scales a
    /// window once, at the first layout after its dimensions are set, and scales whatever exists
    /// at that moment; with layout running, setting the dimensions is that layout, before a
    /// single control has been added, and the two lines are then true and do nothing. So layout
    /// is suspended first, the controls come after the dimensions, and <c>PerformAutoScale</c>
    /// after the controls is the one scale.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryWindowScalesWithTheMonitor()
    {
        var windows = GuiFiles()
            .Select(f => (Name: Path.GetFileName(f), Code: Code(f)))
            .Where(f => IsAWindow().IsMatch(f.Code))
            .ToArray();

        // Self-check: the main window, the editor, two dialogs and six pages.
        windows.Length.ShouldBeGreaterThan(7, "found almost no windows, so the scan has stopped recognising them");

        string[] order =
        [
            "SuspendLayout()",
            "AutoScaleMode = AutoScaleMode.Dpi",
            "AutoScaleDimensions = new SizeF(96F, 96F)",
            "ResumeLayout(",
            "PerformAutoScale()",
        ];

        windows
            .Where(f => !InOrder(f.Code, order))
            .Select(f => f.Name)
            .ShouldBeEmpty(
                "a window scales with the monitor only when it suspends layout, sets AutoScaleMode.Dpi "
                + "and 96-pixel dimensions, builds its controls, resumes, and then calls PerformAutoScale - "
                + "in that order; any other keeps 100 per cent boxes under 125 per cent text");
    }

    /// <summary>Whether every phrase appears, each after the one before it.</summary>
    private static bool InOrder(string code, IReadOnlyList<string> phrases)
    {
        var at = -1;

        foreach (var phrase in phrases)
        {
            at = code.IndexOf(phrase, at + 1, StringComparison.Ordinal);

            if (at < 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The window catches what its handlers let escape.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A premise the GUI's comments repeated was wrong in a useful way: an exception escaping an
    /// <c>async void</c> handler does not end a WinForms process. The framework shows its stock
    /// <c>ThreadExceptionDialog</c> - light, a stack trace, a Continue button - and leaves the
    /// page half-updated. No handler was installed, so that dialog is what every unanticipated
    /// failure looked like.
    /// </para>
    /// <para>
    /// Three lines, and all three are needed: the mode, or the framework keeps its own dialog;
    /// the thread handler, which is where an escaping handler exception goes; and the task
    /// handler, for a fault nobody awaited.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheWindowCatchesWhatItsHandlersLetEscape()
    {
        var program = Code(Path.Combine(Gui, "Program.cs"));

        program.ShouldContain("Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException)");
        program.ShouldContain("Application.ThreadException +=");
        program.ShouldContain("TaskScheduler.UnobservedTaskException +=");
    }

    /// <summary>
    /// The quit listener is armed once the window can hear it, and stays armed.
    /// </summary>
    /// <remarks>
    /// It was registered before <c>Application.Run</c>, with <c>executeOnlyOnce</c>. A signal
    /// in the window between registration and the handle was consumed by a post that had nothing
    /// to post to, and the registration was then spent - so the installer's polite request was
    /// heard exactly never, and its fixed two-second wait failed on a locked executable.
    /// </remarks>
    [Fact]
    public void TheQuitListenerIsArmedOnceTheWindowCanHearIt()
    {
        var program = Code(Path.Combine(Gui, "Program.cs"));

        program.ShouldContain("HandleCreated", customMessage: "arm the wait when there is a handle to post to");
        program.ShouldContain("executeOnlyOnce: false");
        program.ShouldNotContain("executeOnlyOnce: true", customMessage: "a wait that fires once is spent by a signal nobody could act on");
    }

    /// <summary>
    /// The main window disposes the page it replaces.
    /// </summary>
    /// <remarks>
    /// <c>Controls.Clear()</c> detaches without disposing, so every navigation leaked a page -
    /// window handles parked, fonts rooted, handlers still wired to a runner that would call them.
    /// A control disposes itself out of its parent's collection, so disposing is the whole of the
    /// replacement and clearing is never needed.
    /// </remarks>
    [Fact]
    public void TheMainWindowDisposesThePageItReplaces()
    {
        var main = Code(Path.Combine(Gui, "MainForm.cs"));

        main.ShouldNotContain("Controls.Clear()", customMessage: "Clear detaches without disposing; dispose the old pages instead");
        main.ShouldContain(".Dispose()");
    }

    /// <summary>
    /// A window that waits checks it is still there before it says anything.
    /// </summary>
    /// <remarks>
    /// Every page awaits the CLI and then writes to its controls or opens a dialog it owns. An
    /// operator who navigates away during the wait disposes the page, and a dialog owned by a
    /// disposed control is itself an exception - reported, now, by the handler above, which is
    /// better than the stock dialog and still a report of something that should not happen. The
    /// rule is a per-file floor, not a per-await proof: a page that never checks at all is what
    /// it catches.
    /// </remarks>
    [Fact]
    public void AWindowThatWaitsChecksItIsStillThere()
    {
        var waiting = GuiFiles()
            .Where(f => Code(f).Contains("ConfigureAwait(true)", StringComparison.Ordinal))
            .ToArray();

        // Self-check: every page waits on the CLI, or the scan has stopped seeing awaits.
        waiting.Length.ShouldBeGreaterThan(5);

        waiting
            .Where(f => !Code(f).Contains("IsDisposed", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ShouldBeEmpty("a page that waits on the CLI must check IsDisposed before it touches itself");
    }

    /// <summary>
    /// Every font a window creates is disposed by it.
    /// </summary>
    /// <remarks>
    /// A <c>Font</c> assigned to a control is not owned by the control, so one created inline in
    /// a field initialiser outlived the page on every navigation, and one created per error dialog
    /// outlived every error. Four files did this. The rule asks only that a file which creates one
    /// also disposes something, which is the shape the fix takes in each of them.
    /// </remarks>
    [Fact]
    public void EveryFontAWindowCreatesIsDisposed()
    {
        // Both spellings: `new Font(...)`, and a field typed Font initialised with a target-typed
        // `new(...)`, which is how the pages write it and never spells the type after `new`.
        var creators = GuiFiles()
            .Where(f => CreatesAFont().IsMatch(Code(f)))
            .ToArray();

        // Self-check: the run, scheduling and settings pages and the dialog all create one.
        creators.Length.ShouldBeGreaterThan(2);

        creators
            .Where(f => !Code(f).Contains(".Dispose(", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ShouldBeEmpty("a font a window creates is a font it has to dispose");
    }
}
