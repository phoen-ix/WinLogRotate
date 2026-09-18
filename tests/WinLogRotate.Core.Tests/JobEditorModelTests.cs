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

    /// <summary>A <c>job show</c> response whose keys are the ones given, for one case.</summary>
    private static string ShownWith(params (string Key, string Source)[] keys) =>
        """{"schema":1,"ok":true,"exitCode":0,"result":{"job":"iis","path":"C:\\pd\\conf.d\\iis.toml","diagnostics":[],"keys":["""
        + string.Join(',', keys.Select((k, i) =>
            $$$"""{"key":"{{{k.Key}}}","source":{{{System.Text.Json.JsonSerializer.Serialize(k.Source)}}},"line":{{{i + 4}}},"known":true}"""))
        + "]}}";

    /// <summary>
    /// A validly cased enum value is shown in the spelling the form offers.
    /// </summary>
    /// <remarks>
    /// The binder accepts <c>schedule = "Daily"</c>, and so does the form's combo list - but the
    /// list holds "daily", the match was case-sensitive, and a value that matched nothing left the
    /// combo on no entry at all. Reading that back gave null, null differed from "Daily", and an
    /// untouched Save sent <c>--unset schedule</c>: Check said the change would be accepted, Save
    /// prompted for elevation, and a valid line was deleted from the file.
    /// </remarks>
    [Theory]
    [InlineData("\"Daily\"")]
    [InlineData("\"DAILY\"")]
    [InlineData("'daily'")]
    public void AValidlyCasedEnumValueIsShownInTheSpellingTheFormOffers(string source)
    {
        var view = JobEditorModel.From(ShownWith(("schedule", source), ("kind", "\"Manage\"")));

        view.Fields.Single(f => f.Key == "schedule").Value.ShouldBe("daily");
        view.Fields.Single(f => f.Key == "kind").Value.ShouldBe("manage");

        // What the form hands back is the spelling it offered, and that is not a change.
        var untouched = JobEditorModel.SaveArgs(
            null, "iis", isNew: false, view,
            new Dictionary<string, string?> { ["schedule"] = "daily", ["kind"] = "manage" });

        untouched.ShouldBe(["job", "set", "iis"]);
    }

    /// <summary>
    /// An enum value this build does not know is kept as written, and an untouched Save keeps it.
    /// </summary>
    /// <remarks>
    /// A newer CLI may accept a value this window has no entry for. The form adds it to the list
    /// so it can be selected and handed back; what this pins is that the model neither rewrites
    /// it nor treats its return as a change.
    /// </remarks>
    [Fact]
    public void AnEnumValueThisBuildDoesNotKnowSurvivesUntouched()
    {
        var view = JobEditorModel.From(ShownWith(("schedule", "\"fortnightly\"")));

        view.Fields.Single(f => f.Key == "schedule").Value.ShouldBe("fortnightly");

        JobEditorModel.SaveArgs(
                null, "iis", isNew: false, view,
                new Dictionary<string, string?> { ["schedule"] = "fortnightly" })
            .ShouldBe(["job", "set", "iis"]);
    }

    /// <summary>
    /// A quoted value is decoded the way the binder reads it, whichever quotes it wears.
    /// </summary>
    /// <remarks>
    /// TOML has two string spellings. A literal <c>'D:\archive'</c> was shown with its quotes on,
    /// because only <c>"…"</c> was stripped; and a basic string's escapes were shown raw, so
    /// <c>"D:\\archive"</c> appeared with two backslashes and a person who "fixed" it to one
    /// would have written a change to a file that already said that.
    /// </remarks>
    [Theory]
    [InlineData("'D:\\archive'", "D:\\archive")]
    [InlineData("\"D:\\\\archive\"", "D:\\archive")]
    [InlineData("\"say \\\"hi\\\"\"", "say \"hi\"")]
    [InlineData("\"100M\"", "100M")]
    public void AQuotedValueIsDecodedAsTheBinderReadsIt(string source, string shown) =>
        JobEditorModel.From(ShownWith(("olddir", source)))
            .Fields.Single(f => f.Key == "olddir").Value.ShouldBe(shown);

    /// <summary>
    /// A list handed back with the line endings a text box uses is not a change.
    /// </summary>
    /// <remarks>
    /// The model shows a list joined with <c>\n</c>; a multiline WinForms box hands back
    /// <c>\r\n</c>. Compared as text they differ on every line, so every path was re-sent, Save
    /// raised a UAC prompt for a file the operator had not touched, and the CLI then said
    /// Unchanged - a prompt for nothing, on every save of every job with a paths list.
    /// </remarks>
    [Fact]
    public void AListHandedBackWithWindowsLineEndingsIsNotAChange()
    {
        var view = Loaded();
        var edited = view.Fields.ToDictionary(f => f.Key, f => f.Value);

        edited["paths"] = "C:/logs/*.log\r\nD:/b/*.log\r\n";

        JobEditorModel.SaveArgs(null, "iis", isNew: false, view, edited)
            .ShouldBe(["job", "set", "iis"]);
    }

    /// <summary>
    /// A hook of several commands is one line each, and comes back one <c>--set</c> each.
    /// </summary>
    /// <remarks>
    /// The grid's value cell was single-line, so <c>prerotate = ["a", "b"]</c> was drawn as one
    /// line and could not be given a second; the cell wraps now, and hands its lines back with
    /// Windows line endings. That is not a change, and the Source column has to know it as well
    /// as Save does - it compared the text and said "will be set here" about an untouched hook.
    /// </remarks>
    [Fact]
    public void AHookOfSeveralCommandsIsOneLineEachAndComesBackOneSetEach()
    {
        var view = JobEditorModel.From(
            ShownWith(("prerotate", "[\"C:/tools/quiesce.exe\", \"C:/tools/flush.exe\"]")));

        var field = view.Fields.Single(f => f.Key == "prerotate");
        field.Value.ShouldBe("C:/tools/quiesce.exe\nC:/tools/flush.exe");

        JobEditorModel.Unchanged(field, "C:/tools/quiesce.exe\r\nC:/tools/flush.exe\r\n").ShouldBeTrue();
        JobEditorModel.Unchanged(field, "C:/tools/quiesce.exe").ShouldBeFalse();

        var edited = view.Fields.ToDictionary(f => f.Key, f => f.Value);
        edited["prerotate"] = "C:/tools/quiesce.exe\r\nC:/tools/flush.exe\r\nC:/tools/reload.exe";

        var args = JobEditorModel.SaveArgs(null, "iis", isNew: false, view, edited);

        args.ShouldBe(
        [
            "job", "set", "iis",
            "--set", "prerotate=C:/tools/quiesce.exe",
            "--set", "prerotate=C:/tools/flush.exe",
            "--set", "prerotate=C:/tools/reload.exe",
        ]);
        ShouldParse(args);
    }

    /// <summary>A scalar is trimmed before it is compared or sent.</summary>
    /// <remarks>
    /// <c>" 30"</c> is what a box holds after a stray space, and the verb refuses it as not a
    /// whole number. Trimmed, it is the number - and a space typed around an unchanged value is
    /// not a change either.
    /// </remarks>
    [Fact]
    public void AScalarIsTrimmedBeforeItIsComparedOrSent()
    {
        var view = Loaded();
        var edited = view.Fields.ToDictionary(f => f.Key, f => f.Value);

        edited["rotate"] = " 14 ";
        JobEditorModel.SaveArgs(null, "iis", isNew: false, view, edited).ShouldBe(["job", "set", "iis"]);

        edited["rotate"] = " 30";
        JobEditorModel.SaveArgs(null, "iis", isNew: false, view, edited)
            .ShouldBe(["job", "set", "iis", "--set", "rotate=30"]);
    }

    /// <summary>
    /// An explicit <c>enabled = true</c> is not unset by an untouched save.
    /// </summary>
    /// <remarks>
    /// The form's checkbox maps ticked to null, because absent means enabled and writing the
    /// second spelling of the default would put a line in the file nobody asked for. A file that
    /// already carries the explicit spelling then read as different from the box, and an untouched
    /// Save sent <c>--unset enabled</c>: a UAC prompt to delete a line that meant the same thing.
    /// Unticking still writes false, and ticking a disabled job still unsets - which is what
    /// makes enable a byte-exact undo of disable.
    /// </remarks>
    [Fact]
    public void AnExplicitEnabledTrueIsNotUnsetByAnUntouchedSave()
    {
        var explicitlyOn = JobEditorModel.From(ShownWith(("enabled", "true")));

        JobEditorModel.SaveArgs(
                null, "iis", isNew: false, explicitlyOn,
                new Dictionary<string, string?> { ["enabled"] = null })
            .ShouldBe(["job", "set", "iis"]);

        JobEditorModel.SaveArgs(
                null, "iis", isNew: false, explicitlyOn,
                new Dictionary<string, string?> { ["enabled"] = "false" })
            .ShouldBe(["job", "set", "iis", "--set", "enabled=false"]);

        var off = JobEditorModel.From(ShownWith(("enabled", "false")));

        JobEditorModel.SaveArgs(
                null, "iis", isNew: false, off,
                new Dictionary<string, string?> { ["enabled"] = null })
            .ShouldBe(["job", "set", "iis", "--unset", "enabled"]);
    }

    /// <summary>
    /// A <c>job show</c> that refused is reported as the refusal, not as a version mismatch.
    /// </summary>
    /// <remarks>
    /// A name that is not a job completes with no payload and its reason on the envelope: which
    /// names are. Reading <c>result</c> threw <c>KeyNotFoundException</c>, and the editor told the
    /// operator "the two are different versions" about a typo. Driven through the real verb and
    /// the real JSON sink, because that shape is the whole point.
    /// </remarks>
    [Fact]
    public void AShowThatRefusedIsReportedAsTheRefusal()
    {
        var dir = Directory.CreateTempSubdirectory("winlogrotate-editor-");

        try
        {
            Directory.CreateDirectory(Path.Combine(dir.FullName, "conf.d"));
            File.WriteAllText(Path.Combine(dir.FullName, "conf.d", "iis.toml"),
                "schema = 1\n\n[job]\nname = \"iis\"\npaths = [\"C:/logs/*.log\"]\n");

            var writer = new StringWriter();
            var ctx = new Cli.Commands.CommandContext(
                new Cli.Output.JsonOutputSink(verbose: false, stream: false, streamTo: writer),
                CommandTree.Build().Parse(["job", "show", "nginx", "--json"]));

            Cli.Commands.JobCommand.Show(ctx, "nginx", dir.FullName).ShouldBe(ExitCode.ConfigInvalid);

            var view = JobEditorModel.From(writer.ToString());

            view.Unreadable.ShouldBeFalse("a refusal is not an unreadable response");
            view.Fields.ShouldBeEmpty();

            var refusal = view.Refusal.ShouldNotBeNull();
            refusal.ShouldContain("nginx");
            view.Problems.ShouldContain(p => p.Contains("nginx", StringComparison.Ordinal));
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>A job that was read has no refusal to report.</summary>
    [Fact]
    public void AJobThatWasReadHasNoRefusal() => Loaded().Refusal.ShouldBeNull();

    /// <summary>
    /// Every key is reachable from the form: carried on it, common on it, or in the grid.
    /// </summary>
    /// <remarks>
    /// <c>allowdangerous</c> was in none of the three. It is structural - a job's own, with no
    /// <c>[defaults]</c> layer - so the form skipped it along with name, paths, kind and enabled,
    /// which have controls of their own; it had none. The only escape hatch from a guard refusal
    /// could be neither seen nor edited from the window that promised to edit jobs.
    /// </remarks>
    [Fact]
    public void EveryKeyIsReachableFromTheForm()
    {
        var view = Loaded();
        var sections = JobEditorModel.Sections(view);
        var sectioned = sections.SelectMany(s => s.Fields).Select(f => f.Key).ToArray();

        sectioned.ShouldContain("allowdangerous");
        sectioned.ShouldContain("kind", "who rotates is asked in Basics and offered in Advanced");
        sectioned.ShouldNotContain("ownr", "a key this build does not know is a foreign key, not a setting");
        JobEditorModel.Foreign(view).Select(f => f.Key).ShouldBe(["ownr"]);

        // Header, sections and the hidden shorthands partition the schema; nothing is in two of
        // them, and nothing is in none of them.
        var header = JobEditorModel.Header;
        header.Intersect(sectioned, StringComparer.OrdinalIgnoreCase).ShouldBeEmpty();
        JobEditorModel.Shorthands.Intersect(sectioned, StringComparer.OrdinalIgnoreCase).ShouldBeEmpty();

        var reachable = header.Concat(sectioned).Concat(JobEditorModel.Shorthands)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        JobSchema.Keys.Select(k => k.Key).Where(k => !reachable.Contains(k))
            .ShouldBeEmpty("keys the form offers no way to see or edit");
        sectioned.ShouldBeUnique();

        // Basics is a subset of Advanced - a second place to see a value, never a third value.
        JobEditorModel.Basics.ShouldAllBe(k => sectioned.Contains(k, StringComparer.OrdinalIgnoreCase));

        // Sections come in the reference's order, and an empty one is not drawn.
        sections.Select(s => s.Group).ShouldBe(sections.Select(s => s.Group).Order());
        sections.ShouldAllBe(s => s.Fields.Count > 0);
    }

    /// <summary>A new job never sends <c>--unset</c>, because there is nothing to inherit again.</summary>
    /// <remarks>
    /// <c>job add</c> declares no such option, so a blank field on a new job that produced one
    /// would turn the first Save into a parse error. Pinned because the model's rule - a cleared
    /// field is an unset - makes it the natural mistake.
    /// </remarks>
    [Fact]
    public void ANewJobNeverEmitsUnset()
    {
        var args = JobEditorModel.SaveArgs(
            null, "nginx", isNew: true, JobEditorModel.Blank(),
            new Dictionary<string, string?>
            {
                ["paths"] = "C:/a/*.log",
                ["rotate"] = null,
                ["compress"] = "",
                ["olddir"] = "   ",
            });

        args.ShouldNotContain("--unset");
        args.ShouldBe(["job", "add", "nginx", "--set", "paths=C:/a/*.log"]);
        ShouldParse(args);
    }

    /// <summary>Whether a command line changes anything is read from the words the model emits.</summary>
    /// <remarks>
    /// <c>Changes</c> once also looked for <c>--paths</c>, which nothing here ever emitted - a
    /// list goes out as one <c>--set</c> per item. A dead branch in a predicate is a predicate
    /// nobody can tell is right.
    /// </remarks>
    [Fact]
    public void ChangesNamesOnlyWhatSaveArgsEmits()
    {
        JobEditorModel.Changes(["job", "set", "iis"]).ShouldBeFalse();
        JobEditorModel.Changes(["job", "set", "iis", "--set", "rotate=14"]).ShouldBeTrue();
        JobEditorModel.Changes(["job", "set", "iis", "--unset", "rotate"]).ShouldBeTrue();
        JobEditorModel.Changes(["job", "set", "iis", "--paths", "C:/a"]).ShouldBeFalse(
            "the model never emits --paths, so a predicate that answered to it answered to nothing");
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

    /// <summary>
    /// Showing a default does not write it.
    /// </summary>
    /// <remarks>
    /// The form has to pick something to display for a key the file does not set, and whatever it
    /// picks is what comes back when Save is pressed. If that is the default rather than nothing,
    /// every Save writes a line the operator never asked for - identical in meaning, and a
    /// permanent change to a file whose whole promise is that keys nobody named are untouched.
    /// The form says nothing for an unset key; this is what says that is the right thing for it
    /// to say.
    /// </remarks>
    [Fact]
    public void ShowingADefaultDoesNotWriteIt()
    {
        var view = Loaded();

        view.Fields.Single(f => f.Key == "kind").IsSet.ShouldBeFalse("the fixture does not set it");

        var untouched = JobEditorModel.SaveArgs(
            null, "iis", isNew: false, view,
            new Dictionary<string, string?> { ["kind"] = null, ["enabled"] = null });

        untouched.ShouldBe(["job", "set", "iis"]);

        // And choosing it deliberately does write it, or the field would do nothing at all.
        JobEditorModel.SaveArgs(
            null, "iis", isNew: false, view,
            new Dictionary<string, string?> { ["kind"] = "manage" })
            .ShouldBe(["job", "set", "iis", "--set", "kind=manage"]);
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
    /// Validate is save with the flag that writes nothing, and the one that answers in JSON.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Same arguments, same code path, same verdict. A form that validated through one route and
    /// saved through another would report a green tick and then fail - which is the failure mode
    /// an operator has no way to diagnose.
    /// </para>
    /// <para>
    /// <c>--json</c> is the second flag, and it changes the channel rather than the judgement:
    /// the dry run is unelevated and read from stdout, and without it the verb answered in prose
    /// with its diagnostics on standard error - so every Check read "could not read the
    /// response". The elevated save gets <c>--json-stream</c> from the runner instead.
    /// </para>
    /// </remarks>
    [Fact]
    public void ValidateIsSaveWithTheFlagsThatWriteNothingAndAnswerInJson()
    {
        var view = Loaded();
        var edited = view.Fields.ToDictionary(f => f.Key, f => f.Value);
        edited["rotate"] = "30";

        var save = JobEditorModel.SaveArgs(null, "iis", isNew: false, view, edited);
        var check = JobEditorModel.ValidateArgs(null, "iis", isNew: false, view, edited);

        check.ShouldBe([.. save, "--dry-run", "--json"]);
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

    // ---- the schedule contest ---------------------------------------------------------------

    /// <summary>
    /// A file that says <c>daily = true</c> shows daily on the schedule field, and says which key said it.
    /// </summary>
    /// <remarks>
    /// The binder gives one schedule to <c>schedule</c>, to each shorthand that is true and to
    /// <c>size</c>, last line wins. A form showing <c>schedule</c> alone showed an inherited
    /// daily beside a file that said <c>weekly = true</c>, and the shorthands themselves were
    /// five more rows saying one thing.
    /// </remarks>
    [Fact]
    public void AShorthandIsShownAsTheScheduleItSpells()
    {
        var view = JobEditorModel.From(ShownWith(("name", "\"iis\""), ("paths", "\"C:/l/*.log\""), ("daily", "true")));

        var schedule = view.Fields.Single(f => f.Key == "schedule");
        schedule.Value.ShouldBe("daily");
        schedule.Line.ShouldBe(6);
        schedule.Via.ShouldBe("daily");

        JobEditorModel.Sections(view).SelectMany(s => s.Fields).ShouldNotContain(f => f.Key == "daily");
    }

    [Fact]
    public void TheLastScheduleKeyInTheFileDecides()
    {
        var byDay = JobEditorModel.From(ShownWith(
            ("name", "\"iis\""), ("schedule", "\"weekly\""), ("daily", "true")));
        byDay.Fields.Single(f => f.Key == "schedule").Value.ShouldBe("daily");

        var bySize = JobEditorModel.From(ShownWith(
            ("name", "\"iis\""), ("schedule", "\"weekly\""), ("daily", "true"), ("size", "\"5M\"")));
        var schedule = bySize.Fields.Single(f => f.Key == "schedule");
        schedule.Value.ShouldBe("size");
        schedule.Via.ShouldBe("size");
    }

    [Fact]
    public void AFalseShorthandDecidesNothing()
    {
        var view = JobEditorModel.From(ShownWith(("name", "\"iis\""), ("daily", "false")));

        view.Fields.Single(f => f.Key == "schedule").IsSet.ShouldBeFalse();
    }

    [Fact]
    public void AnUntouchedScheduleReadFromAShorthandSendsNothing()
    {
        var view = JobEditorModel.From(ShownWith(("name", "\"iis\""), ("daily", "true")));

        var args = JobEditorModel.SaveArgs(null, "iis", isNew: false, view,
            new Dictionary<string, string?> { ["schedule"] = "daily" });

        JobEditorModel.Changes(args).ShouldBeFalse();
    }

    [Fact]
    public void ChangingTheScheduleRetiresEveryShorthandTheFileWrites()
    {
        var view = JobEditorModel.From(ShownWith(("name", "\"iis\""), ("daily", "true"), ("weekly", "false")));

        var args = JobEditorModel.SaveArgs(null, "iis", isNew: false, view,
            new Dictionary<string, string?> { ["schedule"] = "monthly" });

        args.ShouldBe(["job", "set", "iis", "--set", "schedule=monthly", "--unset", "daily", "--unset", "weekly"]);
        ShouldParse(args);
    }

    /// <summary>Clearing a schedule the file spells as a shorthand unsets the shorthand, not a key the file lacks.</summary>
    [Fact]
    public void ClearingAShorthandScheduleUnsetsTheShorthand()
    {
        var view = JobEditorModel.From(ShownWith(("name", "\"iis\""), ("daily", "true")));

        var args = JobEditorModel.SaveArgs(null, "iis", isNew: false, view,
            new Dictionary<string, string?> { ["schedule"] = null });

        args.ShouldBe(["job", "set", "iis", "--unset", "daily"]);
        ShouldParse(args);
    }

    [Fact]
    public void LeavingTheSizeScheduleClearsTheThreshold()
    {
        var view = JobEditorModel.From(ShownWith(("name", "\"iis\""), ("size", "\"5M\"")));
        view.Fields.Single(f => f.Key == "schedule").Value.ShouldBe("size");

        var args = JobEditorModel.SaveArgs(null, "iis", isNew: false, view,
            new Dictionary<string, string?> { ["schedule"] = "daily", ["size"] = null });

        args.ShouldBe(["job", "set", "iis", "--set", "schedule=daily", "--unset", "size"]);
        ShouldParse(args);
    }

    [Theory]
    [InlineData("size", true)]
    [InlineData("Size", true)]
    [InlineData("daily", false)]
    [InlineData(null, false)]
    public void SizeAppliesOnlyUnderTheSizeSchedule(string? shown, bool applies) =>
        JobEditorModel.SizeApplies(shown).ShouldBe(applies);

    // ---- what the form says beside a field ---------------------------------------------------

    [Fact]
    public void EverySectionedFieldHasAPresentation()
    {
        foreach (var field in JobEditorModel.Sections(Loaded()).SelectMany(s => s.Fields))
        {
            var row = JobSchema.Find(field.Key).ShouldNotBeNull();
            var shown = JobEditorModel.Presentation(field);

            shown.Caption.ShouldBe(row.Title);
            shown.Key.ShouldBe(row.Key);
            shown.Description.ShouldBe(row.Description);
            shown.Default.ShouldBe(row.Default);
            shown.Editor.ShouldBe(row.Kind switch
            {
                JobKeyKind.Flag => FieldEditor.Check,
                JobKeyKind.Enum => FieldEditor.Choice,
                JobKeyKind.Integer => FieldEditor.Number,
                JobKeyKind.TextList => FieldEditor.Lines,
                _ => FieldEditor.Text,
            });
        }
    }

    [Fact]
    public void ThePlaceholderIsTheDefaultWhenThereIsOne()
    {
        var view = Loaded();

        JobEditorModel.Presentation(view.Fields.Single(f => f.Key == "rotate")).Placeholder.ShouldBe("7");
        JobEditorModel.Presentation(view.Fields.Single(f => f.Key == "maxage")).Placeholder.ShouldBe("e.g. 90");
        JobEditorModel.Presentation(view.Fields.Single(f => f.Key == "maxage")).Unit.ShouldBe("days");
        JobEditorModel.Presentation(view.Fields.Single(f => f.Key == "compress")).InheritedChecked.ShouldBeTrue();
        JobEditorModel.Presentation(view.Fields.Single(f => f.Key == "ownr")).Description
            .ShouldBe("Not a setting this product reads.");
    }

    [Fact]
    public void TheSourceLabelSaysWhereAValueComesFrom()
    {
        var view = Loaded();
        var rotate = view.Fields.Single(f => f.Key == "rotate");
        var maxage = view.Fields.Single(f => f.Key == "maxage");
        var olddir = view.Fields.Single(f => f.Key == "olddir");
        var ownr = view.Fields.Single(f => f.Key == "ownr");

        JobEditorModel.SourceLabel(rotate, "14").ShouldBe("set here (line 6)");
        JobEditorModel.SourceLabel(rotate, "30").ShouldBe("will be set here");
        JobEditorModel.SourceLabel(rotate, "").ShouldBe("will inherit again");
        JobEditorModel.SourceLabel(maxage, null).ShouldBe("inherited (none)");
        JobEditorModel.SourceLabel(view.Fields.Single(f => f.Key == "compress"), null).ShouldBe("inherited (default true)");
        JobEditorModel.SourceLabel(olddir, "D:/a").ShouldBe("will be set here");
        JobEditorModel.SourceLabel(ownr, "team-web").ShouldBe("not a setting this product reads");

        var shorthand = JobEditorModel.From(ShownWith(("name", "\"iis\""), ("daily", "true")))
            .Fields.Single(f => f.Key == "schedule");
        JobEditorModel.SourceLabel(shorthand, "daily").ShouldBe("set here (line 5, as daily = true)");
    }

    /// <summary>
    /// A value the CLI would refuse is said beside the box, in the verb's own words, before it is sent.
    /// </summary>
    [Theory]
    [InlineData("rotate", "soon", "whole number")]
    [InlineData("compresstype", "bzip2", "one of")]
    [InlineData("hook_timeout", "a while", "duration")]
    [InlineData("maxsize", "big", "100M")]
    [InlineData("weekday", "9", "between 0 and 7")]
    [InlineData("retrycount", "0", "at least 1")]
    [InlineData("rotate", "-2", "at least -1")]
    [InlineData("dateformat", "-%Y%m%d", "strftime")]
    public void AValueTheCliWouldRefuseIsJudgedBeforeItIsSent(string key, string text, string says)
    {
        var field = Loaded().Fields.Single(f => f.Key == key);

        JobEditorModel.Judge(field, text).ShouldNotBeNull().ShouldContain(says, Case.Sensitive);
    }

    [Theory]
    [InlineData("rotate", "-1")]
    [InlineData("rotate", " 14 ")]
    [InlineData("weekday", "7")]
    [InlineData("dateformat", "-yyyyMMdd")]
    [InlineData("maxsize", "100M")]
    [InlineData("compresstype", "GZIP")]
    public void AValueTheCliWouldAcceptIsNotJudged(string key, string text) =>
        JobEditorModel.Judge(Loaded().Fields.Single(f => f.Key == key), text).ShouldBeNull();

    [Fact]
    public void ABlankValueIsNeverAProblemAndAForeignKeyIsNotJudged()
    {
        var view = Loaded();

        foreach (var field in view.Fields)
        {
            JobEditorModel.Judge(field, "").ShouldBeNull(field.Key);
            JobEditorModel.Judge(field, null).ShouldBeNull(field.Key);
        }

        JobEditorModel.Judge(view.Fields.Single(f => f.Key == "ownr"), "anything").ShouldBeNull();
    }

    [Fact]
    public void TheInlineJudgementIsTheVerbsOwnSentence()
    {
        JobSchema.TryParse("rotate", "soon", out _, out var problem).ShouldBeFalse();

        JobEditorModel.Judge(Loaded().Fields.Single(f => f.Key == "rotate"), "soon")
            .ShouldBe($"'soon' is {problem}");
    }

    // ---- what the Basics view stands for ------------------------------------------------------

    [Fact]
    public void ChoosingWhoRotatesWritesKindOnlyWhenItChanges()
    {
        JobEditorModel.KindValue(manage: false, original: null).ShouldBeNull("a new job keeping the first radio inherits");
        JobEditorModel.KindValue(manage: true, original: null).ShouldBe("manage");
        JobEditorModel.KindValue(manage: false, original: "manage").ShouldBe("rotate", "silence would leave the file as it was");
        JobEditorModel.KindValue(manage: false, original: "rotate").ShouldBe("rotate");

        var fresh = JobEditorModel.BasicsEdits(JobEditorModel.Blank(), new BasicsAnswers(false, null, null, null, null));
        var args = JobEditorModel.SaveArgs(null, "iis", isNew: true, JobEditorModel.Blank(),
            new Dictionary<string, string?>(fresh) { ["paths"] = "C:/logs/*.log" });

        args.ShouldBe(["job", "add", "iis", "--set", "paths=C:/logs/*.log"], "the shortest job that works");
        ShouldParse(args);
    }

    [Fact]
    public void AManagedJobLeavesTheRotateRowAlone()
    {
        var edits = JobEditorModel.BasicsEdits(
            JobEditorModel.Blank(), new BasicsAnswers(Manage: true, Schedule: "weekly", MaxSize: "100M", Rotate: null, MaxAge: null));

        var args = JobEditorModel.SaveArgs(null, "iis", isNew: true, JobEditorModel.Blank(),
            new Dictionary<string, string?>(edits) { ["paths"] = "C:/logs/*.log" });

        args.ShouldBe(["job", "add", "iis", "--set", "paths=C:/logs/*.log", "--set", "kind=manage"]);
        ShouldParse(args);
    }

    [Fact]
    public void ABasicsJobIsWrittenInSchemaOrder()
    {
        var edits = JobEditorModel.BasicsEdits(
            JobEditorModel.Blank(),
            new BasicsAnswers(Manage: false, Schedule: "weekly", MaxSize: "100M", Rotate: "14", MaxAge: "90"));

        var args = JobEditorModel.SaveArgs(null, "iis", isNew: true, JobEditorModel.Blank(),
            new Dictionary<string, string?>(edits) { ["paths"] = "C:/logs/*.log" });

        args.ShouldBe([
            "job", "add", "iis",
            "--set", "paths=C:/logs/*.log",
            "--set", "schedule=weekly",
            "--set", "rotate=14",
            "--set", "maxage=90",
            "--set", "maxsize=100M",
        ]);
        ShouldParse(args);
    }

    [Fact]
    public void TheDefaultChoiceIsTheEmptyValueBothWays()
    {
        JobEditorModel.DefaultChoice("daily").ShouldBe("(default: daily)");
        JobEditorModel.DefaultChoice(null).ShouldBe(JobEditorModel.InheritChoice);
        JobEditorModel.ChoiceValue("(default: daily)").ShouldBeNull();
        JobEditorModel.ChoiceValue(JobEditorModel.InheritChoice).ShouldBeNull();
        JobEditorModel.ChoiceValue("weekly").ShouldBe("weekly");
        JobEditorModel.ChoiceValue(null).ShouldBeNull();
    }

    [Fact]
    public void TheAdvancedLinkCountsWhatBasicsCannotShow()
    {
        // The fixture sets name, paths, rotate, maxsize and ownr: only ownr is beyond Basics.
        JobEditorModel.AdvancedSetCount(Loaded()).ShouldBe(1);
        JobEditorModel.AdvancedSetCount(JobEditorModel.Blank()).ShouldBe(0);

        JobEditorModel.AdvancedLinkText(0, showingAdvanced: false).ShouldBe("Advanced settings");
        JobEditorModel.AdvancedLinkText(3, showingAdvanced: false).ShouldBe("Advanced settings (3 set)");
        JobEditorModel.AdvancedLinkText(3, showingAdvanced: true).ShouldBe("Basic settings");
    }

    [Fact]
    public void AForeignKeyCanOnlyBeRemoved()
    {
        var args = JobEditorModel.SaveArgs(null, "iis", isNew: false, Loaded(),
            new Dictionary<string, string?> { ["ownr"] = null });

        args.ShouldBe(["job", "set", "iis", "--unset", "ownr"]);
        ShouldParse(args);
    }

    [Fact]
    public void EverySizeAndCountExampleInTheHintsParses()
    {
        foreach (var size in new[] { "100k", "10M", "1G" })
        {
            JobSchema.TryParse("maxsize", size, out _, out _).ShouldBeTrue(size);
        }

        JobSchema.TryParse("rotate", "7", out _, out _).ShouldBeTrue();
        JobEditorText.FilesHint.ShouldNotContain("glob", Case.Insensitive);
        JobEditorText.FilesHint.ShouldNotContain("pattern", Case.Insensitive);
    }
}
