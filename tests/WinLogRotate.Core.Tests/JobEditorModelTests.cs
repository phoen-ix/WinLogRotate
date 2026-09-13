using Shouldly;
using WinLogRotate.Cli.Commands;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Gui.Cli;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// The decisions the job editor makes, on the leg that can see them.
/// </summary>
/// <remarks>
/// The form itself is a project carrying a Microsoft.WindowsDesktop.App framework reference,
/// which cannot be built or run here at all - so every decision left on the form is a decision no
/// test can check. These are the decisions: which fields a file sets and which it inherits, and
/// what command line somebody's typing amounts to.
/// </remarks>
public sealed class JobEditorModelTests
{
    /// <summary>A realistic <c>job show --json</c> response, as the CLI actually emits one.</summary>
    private const string Shown = """
        {"schema":1,"product":"WinLogRotate","version":"0.14.0","verb":"job show","ok":true,
         "exitCode":0,
         "result":{"job":"iis","path":"C:\\ProgramData\\WinLogRotate\\conf.d\\iis.toml",
          "keys":[
           {"key":"name","source":"\"iis\"","line":4,"known":true},
           {"key":"paths","source":"[\"C:/logs/*.log\", \"D:/b/*.log\"]","line":5,"known":true},
           {"key":"rotate","source":"14","line":6,"known":true},
           {"key":"maxsize","source":"\"100M\"","line":7,"known":true},
           {"key":"ownr","source":"\"team-web\"","line":8,"known":false}],
          "diagnostics":[
           {"severity":"Warning","code":"LR1003","message":"'ownr' is not a [job] setting, and is ignored.",
            "file":"x","line":8,"column":1}]}}
        """;

    private static JobEditorView Loaded() => JobEditorModel.From(Shown);

    private static void ShouldParse(IReadOnlyList<string> args) =>
        CommandTree.Build().Parse([.. args]).Errors
            .ShouldBeEmpty($"the editor would run this: {string.Join(' ', args)}");

    // ---- reading ----------------------------------------------------------------------------

    /// <summary>
    /// Every key this product knows is offered, whether the file writes it or not.
    /// </summary>
    /// <remarks>
    /// A form listing only the keys a file happens to write would offer no way to add the others,
    /// which is the defect the whole milestone is about wearing a smaller hat.
    /// </remarks>
    [Fact]
    public void EveryKeyIsOfferedWhetherTheFileWritesItOrNot()
    {
        var view = Loaded();

        foreach (var row in JobSchema.Keys)
        {
            view.Fields.ShouldContain(f => f.Key == row.Key, $"no field for '{row.Key}'");
        }

        view.Fields.Count.ShouldBe(JobSchema.Keys.Count + 1, "the schema, plus the file's own 'ownr'");
    }

    /// <summary>
    /// A field says whether the file sets it or the job inherits it.
    /// </summary>
    /// <remarks>
    /// The distinction the editor turns on. A field showing a value it inherited, saved back,
    /// stops inheriting - so an editor that cannot tell the two apart converts every
    /// <c>[defaults]</c> value into a per-job one the first time somebody presses Save.
    /// </remarks>
    [Fact]
    public void AFieldSaysWhetherItIsSetHereOrInherited()
    {
        var view = Loaded();

        view.Fields.Single(f => f.Key == "rotate").IsSet.ShouldBeTrue();
        view.Fields.Single(f => f.Key == "rotate").Value.ShouldBe("14");
        view.Fields.Single(f => f.Key == "rotate").Line.ShouldBe(6);

        view.Fields.Single(f => f.Key == "compress").IsSet.ShouldBeFalse();
        view.Fields.Single(f => f.Key == "compress").Value.ShouldBeNull();
        view.Fields.Single(f => f.Key == "compress").Line.ShouldBe(0);
    }

    /// <summary>A value is shown as a person would type it, not as TOML writes it.</summary>
    /// <remarks>
    /// A text box holding <c>"iis"</c> with the quotes showing is a box somebody will delete the
    /// quotes from and then wonder why nothing changed. A size keeps its own spelling: the file
    /// says <c>"100M"</c> and so does the box, never 104857600.
    /// </remarks>
    [Fact]
    public void AValueIsShownAsAPersonWouldTypeIt()
    {
        var view = Loaded();

        view.Fields.Single(f => f.Key == "name").Value.ShouldBe("iis");
        view.Fields.Single(f => f.Key == "maxsize").Value.ShouldBe("100M");
        view.Fields.Single(f => f.Key == "rotate").Value.ShouldBe("14");

        // A list is one item per line, which is what a multiline box shows.
        view.Fields.Single(f => f.Key == "paths").Value.ShouldBe("C:/logs/*.log\nD:/b/*.log");
    }

    /// <summary>A key this build does not know is shown, not dropped.</summary>
    /// <remarks>
    /// The operator is being warned about it, and <c>--unset</c> is the only way to act on that
    /// warning. A form that hid it would show a file that does not match the one on disk.
    /// </remarks>
    [Fact]
    public void AKeyThisBuildDoesNotKnowIsShownAndMarked()
    {
        var field = Loaded().Fields.Single(f => f.Key == "ownr");

        field.Known.ShouldBeFalse();
        field.IsSet.ShouldBeTrue();
        field.Value.ShouldBe("team-web");
        field.Line.ShouldBe(8);
    }

    /// <summary>The file's own verdict comes through, because it is why somebody opened it.</summary>
    [Fact]
    public void TheVerdictComesThrough() =>
        Loaded().Problems.ShouldContain(p => p.Contains("ownr", StringComparison.Ordinal));

    /// <summary>
    /// A response this build cannot read says so, rather than throwing out of an event handler.
    /// </summary>
    /// <remarks>
    /// <c>GetProperty</c> throws <c>KeyNotFoundException</c> and the typed accessors throw
    /// <c>InvalidOperationException</c>; neither is a <c>JsonException</c>. The GUI installs no
    /// <c>Application.ThreadException</c> handler, so one escaping an <c>async void</c> handler
    /// ends the process. A CLI one version out of step is the ordinary way to produce that.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("""{"schema":1}""")]
    [InlineData("""{"result":{}}""")]
    [InlineData("""{"result":{"job":"iis","path":"p","keys":[{"key":"rotate"}],"diagnostics":[]}}""")]
    [InlineData("""{"result":{"job":"iis","path":"p","keys":[{"key":"rotate","source":"1","line":"six","known":true}],"diagnostics":[]}}""")]
    public void AResponseThisBuildCannotReadSaysSo(string json)
    {
        var view = JobEditorModel.From(json);

        view.Unreadable.ShouldBeTrue();
        view.Fields.ShouldBeEmpty();
        view.Problems.ShouldNotBeEmpty();
    }

    // ---- writing ----------------------------------------------------------------------------

    /// <summary>Pressing Save without typing anything sends no change.</summary>
    /// <remarks>
    /// The assertion that keeps a form from being a rewriter. Every field is handed back exactly
    /// as it was read, including the inherited ones - which is what a form does when somebody
    /// opens a job, looks at it, and presses Save.
    /// </remarks>
    [Fact]
    public void SavingWithoutTypingAnythingSendsNoChange()
    {
        var view = Loaded();

        var args = JobEditorModel.SaveArgs(
            null, "iis", isNew: false, view,
            view.Fields.ToDictionary(f => f.Key, f => f.Value));

        args.ShouldBe(["job", "set", "iis"]);
        JobEditorModel.Changes(args).ShouldBeFalse();
        ShouldParse(args);
    }

    /// <summary>
    /// An inherited field handed back untouched does not become a per-job value.
    /// </summary>
    /// <remarks>
    /// Stated on its own because it is the failure that would be invisible: the job keeps
    /// working, and stops following <c>[defaults]</c> for ever. A form that sent every field
    /// would do this on the first Save, to every key at once.
    /// </remarks>
    [Fact]
    public void AnInheritedFieldHandedBackDoesNotBecomeAPerJobValue()
    {
        var view = Loaded();
        var edited = view.Fields.ToDictionary(f => f.Key, f => f.Value);

        edited["rotate"] = "30";

        var args = JobEditorModel.SaveArgs(null, "iis", isNew: false, view, edited);

        args.ShouldBe(["job", "set", "iis", "--set", "rotate=30"]);
        args.ShouldNotContain("compress=false");
        args.Count(a => a == "--set").ShouldBe(1, "one field changed, so one --set");
        ShouldParse(args);
    }

    /// <summary>Clearing a field means inherit again, not set to nothing.</summary>
    [Fact]
    public void ClearingAFieldMeansInheritAgain()
    {
        var view = Loaded();
        var edited = view.Fields.ToDictionary(f => f.Key, f => f.Value);

        edited["rotate"] = "";
        edited["maxsize"] = "   ";

        var args = JobEditorModel.SaveArgs(null, "iis", isNew: false, view, edited);

        args.ShouldBe(["job", "set", "iis", "--unset", "rotate", "--unset", "maxsize"]);
        ShouldParse(args);
    }

    /// <summary>A list field sends one item per repetition, never a joined string.</summary>
    /// <remarks>
    /// The verb accumulates a repeated key and deliberately never splits on a separator, because
    /// a Windows path may contain any of them.
    /// </remarks>
    [Fact]
    public void AListFieldSendsOneItemPerRepetition()
    {
        var view = Loaded();
        var edited = view.Fields.ToDictionary(f => f.Key, f => f.Value);

        edited["paths"] = "C:/logs/*.log\nE:/new/*.log\n";

        var args = JobEditorModel.SaveArgs(null, "iis", isNew: false, view, edited);

        args.ShouldBe([
            "job", "set", "iis",
            "--set", "paths=C:/logs/*.log",
            "--set", "paths=E:/new/*.log"]);
        ShouldParse(args);
    }

    /// <summary>The name is never sent as a key, because the verb refuses it.</summary>
    /// <remarks>
    /// A job's name is its identity: the file is named after it and the journal is keyed by it.
    /// <c>job set --set name=</c> is a refusal, so a form that sent every field would turn every
    /// Save into one.
    /// </remarks>
    [Fact]
    public void TheNameIsNeverSentAsAKey()
    {
        var view = Loaded();
        var edited = view.Fields.ToDictionary(f => f.Key, f => f.Value);

        edited["name"] = "something-else";
        edited["rotate"] = "30";

        var args = JobEditorModel.SaveArgs(null, "iis", isNew: false, view, edited);

        args.ShouldNotContain("name=something-else");
        args.ShouldBe(["job", "set", "iis", "--set", "rotate=30"]);
        ShouldParse(args);
    }

    /// <summary>A new job is an add, and carries everything that was typed.</summary>
    [Fact]
    public void ANewJobIsAnAddCarryingEverythingTyped()
    {
        var blank = JobEditorModel.Blank();

        blank.Fields.ShouldAllBe(f => !f.IsSet);

        var args = JobEditorModel.SaveArgs(
            null, "nginx", isNew: true, blank,
            new Dictionary<string, string?>
            {
                ["name"] = "nginx",
                ["paths"] = "C:/nginx/logs/*.log",
                ["rotate"] = "14",
                ["compress"] = "true",
            });

        args.ShouldBe([
            "job", "add", "nginx",
            "--set", "paths=C:/nginx/logs/*.log",
            "--set", "rotate=14",
            "--set", "compress=true"]);
        ShouldParse(args);
    }

    /// <summary>Keys are sent in schema order, so what the form writes reads like the reference.</summary>
    [Fact]
    public void KeysAreSentInSchemaOrder()
    {
        var blank = JobEditorModel.Blank();

        var args = JobEditorModel.SaveArgs(
            null, "nginx", isNew: true, blank,
            new Dictionary<string, string?>
            {
                ["compress"] = "true",
                ["rotate"] = "14",
                ["paths"] = "C:/a/*.log",
                ["kind"] = "rotate",
            });

        args.Where((_, i) => i > 0 && args[i - 1] == "--set")
            .Select(a => a.Split('=')[0])
            .ShouldBe(["paths", "kind", "rotate", "compress"]);
    }

    /// <summary>
    /// Validate and save differ by exactly one flag.
    /// </summary>
    /// <remarks>
    /// Same arguments, same code path, same verdict. A form that validated through one route and
    /// saved through another would report a green tick and then fail - which is the failure mode
    /// an operator has no way to diagnose.
    /// </remarks>
    [Fact]
    public void ValidateAndSaveDifferByExactlyOneFlag()
    {
        var view = Loaded();
        var edited = view.Fields.ToDictionary(f => f.Key, f => f.Value);
        edited["rotate"] = "30";

        var save = JobEditorModel.SaveArgs(null, "iis", isNew: false, view, edited);
        var check = JobEditorModel.ValidateArgs(null, "iis", isNew: false, view, edited);

        check.ShouldBe([.. save, "--dry-run"]);
        ShouldParse(check);
    }

    /// <summary>The configuration directory is carried, and still parses with it.</summary>
    [Fact]
    public void TheConfigurationDirectoryIsCarried()
    {
        var view = Loaded();
        var edited = view.Fields.ToDictionary(f => f.Key, f => f.Value);
        edited["rotate"] = "30";

        var args = JobEditorModel.SaveArgs(@"C:\conf", "iis", isNew: false, view, edited);

        args.ShouldBe(["job", "set", "iis", "--set", "rotate=30", "--config-dir", @"C:\conf"]);
        ShouldParse(args);
        ShouldParse(JobEditorModel.ValidateArgs(@"C:\conf", "iis", isNew: false, view, edited));
    }

    /// <summary>
    /// Every key the form can offer produces a command line the CLI accepts.
    /// </summary>
    /// <remarks>
    /// The form's premise, asserted rather than assumed: a field for a key whose command line the
    /// CLI refuses is a field that silently does nothing, and the operator has no way to tell
    /// which one. This is the GUI half of
    /// <c>JobKeyReachabilityTests.EveryJobKeyIsReachableFromTheCommandLine</c>.
    /// </remarks>
    [Fact]
    public void EveryFieldTheFormOffersProducesACommandLineTheCliAccepts()
    {
        var blank = JobEditorModel.Blank();

        foreach (var field in blank.Fields.Where(f => f.Key != "name"))
        {
            var typed = JobEditorModel.SaveArgs(
                null, "iis", isNew: false, blank,
                new Dictionary<string, string?> { [field.Key] = field.Sample.Trim('"') });

            JobEditorModel.Changes(typed).ShouldBeTrue($"'{field.Key}' produced no change");
            ShouldParse(typed);

            // And clearing it, which is the other thing every field can do.
            var cleared = JobEditorModel.SaveArgs(
                null, "iis", isNew: false, Loaded(),
                new Dictionary<string, string?> { [field.Key] = null });

            ShouldParse(cleared);
        }
    }

    // ---- hooks -----------------------------------------------------------------------------

    /// <summary>
    /// A hook field says so when nothing typed into it will ever run.
    /// </summary>
    /// <remarks>
    /// The hook gate judges the owner of every file it would execute from, and on a per-user
    /// installation that owner is the installing user by design - so hooks are refused for most
    /// people who open this window. A field that accepted one in silence is how somebody finds
    /// out at three in the morning that their service-restart hook has never once fired.
    /// </remarks>
    [Fact]
    public void AHookFieldSaysSoWhenNothingTypedIntoItWillRun()
    {
        JobEditorModel.HookWarning(hooksAllowed: true).ShouldBeNull();

        var warning = JobEditorModel.HookWarning(hooksAllowed: false).ShouldNotBeNull();
        warning.ShouldContain("never run");
        warning.ShouldContain("doctor", Case.Insensitive);
    }

    /// <summary>The warning is about the commands, not about the timeout beside them.</summary>
    /// <remarks>
    /// <c>hook_timeout</c> is in the Hooks group and is a duration. Telling somebody a timeout
    /// will never run is nonsense, and nonsense beside a real warning is how the real one stops
    /// being read.
    /// </remarks>
    [Fact]
    public void TheWarningIsAboutTheCommandsAndNotTheTimeout()
    {
        JobEditorModel.Hooks.ShouldBe(["prerotate", "postrotate"]);

        // Both are real keys, and the timeout that sits beside them is deliberately not one.
        foreach (var hook in JobEditorModel.Hooks)
        {
            JobSchema.Find(hook).ShouldNotBeNull();
        }

        JobSchema.Find("hook_timeout").ShouldNotBeNull();
        JobEditorModel.Hooks.ShouldNotContain("hook_timeout");
    }

    /// <summary>Doctor's verdict is read, and an unreadable answer means refused.</summary>
    /// <remarks>
    /// The safe direction. A warning shown where hooks do work is a sentence somebody ignores;
    /// a warning missing where they do not is a hook nobody knows is dead.
    /// </remarks>
    [Theory]
    [InlineData("""{"result":{"hooksAllowed":true}}""", true)]
    [InlineData("""{"result":{"hooksAllowed":false}}""", false)]
    [InlineData("""{"result":{}}""", false)]
    [InlineData("""{"result":{"hooksAllowed":"yes"}}""", false)]
    [InlineData("not json", false)]
    [InlineData("", false)]
    public void DoctorsVerdictIsReadAndAnUnreadableAnswerMeansRefused(string json, bool expected) =>
        JobEditorModel.HooksAllowed(json).ShouldBe(expected);
}
