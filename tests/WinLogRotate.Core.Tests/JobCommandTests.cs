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
}
