using System.CommandLine;
using Shouldly;
using WinLogRotate.Cli.Commands;
using WinLogRotate.Cli.Output;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Safety;
using WinLogRotate.Core.Secrets;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// Creating a job, which for twenty-eight milestones could only be done in Notepad.
/// </summary>
/// <remarks>
/// Elevation and ownership are Windows-only, so both arrive through a seam - the shape
/// <c>SecretCommandTests</c> established and for the same reason: without it, every refusal and
/// every diagnostic in the verb could only be exercised on a Windows runner, and a mistake in any
/// of them would surface minutes later in CI instead of instantly here.
/// </remarks>
public sealed class JobCommandTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-jobcmd-");

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    private sealed class Capturing : IOutputSink
    {
        private readonly List<CliDiagnostic> _diagnostics = [];

        public List<string> Lines { get; } = [];

        public object? Result { get; private set; }

        public bool Verbose => true;

        public IReadOnlyList<CliDiagnostic> Diagnostics => _diagnostics;

        public void Diagnostic(CliDiagnostic diagnostic) => _diagnostics.Add(diagnostic);

        public void Event(CliEvent evt) { }

        public void Line(string text) => Lines.Add(text);

        public int Complete<T>(string verb, int exitCode, T? result)
        {
            Result = result;
            return exitCode;
        }
    }

    private static readonly Func<bool> Elevated = () => true;
    private static readonly Func<bool> NotElevated = () => false;

    private string Root => _dir.FullName;

    private string ConfD => Path.Combine(Root, "conf.d");

    private (Capturing Sink, CommandContext Ctx) Context()
    {
        Directory.CreateDirectory(ConfD);
        var sink = new Capturing();
        return (sink, new CommandContext(sink, CommandTree.Build().Parse(["job", "add", "x"])));
    }

    private LoadedConfig Load() =>
        ConfigLoader.Load(
            InstallPaths.Resolve(Root),
            new PathGuard(new GuardOptions()),
            new UnknownSecretLookup(),
            quarantineBadFiles: false);

    private static IReadOnlyList<JobEdit> Edits(params (string Key, string? Value)[] pairs) =>
        pairs.Select(p => new JobEdit(p.Key, p.Value)).ToArray();

    /// <summary>
    /// A job can be created without hand-writing TOML.
    /// </summary>
    /// <remarks>
    /// The whole milestone in one assertion, and it ends at the loader rather than at the file:
    /// what matters is not that bytes were written but that a run would find a job there.
    /// </remarks>
    [Fact]
    public void AJobCanBeCreatedWithoutHandWritingToml()
    {
        var (sink, ctx) = Context();

        JobCommand.Add(ctx, "iis", Root,
            Edits(("paths", "C:/logs/*.log"), ("rotate", "14"), ("compress", "true")),
            dryRun: false, Elevated)
            .ShouldBe(ExitCode.Ok);

        var loaded = Load();

        loaded.Diagnostics.Where(d => d.Severity >= Severity.Error).ShouldBeEmpty();

        var job = loaded.Jobs.ShouldHaveSingleItem();
        job.Name.ShouldBe("iis");
        job.Paths.ShouldBe(["C:/logs/*.log"]);
        job.Rotate.ShouldBe(14);
        job.Compress.ShouldBeTrue();
    }

    /// <summary>
    /// The values are written as the types the binder reads back, not as quoted strings.
    /// </summary>
    /// <remarks>
    /// The outage this milestone opened with. <c>enabled = "false"</c> is a string where the
    /// binder wants a bool, and that error is not file-scoped - so the job is not disabled
    /// <i>and</i> every job on the machine stops rotating. Asserted against the file's own text,
    /// because a binder that happened to coerce would hide it.
    /// </remarks>
    [Fact]
    public void TheFileSaysWhatTheBinderExpectsToRead()
    {
        var (_, ctx) = Context();

        JobCommand.Add(ctx, "iis", Root,
            Edits(("paths", "C:/logs/*.log"), ("rotate", "14"), ("compress", "true"),
                  ("enabled", "false"), ("maxsize", "100M")),
            dryRun: false, Elevated)
            .ShouldBe(ExitCode.Ok);

        var text = File.ReadAllText(Path.Combine(ConfD, "iis.toml"));

        text.ShouldContain("rotate = 14");
        text.ShouldContain("compress = true");
        text.ShouldContain("enabled = false");
        text.ShouldContain("paths = [\"C:/logs/*.log\"]");

        // A size keeps the caller's spelling: "100M" is what GetSize reads and what the operator
        // will recognise, and 104857600 is neither.
        text.ShouldContain("maxsize = \"100M\"");
    }

    /// <summary>The write leaves nothing behind it.</summary>
    /// <remarks>
    /// The file is written as a temp sibling and moved, so conf.d briefly holds a second file -
    /// and <c>JobFiles.In</c> would not read a <c>.tmp</c>, but a person looking at the directory
    /// would wonder, and a backup would copy it.
    /// </remarks>
    [Fact]
    public void TheWriteLeavesNoTemporaryFileBehind()
    {
        var (_, ctx) = Context();

        JobCommand.Add(ctx, "iis", Root, Edits(("paths", "C:/logs/*.log")), dryRun: false, Elevated)
            .ShouldBe(ExitCode.Ok);

        Directory.GetFiles(ConfD).Select(Path.GetFileName).ShouldBe(["iis.toml"]);
    }

    /// <summary>A created file reads in schema order, so it looks like the reference.</summary>
    [Fact]
    public void AWrittenJobFileReadsInTheOrderTheDocumentationDoes()
    {
        var (_, ctx) = Context();

        // Named backwards on purpose.
        JobCommand.Add(ctx, "iis", Root,
            Edits(("compress", "true"), ("rotate", "14"), ("kind", "rotate"), ("paths", "C:/logs/*.log")),
            dryRun: false, Elevated)
            .ShouldBe(ExitCode.Ok);

        var keys = File.ReadAllLines(Path.Combine(ConfD, "iis.toml"))
            .Where(l => l.Contains('=', StringComparison.Ordinal) && !l.StartsWith("schema", StringComparison.Ordinal))
            .Select(l => l.Split('=')[0].Trim())
            .ToArray();

        keys.ShouldBe(["name", "paths", "kind", "rotate", "compress"]);
    }

    /// <summary>A name another file already has is refused, and nothing is written.</summary>
    /// <remarks>
    /// It has to be refused <i>before</i> the write rather than reported after it: a duplicate
    /// name is not file-scoped, so a second file carrying one stops every job on the machine.
    /// </remarks>
    [Fact]
    public void ATakenNameIsRefusedBeforeAnythingIsWritten()
    {
        Directory.CreateDirectory(ConfD);
        File.WriteAllText(Path.Combine(ConfD, "existing.toml"),
            "schema = 1\n\n[job]\nname = \"iis\"\npaths = [\"C:/a/*.log\"]\n");

        var (sink, ctx) = Context();

        JobCommand.Add(ctx, "iis", Root, Edits(("paths", "C:/logs/*.log")), dryRun: false, Elevated)
            .ShouldBe(ExitCode.ConfigInvalid);

        sink.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(DiagnosticCode.ArgumentUnusable);
        sink.Diagnostics[0].Remedy.ShouldNotBeNull().ShouldContain("existing.toml");

        Directory.GetFiles(ConfD).Select(Path.GetFileName).ShouldBe(["existing.toml"]);
    }

    /// <summary>
    /// Two names that sanitise to one file are refused rather than resolved with a suffix.
    /// </summary>
    /// <remarks>
    /// A file whose name does not match the job in it is how a conf.d stops being readable to the
    /// person maintaining it, and this is reachable the moment names come from a text box.
    /// </remarks>
    [Fact]
    public void TwoNamesThatSanitiseToOneFileAreRefused()
    {
        var (_, first) = Context();
        JobCommand.Add(first, "IIS: W3SVC1", Root, Edits(("paths", "C:/a/*.log")), dryRun: false, Elevated)
            .ShouldBe(ExitCode.Ok);

        File.Exists(Path.Combine(ConfD, "iis-w3svc1.toml")).ShouldBeTrue();

        var (sink, second) = Context();
        JobCommand.Add(second, "iis w3svc1", Root, Edits(("paths", "C:/b/*.log")), dryRun: false, Elevated)
            .ShouldBe(ExitCode.ConfigInvalid);

        sink.Diagnostics.ShouldHaveSingleItem().Remedy.ShouldNotBeNull().ShouldContain("iis-w3svc1.toml");
        Directory.GetFiles(ConfD).Length.ShouldBe(1);
    }

    /// <summary>A dry run judges the proposal and writes nothing.</summary>
    [Fact]
    public void ADryRunWritesNothing()
    {
        var (sink, ctx) = Context();

        JobCommand.Add(ctx, "iis", Root, Edits(("paths", "C:/logs/*.log")), dryRun: true, Elevated)
            .ShouldBe(ExitCode.Ok);

        Directory.GetFiles(ConfD).ShouldBeEmpty();

        var result = sink.Result.ShouldBeOfType<JobEditResult>();
        result.Written.ShouldBeFalse();
        result.Changes.Select(c => c.Key).ShouldBe(["name", "paths"]);
    }

    /// <summary>
    /// A dry run does not need administrator rights.
    /// </summary>
    /// <remarks>
    /// This is the GUI's validate call. A form that had to raise a UAC prompt to say a field was
    /// wrong would ask on every blur, and people asked that often stop reading what they agree to.
    /// </remarks>
    [Fact]
    public void ADryRunDoesNotNeedAdministratorRights()
    {
        var (sink, ctx) = Context();

        JobCommand.Add(ctx, "iis", Root, Edits(("paths", "C:/logs/*.log")), dryRun: true, NotElevated)
            .ShouldBe(ExitCode.Ok);

        sink.Diagnostics.ShouldBeEmpty();
    }

    /// <summary>
    /// An unelevated write is refused as such, and not as a defect.
    /// </summary>
    /// <remarks>
    /// <c>import</c> does not check, so the same situation there escapes as an
    /// <c>UnauthorizedAccessException</c> reported as <c>LR1006</c>, exit 4 - "This is a defect.
    /// Nothing about what was or was not done can be relied on" - about a machine refusing
    /// correctly. It is also the one code the GUI turns into a modal.
    /// </remarks>
    [Fact]
    public void AnUnelevatedWriteIsRefusedAsSuchAndNotAsADefect()
    {
        var (sink, ctx) = Context();

        JobCommand.Add(ctx, "iis", Root, Edits(("paths", "C:/logs/*.log")), dryRun: false, NotElevated)
            .ShouldBe(ExitCode.Errors);

        var refusal = sink.Diagnostics.ShouldHaveSingleItem();
        refusal.Code.ShouldBe(DiagnosticCode.NeedsAdministrator);
        refusal.Code.ShouldNotBe(DiagnosticCode.InternalError);
        refusal.Remedy.ShouldNotBeNull();

        Directory.GetFiles(ConfD).ShouldBeEmpty();
    }

    /// <summary>An unknown key is refused, with the nearest spelling.</summary>
    [Fact]
    public void AnUnknownKeyIsRefusedWithTheNearestSpelling()
    {
        var (sink, ctx) = Context();

        JobCommand.Add(ctx, "iis", Root,
            Edits(("paths", "C:/logs/*.log"), ("comprss", "true")), dryRun: false, Elevated)
            .ShouldBe(ExitCode.ConfigInvalid);

        sink.Diagnostics.ShouldHaveSingleItem().Remedy.ShouldBe("Did you mean 'compress'?");
        Directory.GetFiles(ConfD).ShouldBeEmpty();
    }

    /// <summary>
    /// A value of the wrong shape is refused, naming the key and showing one that works.
    /// </summary>
    /// <remarks>
    /// And every bad key is reported, not just the first: somebody correcting a form wants all of
    /// them at once, not one per round trip.
    /// </remarks>
    [Fact]
    public void EveryUnusableValueIsReportedAtOnce()
    {
        var (sink, ctx) = Context();

        JobCommand.Add(ctx, "iis", Root,
            Edits(("paths", "C:/logs/*.log"), ("rotate", "soon"), ("maxage", "a while"),
                  ("compresstype", "rar")),
            dryRun: false, Elevated)
            .ShouldBe(ExitCode.ConfigInvalid);

        sink.Diagnostics.Count.ShouldBe(3);
        sink.Diagnostics.ShouldAllBe(d => d.Code == DiagnosticCode.ArgumentUnusable);
        sink.Diagnostics.Select(d => d.Remedy).ShouldContain(r => r!.StartsWith("For example: rotate", StringComparison.Ordinal));
        sink.Diagnostics.Select(d => d.Message).ShouldContain(m => m.Contains("zip", StringComparison.Ordinal));
    }

    /// <summary>A repeated list key accumulates into one array rather than overwriting.</summary>
    /// <remarks>
    /// <c>JobSchema.TryParse</c> deliberately does not split on a separator, because a Windows
    /// path may contain any of them - so repeating the key is the only way to say "and also".
    /// </remarks>
    [Fact]
    public void ARepeatedListKeyAccumulates()
    {
        var (_, ctx) = Context();

        JobCommand.Add(ctx, "iis", Root,
            Edits(("paths", "C:/a/*.log"), ("paths", "C:/b/*.log")), dryRun: false, Elevated)
            .ShouldBe(ExitCode.Ok);

        Load().Jobs.ShouldHaveSingleItem().Paths.ShouldBe(["C:/a/*.log", "C:/b/*.log"]);
    }

    /// <summary>
    /// A job the validator refuses is reported and not written.
    /// </summary>
    /// <remarks>
    /// The proposal is judged by <c>ConfigLoader.Judge</c> - the loader's own pass - so what this
    /// verb refuses and what a run refuses cannot come apart.
    /// </remarks>
    [Fact]
    public void AJobTheValidatorRefusesIsNotWritten()
    {
        var (sink, ctx) = Context();

        JobCommand.Add(ctx, "iis", Root,
            Edits(("paths", "C:/logs/*.log"), ("rotate", "-2")), dryRun: false, Elevated)
            .ShouldBe(ExitCode.ConfigInvalid);

        sink.Diagnostics.ShouldNotBeEmpty();
        Directory.GetFiles(ConfD).ShouldBeEmpty();
    }

    /// <summary>A job with no name is refused before the configuration is even opened.</summary>
    [Fact]
    public void AJobWithNoNameIsRefused()
    {
        var (sink, ctx) = Context();

        JobCommand.Add(ctx, "   ", Root, Edits(("paths", "C:/logs/*.log")), dryRun: false, Elevated)
            .ShouldBe(ExitCode.ConfigInvalid);

        sink.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(DiagnosticCode.ArgumentUnusable);
    }

    /// <summary>
    /// The name is not a key an edit may change.
    /// </summary>
    /// <remarks>
    /// Renaming is not a key edit: the file is named after the job and the journal is keyed by
    /// the name, so half a rename is a job that silently stops being the job whose history
    /// anybody has.
    /// </remarks>
    [Fact]
    public void TheNameIsNotAKeyAnEditMayChange()
    {
        var (sink, ctx) = Context();

        JobCommand.Add(ctx, "iis", Root,
            Edits(("paths", "C:/logs/*.log"), ("name", "something-else")), dryRun: false, Elevated)
            .ShouldBe(ExitCode.ConfigInvalid);

        sink.Diagnostics.ShouldHaveSingleItem().Message.ShouldContain("name");
        Directory.GetFiles(ConfD).ShouldBeEmpty();
    }

    /// <summary>
    /// The written file is the one the loader reads, and the name in it is the name asked for.
    /// </summary>
    /// <remarks>
    /// A name that has to be sanitised to become a filename must not be sanitised on its way into
    /// the file: <c>IIS: W3SVC1</c> is the job's name and <c>iis-w3svc1.toml</c> is only where it
    /// lives.
    /// </remarks>
    [Fact]
    public void ASanitisedFilenameDoesNotSanitiseTheName()
    {
        var (_, ctx) = Context();

        JobCommand.Add(ctx, "IIS: W3SVC1", Root, Edits(("paths", "C:/logs/*.log")), dryRun: false, Elevated)
            .ShouldBe(ExitCode.Ok);

        Load().Jobs.ShouldHaveSingleItem().Name.ShouldBe("IIS: W3SVC1");
    }

    // ---- job set ------------------------------------------------------------------------

    /// <summary>
    /// A file a person wrote by hand, with everything in it that makes editing it risky.
    /// </summary>
    /// <remarks>
    /// A file this product created will always round-trip. The hazard is this one: a comment
    /// somebody argued for, a key the binder does not know, an <c>allowdangerous</c> entry, an
    /// inline comment on the very key being changed, and CRLF line endings.
    /// </remarks>
    private const string HandWritten =
        "# Rotates the W3SVC logs. Do NOT enable compress - the SIEM reads these live.\r\n"
        + "schema = 1\r\n"
        + "\r\n"
        + "[job]\r\n"
        + "name  = \"iis\"\r\n"
        + "paths = [\"C:/inetpub/logs/*.log\"]\r\n"
        + "rotate = 7   # two weeks was too much\r\n"
        + "maxage = 90\r\n"
        + "allowdangerous = [\"C:/inetpub/logs\"]\r\n"
        + "ownr = \"team-web\"\r\n";

    private string WriteHandWritten()
    {
        Directory.CreateDirectory(ConfD);
        var path = Path.Combine(ConfD, "iis.toml");
        File.WriteAllText(path, HandWritten);
        return path;
    }

    /// <summary>
    /// One key changes exactly one line, and every other byte survives.
    /// </summary>
    /// <remarks>
    /// The property the whole design exists for. Asserted as a line-by-line diff rather than as
    /// "the comment is still there", because the ways to lose a byte here are many and naming
    /// three of them would pin three of them.
    /// </remarks>
    [Fact]
    public void OneKeyChangesExactlyOneLine()
    {
        var path = WriteHandWritten();
        var (sink, ctx) = Context();

        JobCommand.Set(ctx, "iis", Root, Edits(("rotate", "14")), dryRun: false, Elevated)
            .ShouldBe(ExitCode.Ok, Why(sink));

        var before = HandWritten.Split("\r\n");
        var after = File.ReadAllText(path).Split("\r\n");

        after.Length.ShouldBe(before.Length, "no line was added or removed");

        var differing = before.Zip(after).Index()
            .Where(x => x.Item.First != x.Item.Second)
            .ToArray();

        differing.Length.ShouldBe(1);
        differing[0].Item.First.ShouldBe("rotate = 7   # two weeks was too much");
        differing[0].Item.Second.ShouldBe("rotate = 14   # two weeks was too much");
    }

    /// <summary>
    /// The line endings are the document's, not this platform's.
    /// </summary>
    /// <remarks>
    /// Separate from the diff above because it is the one difference a diff of split lines
    /// cannot see: rewriting CRLF as LF changes every line and nothing about the meaning, which
    /// is how a one-key edit turns into a whole-file change in somebody's version control.
    /// </remarks>
    [Fact]
    public void TheLineEndingsAreTheDocumentsOwn()
    {
        var path = WriteHandWritten();
        var (sink, ctx) = Context();

        JobCommand.Set(ctx, "iis", Root, Edits(("rotate", "14")), dryRun: false, Elevated)
            .ShouldBe(ExitCode.Ok, Why(sink));

        var text = File.ReadAllText(path);
        text.ShouldContain("\r\n");
        text.Replace("\r\n", string.Empty, StringComparison.Ordinal).ShouldNotContain("\n");
    }

    /// <summary>An unset key is removed, so the job inherits it from [defaults] again.</summary>
    /// <remarks>
    /// The rule the <c>[defaults]</c> decision needs: a cleared field means inherit again, never
    /// set to zero. Asserted through the loader, because the difference between the two is
    /// invisible in the file and decisive in the run.
    /// </remarks>
    [Fact]
    public void AnUnsetKeyInheritsAgainRatherThanBecomingZero()
    {
        File.WriteAllText(Path.Combine(Root, "config.toml"), "schema = 1\n\n[defaults]\nrotate = 4\n");
        WriteHandWritten();

        var (sink, ctx) = Context();

        JobCommand.Set(ctx, "iis", Root, Edits(("rotate", null)), dryRun: false, Elevated)
            .ShouldBe(ExitCode.Ok, Why(sink));

        File.ReadAllText(Path.Combine(ConfD, "iis.toml")).ShouldNotContain("rotate =");
        Load().Jobs.ShouldHaveSingleItem().Rotate.ShouldBe(4, "from [defaults], not 0");
    }

    /// <summary>A key that was already what was asked for is not written at all.</summary>
    /// <remarks>
    /// Rewriting to identical bytes would change the file's timestamp and its descriptor for
    /// nothing, and a backup tool would report a change that is not one.
    /// </remarks>
    [Fact]
    public void AKeyThatAlreadySaysThatIsNotWritten()
    {
        var path = WriteHandWritten();

        // Stamped rather than read back: a filesystem may report the write's own timestamp with
        // less resolution than it stores, and a test that is flaky about the thing it asserts is
        // worse than no test.
        var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);

        var (sink, ctx) = Context();

        JobCommand.Set(ctx, "iis", Root, Edits(("rotate", "7")), dryRun: false, Elevated)
            .ShouldBe(ExitCode.Ok, Why(sink));

        File.ReadAllText(path).ShouldBe(HandWritten);
        File.GetLastWriteTimeUtc(path).ShouldBe(stamp, "the file was not touched at all");

        var result = sink.Result.ShouldBeOfType<JobEditResult>();
        result.Written.ShouldBeFalse();
        result.Changes.ShouldHaveSingleItem().Kind.ShouldBe("Unchanged");
    }

    /// <summary>Unsetting a key that is not there is not an error.</summary>
    [Fact]
    public void UnsettingAKeyThatIsNotThereIsNotAnError()
    {
        var path = WriteHandWritten();
        var (sink, ctx) = Context();

        JobCommand.Set(ctx, "iis", Root, Edits(("compress", null)), dryRun: false, Elevated)
            .ShouldBe(ExitCode.Ok, Why(sink));

        File.ReadAllText(path).ShouldBe(HandWritten);
    }

    /// <summary>
    /// A job that is not there is refused, and the refusal says which jobs are.
    /// </summary>
    /// <remarks>
    /// The name in the file is the job's name and the filename is only where it lives, so
    /// somebody who guessed from a directory listing has guessed wrong and has no other way to
    /// find out.
    /// </remarks>
    [Fact]
    public void AJobThatIsNotThereIsRefusedAndSaysWhichAre()
    {
        WriteHandWritten();
        var (sink, ctx) = Context();

        JobCommand.Set(ctx, "nginx", Root, Edits(("rotate", "14")), dryRun: false, Elevated)
            .ShouldBe(ExitCode.ConfigInvalid);

        sink.Diagnostics.ShouldHaveSingleItem().Remedy.ShouldNotBeNull().ShouldContain("iis");
    }

    /// <summary>A job is not a duplicate of itself.</summary>
    /// <remarks>
    /// Without excluding its own file from the names in use, every edit would be refused for
    /// colliding with the job being edited.
    /// </remarks>
    [Fact]
    public void EditingAJobDoesNotReportItAsADuplicateOfItself()
    {
        WriteHandWritten();
        var (sink, ctx) = Context();

        JobCommand.Set(ctx, "iis", Root, Edits(("rotate", "14")), dryRun: false, Elevated)
            .ShouldBe(ExitCode.Ok, Why(sink));

        // The file's own warnings are forwarded and are not the point; nothing may claim the job
        // collides with itself, and nothing may be an error.
        sink.Diagnostics.ShouldAllBe(d => d.Severity < Severity.Error);
        sink.Diagnostics.ShouldNotContain(d => d.Message.Contains("already defined", StringComparison.Ordinal));
    }

    /// <summary>An edit that names nothing is refused rather than reported as a success.</summary>
    [Fact]
    public void AnEditThatNamesNothingIsRefused()
    {
        WriteHandWritten();
        var (sink, ctx) = Context();

        JobCommand.Set(ctx, "iis", Root, [], dryRun: false, Elevated)
            .ShouldBe(ExitCode.ConfigInvalid);

        sink.Diagnostics.ShouldHaveSingleItem().Remedy.ShouldNotBeNull().ShouldContain("--unset");
    }

    /// <summary>
    /// An edit that would make the job invalid is refused, and the file is left as it was.
    /// </summary>
    /// <remarks>
    /// The one that matters most: an editor that judged after writing would leave the operator
    /// with a broken job and a message about it.
    /// </remarks>
    [Fact]
    public void AnEditThatWouldBreakTheJobLeavesTheFileAsItWas()
    {
        var path = WriteHandWritten();
        var (sink, ctx) = Context();

        JobCommand.Set(ctx, "iis", Root, Edits(("rotate", "-2")), dryRun: false, Elevated)
            .ShouldBe(ExitCode.ConfigInvalid);

        sink.Diagnostics.ShouldNotBeEmpty();
        File.ReadAllText(path).ShouldBe(HandWritten);
    }

    /// <summary>An unknown key already in the file is left alone by an edit to another key.</summary>
    /// <remarks>
    /// The binder warns about <c>ownr</c> and ignores it; an editor that "cleaned up" what it
    /// did not recognise would delete somebody's annotation, and the warning is what tells them
    /// to fix it.
    /// </remarks>
    [Fact]
    public void AnUnknownKeyInTheFileSurvivesAnEditToAnother()
    {
        var path = WriteHandWritten();
        var (sink, ctx) = Context();

        JobCommand.Set(ctx, "iis", Root, Edits(("rotate", "14")), dryRun: false, Elevated)
            .ShouldBe(ExitCode.Ok, Why(sink));

        File.ReadAllText(path).ShouldContain("ownr = \"team-web\"");
    }

    /// <summary>
    /// A key the binder does not read can still be removed, and only removed.
    /// </summary>
    /// <remarks>
    /// The binder warns "'ownr' is not a [job] setting, and is ignored" with a remedy that says
    /// "Remove it", and until now there was no way to do that but Notepad - the product naming a
    /// problem and offering nothing. Writing such a key stays refused, because a key the binder
    /// ignores is a key that does nothing. It also means a file written by a newer schema can be
    /// tidied by an older build rather than being untouchable by it.
    /// </remarks>
    [Fact]
    public void AKeyTheBinderDoesNotReadCanStillBeRemoved()
    {
        var path = WriteHandWritten();
        var (sink, ctx) = Context();

        JobCommand.Set(ctx, "iis", Root, Edits(("ownr", null)), dryRun: false, Elevated)
            .ShouldBe(ExitCode.Ok, Why(sink));

        var after = File.ReadAllText(path);
        after.ShouldNotContain("ownr");
        after.ShouldContain("# Rotates the W3SVC logs", Case.Sensitive);
        after.ShouldContain("rotate = 7   # two weeks was too much", Case.Sensitive);

        sink.Result.ShouldBeOfType<JobEditResult>()
            .Changes.ShouldHaveSingleItem().Before.ShouldBe("\"team-web\"");
    }

    /// <summary>But writing one is still refused.</summary>
    [Fact]
    public void AKeyTheBinderDoesNotReadStillCannotBeWritten()
    {
        var path = WriteHandWritten();
        var (sink, ctx) = Context();

        JobCommand.Set(ctx, "iis", Root, Edits(("ownr", "team-db")), dryRun: false, Elevated)
            .ShouldBe(ExitCode.ConfigInvalid);

        sink.Diagnostics.ShouldNotBeEmpty();
        File.ReadAllText(path).ShouldBe(HandWritten);
    }

    /// <summary>And unsetting a key that is neither known nor present says both.</summary>
    [Fact]
    public void UnsettingAKeyThatIsNeitherKnownNorPresentIsRefused()
    {
        WriteHandWritten();
        var (sink, ctx) = Context();

        JobCommand.Set(ctx, "iis", Root, Edits(("teamm", null)), dryRun: false, Elevated)
            .ShouldBe(ExitCode.ConfigInvalid);

        sink.Diagnostics.ShouldHaveSingleItem().Message
            .ShouldContain("does not have it either");
    }

    // ---- job enable / disable ------------------------------------------------------------

    /// <summary>
    /// Enable is a byte-exact undo of disable.
    /// </summary>
    /// <remarks>
    /// <c>enabled</c> is a per-job key with no <c>[defaults]</c> layer beneath it, so absent and
    /// true are the same state and enable removes the key rather than writing the second spelling
    /// of a state the file already had. Otherwise a job's file would accumulate a line after every
    /// off-and-on, and the file would slowly record somebody's afternoon.
    /// </remarks>
    [Fact]
    public void EnableIsAByteExactUndoOfDisable()
    {
        var path = WriteHandWritten();
        var (sink, ctx) = Context();

        JobCommand.Switch(ctx, "iis", Root, on: false, dryRun: false, Elevated)
            .ShouldBe(ExitCode.Ok, Why(sink));

        File.ReadAllText(path).ShouldContain("enabled = false");
        Load().Jobs.ShouldHaveSingleItem().Enabled.ShouldBeFalse();

        var (back, again) = Context();

        JobCommand.Switch(again, "iis", Root, on: true, dryRun: false, Elevated)
            .ShouldBe(ExitCode.Ok, Why(back));

        File.ReadAllText(path).ShouldBe(HandWritten);
        Load().Jobs.ShouldHaveSingleItem().Enabled.ShouldBeTrue();
    }

    /// <summary>
    /// A disabled job is still there to be edited and enabled again.
    /// </summary>
    /// <remarks>
    /// Which is what makes disabling the reversible alternative <c>job remove</c> points at.
    /// The verbs resolve a name through <c>JobIndex</c>, which reads the files rather than the
    /// loaded configuration, so nothing about a job's state can make it unreachable to them.
    /// </remarks>
    [Fact]
    public void ADisabledJobCanStillBeFoundByName()
    {
        WriteHandWritten();
        var (_, off) = Context();
        JobCommand.Switch(off, "iis", Root, on: false, dryRun: false, Elevated).ShouldBe(ExitCode.Ok);

        var (sink, ctx) = Context();
        JobCommand.Set(ctx, "iis", Root, Edits(("rotate", "14")), dryRun: false, Elevated)
            .ShouldBe(ExitCode.Ok, Why(sink));
    }

    /// <summary>Enabling a job that is already on changes nothing and says so.</summary>
    [Fact]
    public void EnablingAJobThatIsAlreadyOnChangesNothing()
    {
        var path = WriteHandWritten();
        var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);

        var (sink, ctx) = Context();

        JobCommand.Switch(ctx, "iis", Root, on: true, dryRun: false, Elevated)
            .ShouldBe(ExitCode.Ok, Why(sink));

        File.GetLastWriteTimeUtc(path).ShouldBe(stamp);
        sink.Result.ShouldBeOfType<JobEditResult>().Written.ShouldBeFalse();
    }

    /// <summary>
    /// Disabling writes a bare false, not a quoted one.
    /// </summary>
    /// <remarks>
    /// The outage this milestone opened with, in the verb that would have caused it:
    /// <c>enabled = "false"</c> is a string where the binder wants a bool, so the job would not
    /// be disabled - and because that error is raised against the whole configuration rather than
    /// one file, every job on the machine would stop rotating. Two wrong answers from one
    /// keystroke.
    /// </remarks>
    [Fact]
    public void DisablingWritesABareFalse()
    {
        var path = WriteHandWritten();
        var (_, ctx) = Context();

        JobCommand.Switch(ctx, "iis", Root, on: false, dryRun: false, Elevated).ShouldBe(ExitCode.Ok);

        File.ReadAllText(path).ShouldContain("enabled = false", Case.Sensitive);
        File.ReadAllText(path).ShouldNotContain("\"false\"");

        var loaded = Load();
        loaded.Diagnostics.Where(d => d.Severity >= Severity.Error).ShouldBeEmpty();
        loaded.HasErrors.ShouldBeFalse("a disabled job must not stop the machine");
    }

    /// <summary>Switching a job that is not there is refused like any other verb.</summary>
    [Fact]
    public void SwitchingAJobThatIsNotThereIsRefused()
    {
        WriteHandWritten();
        var (sink, ctx) = Context();

        JobCommand.Switch(ctx, "nginx", Root, on: false, dryRun: false, Elevated)
            .ShouldBe(ExitCode.ConfigInvalid);

        sink.Diagnostics.ShouldHaveSingleItem().Remedy.ShouldNotBeNull().ShouldContain("iis");
    }

    /// <summary>A dry run reports the switch and writes nothing.</summary>
    [Fact]
    public void ADryRunSwitchWritesNothing()
    {
        var path = WriteHandWritten();
        var (sink, ctx) = Context();

        JobCommand.Switch(ctx, "iis", Root, on: false, dryRun: true, Elevated)
            .ShouldBe(ExitCode.Ok, Why(sink));

        File.ReadAllText(path).ShouldBe(HandWritten);
        sink.Result.ShouldBeOfType<JobEditResult>().Changes.ShouldHaveSingleItem().Key.ShouldBe("enabled");
    }

    // ---- job remove ------------------------------------------------------------------------

    /// <summary>
    /// Removal deletes the file, and says what was in it first.
    /// </summary>
    /// <remarks>
    /// It deletes rather than renaming aside. A kept-aside <c>iis.toml.removed</c> renamed back
    /// beside a live job of the same name is a duplicate name, and a duplicate name is not
    /// file-scoped: it stops every rotation on the machine. That would trade a loss the operator
    /// chose for an outage they did not.
    /// </remarks>
    [Fact]
    public void RemovalDeletesTheFileAndRecitesWhatItHeld()
    {
        var path = WriteHandWritten();
        var (sink, ctx) = Context();

        JobCommand.Remove(ctx, "iis", Root, dryRun: false, Elevated).ShouldBe(ExitCode.Ok, Why(sink));

        File.Exists(path).ShouldBeFalse();
        Directory.GetFiles(ConfD).ShouldBeEmpty();

        var result = sink.Result.ShouldBeOfType<JobEditResult>();
        result.Written.ShouldBeTrue();
        result.Changes.Select(c => c.Key).ShouldBe(["name", "paths", "rotate", "maxage", "allowdangerous"], ignoreOrder: true);
        result.Changes.ShouldAllBe(c => c.Kind == "Unset");
        result.Changes.First(c => c.Key == "rotate").Before.ShouldBe("7");
    }

    /// <summary>And it names the reversible alternative, because it has one.</summary>
    [Fact]
    public void RemovalNamesTheReversibleAlternative()
    {
        WriteHandWritten();
        var (sink, ctx) = Context();

        JobCommand.Remove(ctx, "iis", Root, dryRun: false, Elevated).ShouldBe(ExitCode.Ok);

        sink.Lines.ShouldContain(l => l.Contains("job disable", StringComparison.Ordinal));
    }

    /// <summary>A dry run deletes nothing.</summary>
    [Fact]
    public void ADryRunRemovalDeletesNothing()
    {
        var path = WriteHandWritten();
        var (sink, ctx) = Context();

        JobCommand.Remove(ctx, "iis", Root, dryRun: true, Elevated).ShouldBe(ExitCode.Ok, Why(sink));

        File.ReadAllText(path).ShouldBe(HandWritten);
        sink.Result.ShouldBeOfType<JobEditResult>().Written.ShouldBeFalse();
    }

    /// <summary>
    /// A job it cannot identify is refused, and nothing in the directory is touched.
    /// </summary>
    /// <remarks>
    /// The filename is not the job's name. A verb that fell back to deleting
    /// <c>&lt;name&gt;.toml</c> when it could not find the job would delete a file belonging to a
    /// job with a different name - which is exactly the case <c>JobFiles.NameFor</c> creates,
    /// since two names can sanitise to one filename.
    /// </remarks>
    [Fact]
    public void AJobItCannotIdentifyIsRefusedAndNothingIsTouched()
    {
        WriteHandWritten();
        var (sink, ctx) = Context();

        JobCommand.Remove(ctx, "nginx", Root, dryRun: false, Elevated).ShouldBe(ExitCode.ConfigInvalid);

        sink.Diagnostics.ShouldHaveSingleItem().Remedy.ShouldNotBeNull().ShouldContain("iis");
        Directory.GetFiles(ConfD).Select(Path.GetFileName).ShouldBe(["iis.toml"]);
    }

    /// <summary>
    /// A quarantined file is not a job, so no verb can name one.
    /// </summary>
    /// <remarks>
    /// <c>ConfigLoader</c> renames a file it cannot parse to <c>.toml.bad</c> and leaves it as
    /// forensic evidence. Deleting one because a job of that name used to live in it would
    /// destroy the only copy of what went wrong - and <c>JobFiles.In</c> is the single place that
    /// decides what a job file is, so this holds for every verb at once rather than for this one.
    /// </remarks>
    [Fact]
    public void AQuarantinedFileIsNotAJob()
    {
        Directory.CreateDirectory(ConfD);
        var bad = Path.Combine(ConfD, "iis.toml.bad");
        File.WriteAllText(bad, "schema = 1\n\n[job]\nname = \"iis\"\npaths = [\"C:/logs/*.log\"]\n");

        var (sink, ctx) = Context();

        JobCommand.Remove(ctx, "iis", Root, dryRun: false, Elevated).ShouldBe(ExitCode.ConfigInvalid);

        File.Exists(bad).ShouldBeTrue("the evidence is still there");
        sink.Diagnostics.ShouldHaveSingleItem().Remedy.ShouldNotBeNull().ShouldContain("no jobs");
    }

    /// <summary>An unelevated removal is refused before anything is deleted.</summary>
    [Fact]
    public void AnUnelevatedRemovalIsRefused()
    {
        var path = WriteHandWritten();
        var (sink, ctx) = Context();

        JobCommand.Remove(ctx, "iis", Root, dryRun: false, NotElevated).ShouldBe(ExitCode.Errors);

        sink.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(DiagnosticCode.NeedsAdministrator);
        File.Exists(path).ShouldBeTrue();
    }

    // ---- job show --------------------------------------------------------------------------

    /// <summary>
    /// What <c>show</c> prints, fed back through <c>set</c>, changes nothing.
    /// </summary>
    /// <remarks>
    /// The contract the verb exists for. If <c>show</c> normalised anything - a size to bytes, a
    /// duration to a TimeSpan, a default materialised - this round trip would rewrite the file,
    /// and a GUI built on it would rewrite the file every time somebody opened a job and pressed
    /// Save without typing.
    /// </remarks>
    [Fact]
    public void WhatShowPrintsFedBackThroughSetChangesNothing()
    {
        var path = WriteHandWritten();
        var (sink, ctx) = Context();

        JobCommand.Show(ctx, "iis", Root).ShouldBe(ExitCode.Ok, Why(sink));

        var shown = sink.Result.ShouldBeOfType<JobShowResult>();

        // Every key, including the one the binder does not read, and including the name.
        shown.Keys.Select(k => k.Key)
            .ShouldBe(["name", "paths", "rotate", "maxage", "allowdangerous", "ownr"]);

        // Each key says whether this product reads it, which is what tells an editor what it may
        // feed back: a key the binder ignores can be removed but not written.
        shown.Keys.Single(k => k.Key == "ownr").Known.ShouldBeFalse();
        shown.Keys.Where(k => k.Key != "ownr").ShouldAllBe(k => k.Known);

        var (back, again) = Context();

        // The name is refused from --set, which is itself part of the contract: an editor feeds
        // back what it may change, and identity is not among it.
        JobCommand.Set(again, "iis", Root,
            [.. shown.Keys.Where(k => k.Known && k.Key != "name")
                .Select(k => new JobEdit(k.Key, Unquoted(k.Source)))],
            dryRun: false, Elevated)
            .ShouldBe(ExitCode.Ok, Why(back));

        File.ReadAllText(path).ShouldBe(HandWritten, "not one byte");

        back.Result.ShouldBeOfType<JobEditResult>()
            .Changes.ShouldAllBe(c => c.Kind == "Unchanged");
    }

    /// <summary>Show carries no resolved value, even when [defaults] has one.</summary>
    /// <remarks>
    /// The hazard in one assertion. <c>config show</c> would report <c>rotate = 4</c> for a job
    /// whose file says nothing about it; an editor that wrote that back would sever the job from
    /// <c>[defaults]</c> permanently.
    /// </remarks>
    [Fact]
    public void ShowCarriesNoResolvedValue()
    {
        Directory.CreateDirectory(ConfD);
        File.WriteAllText(Path.Combine(Root, "config.toml"), "schema = 1\n\n[defaults]\nrotate = 4\ncompress = true\n");
        File.WriteAllText(Path.Combine(ConfD, "iis.toml"),
            "schema = 1\n\n[job]\nname = \"iis\"\npaths = [\"C:/logs/*.log\"]\n");

        var (sink, ctx) = Context();

        JobCommand.Show(ctx, "iis", Root).ShouldBe(ExitCode.Ok, Why(sink));

        var shown = sink.Result.ShouldBeOfType<JobShowResult>();
        shown.Keys.Select(k => k.Key).ShouldBe(["name", "paths"]);
        shown.Keys.ShouldNotContain(k => k.Key == "rotate");
        shown.Keys.ShouldNotContain(k => k.Key == "compress");
    }

    /// <summary>
    /// Show finds a job that will not load, which is the one somebody opened an editor to fix.
    /// </summary>
    /// <remarks>
    /// <c>ConfigLoader</c> drops a job with a validation error from <c>LoadedConfig.Jobs</c>, so
    /// <c>config show --json</c> does not list it. This reads the file directly, and reports the
    /// verdict as well - which is the reason they opened it.
    /// </remarks>
    [Fact]
    public void ShowFindsAJobThatWillNotLoad()
    {
        Directory.CreateDirectory(ConfD);
        File.WriteAllText(Path.Combine(ConfD, "iis.toml"),
            "schema = 1\n\n[job]\nname = \"iis\"\npaths = [\"C:/logs/*.log\"]\nrotate = -2\n");

        Load().Jobs.ShouldBeEmpty("the loader will not run it");

        var (sink, ctx) = Context();

        JobCommand.Show(ctx, "iis", Root).ShouldBe(ExitCode.ConfigInvalid);

        var shown = sink.Result.ShouldBeOfType<JobShowResult>();
        shown.Keys.Select(k => k.Key).ShouldContain("rotate");
        shown.Diagnostics.ShouldNotBeEmpty("and says why");
    }

    /// <summary>Show needs no administrator rights, because it writes nothing.</summary>
    /// <remarks>
    /// Which matters for the GUI: opening a job to look at it must not raise a UAC prompt.
    /// </remarks>
    [Fact]
    public void ShowNeedsNoAdministratorRights()
    {
        WriteHandWritten();
        var (sink, ctx) = Context();

        JobCommand.Show(ctx, "iis", Root).ShouldBe(ExitCode.Ok, Why(sink));
        sink.Diagnostics.ShouldAllBe(d => d.Code != DiagnosticCode.NeedsAdministrator);
    }

    /// <summary>
    /// A source value as the command line would carry it.
    /// </summary>
    /// <remarks>
    /// A shell strips the quotes around a scalar; an array is passed through, because
    /// <c>JobSchema.TryParse</c> reads one back as its items. This is what a GUI building a
    /// command line has to do, which is the point of asserting it here.
    /// </remarks>
    private static string Unquoted(string source) =>
        source.Length > 1 && source[0] == '"' && source[^1] == '"'
            ? source[1..^1]
            : source;

    /// <summary>What the verb said, for an assertion that would otherwise only report a number.</summary>
    private static string Why(Capturing sink) =>
        sink.Diagnostics.Count == 0
            ? "no diagnostic was raised"
            : string.Join(" | ", sink.Diagnostics.Select(d => $"{d.Code} {d.Message}"));
}
