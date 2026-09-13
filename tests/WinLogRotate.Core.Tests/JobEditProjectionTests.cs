using Shouldly;
using WinLogRotate.Cli.Commands;
using WinLogRotate.Cli.Output;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Gui.Cli;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// What the editor and the Jobs page say after a <c>job</c> verb has answered.
/// </summary>
/// <remarks>
/// <para>
/// Every refused edit read "Could not read the response from winlogrotate.exe." The pages fed
/// the answer of <c>job add</c>, <c>set</c>, <c>enable</c>, <c>disable</c> and <c>remove</c> to
/// <c>ConfigCheckProjection</c>, which requires <c>result.errors</c> and <c>result.warnings</c>;
/// a <c>JobEditResult</c> carries neither, and a refusal carries no <c>result</c> at all. The
/// dry runs made it worse by running without <c>--json</c>, so what reached the projection was
/// prose. Check with <c>rotate=abc</c> showed the generic sentence with the real reason under an
/// expander; a successful save discarded the "2 changes" the verb had counted.
/// </para>
/// <para>
/// Driven through the real verb and the real JSON sink rather than hand-written envelopes,
/// because the shape of a refusal - <c>result: null</c>, the reason on the envelope's own
/// diagnostics - is exactly what the old code got wrong, and a fixture would only have asserted
/// the shape its author believed in.
/// </para>
/// </remarks>
public sealed class JobEditProjectionTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-jobview-");

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    private static readonly Func<bool> Elevated = () => true;
    private static readonly Func<bool> NotElevated = () => false;

    private string Root => _dir.FullName;

    private string ConfD => Path.Combine(Root, "conf.d");

    /// <summary>The file a person wrote: a comment, an unknown key, and a key to change.</summary>
    private const string HandWritten =
        "# Rotates the W3SVC logs.\n"
        + "schema = 1\n"
        + "\n"
        + "[job]\n"
        + "name  = \"iis\"\n"
        + "paths = [\"C:/inetpub/logs/*.log\"]\n"
        + "rotate = 7\n"
        + "ownr = \"team-web\"\n";

    private void WriteHandWritten()
    {
        Directory.CreateDirectory(ConfD);
        File.WriteAllText(Path.Combine(ConfD, "iis.toml"), HandWritten);
    }

    /// <summary>
    /// Runs one job verb and hands back exactly what the GUI would hold afterwards.
    /// </summary>
    /// <remarks>
    /// The real <c>JsonOutputSink</c> over a string, so the envelope is the CLI's own - a refusal
    /// completes with a null payload and its reason on the envelope, which no capturing sink of a
    /// typed result can reproduce.
    /// </remarks>
    private CliResult Run(string verb, Func<CommandContext, int> run)
    {
        Directory.CreateDirectory(ConfD);

        var writer = new StringWriter();
        var sink = new JsonOutputSink(verbose: false, stream: false, streamTo: writer);
        var ctx = new CommandContext(sink, CommandTree.Build().Parse([.. verb.Split(' '), "x", "--json"]));

        var exit = run(ctx);

        return new CliResult { ExitCode = exit, StdOut = writer.ToString(), StdErr = "", Verb = verb };
    }

    private static IReadOnlyList<JobEdit> Edits(params (string Key, string? Value)[] pairs) =>
        pairs.Select(p => new JobEdit(p.Key, p.Value)).ToArray();

    /// <summary>
    /// A value the key cannot hold is refused with the CLI's own sentence, not a generic one.
    /// </summary>
    /// <remarks>
    /// The defect as an operator met it: Check with <c>rotate=abc</c>. The real reason - which
    /// key, and an example that works - was in the envelope the whole time.
    /// </remarks>
    [Fact]
    public void AValueTheKeyCannotHoldIsRefusedInTheClisOwnWords()
    {
        var view = JobEditProjection.From(Run("job add", ctx =>
            JobCommand.Add(ctx, "iis", Root, Edits(("paths", "C:/logs/*.log"), ("rotate", "abc")),
                dryRun: true, Elevated)));

        view.Tone.ShouldBe(CheckTone.Error);
        view.Message.ShouldNotContain("Could not read");

        // The verb's own sentence names the value, and its remedy names the key.
        view.Message.ShouldContain("'abc' is not a whole number");
        view.Details.ShouldContain("For example: rotate");
    }

    /// <summary>A job the validator would refuse to run is refused, and says why.</summary>
    [Fact]
    public void AJobTheValidatorRefusesIsRefusedAndSaysWhy()
    {
        var view = JobEditProjection.From(Run("job add", ctx =>
            JobCommand.Add(ctx, "iis", Root, Edits(("paths", "C:/logs/*.log"), ("rotate", "-2")),
                dryRun: true, Elevated)));

        view.Tone.ShouldBe(CheckTone.Error);
        view.Message.ShouldNotContain("Could not read");
        view.Details.ShouldNotBeEmpty();
    }

    /// <summary>
    /// A dry run that would be accepted says so, and says how much it would write.
    /// </summary>
    /// <remarks>
    /// Written is false and the count is not zero: that pair is what tells a Check apart from a
    /// Save that found nothing to do, and the sentence has to say which.
    /// </remarks>
    [Fact]
    public void ADryRunThatWouldBeAcceptedSaysSo()
    {
        WriteHandWritten();

        var view = JobEditProjection.From(Run("job set", ctx =>
            JobCommand.Set(ctx, "iis", Root, Edits(("rotate", "14")), dryRun: true, Elevated)));

        view.Tone.ShouldBe(CheckTone.Warning, "the file's own 'ownr' warning is forwarded");
        view.Written.ShouldBeFalse();
        view.Changes.ShouldBe(1);
        view.Message.ShouldContain("1 change");
        view.Message.ShouldContain("Nothing has been written");
        view.Message.ShouldContain("1 warning(s)");
        view.Details.ShouldContain("ownr");
    }

    /// <summary>An edit the file already says is not a change, and is not an error.</summary>
    [Fact]
    public void AnEditTheFileAlreadySaysIsNothingToChange()
    {
        Directory.CreateDirectory(ConfD);
        File.WriteAllText(Path.Combine(ConfD, "iis.toml"),
            "schema = 1\n\n[job]\nname = \"iis\"\npaths = [\"C:/logs/*.log\"]\nrotate = 7\n");

        var view = JobEditProjection.From(Run("job set", ctx =>
            JobCommand.Set(ctx, "iis", Root, Edits(("rotate", "7")), dryRun: false, Elevated)));

        view.Tone.ShouldBe(CheckTone.Clean);
        view.Written.ShouldBeFalse();
        view.Changes.ShouldBe(0);
        view.Message.ShouldContain("Nothing to change");
    }

    /// <summary>
    /// A save that wrote says what it wrote and where.
    /// </summary>
    /// <remarks>
    /// The feedback the old code discarded: the verb counted the changes and named the file, and
    /// the page said nothing at all.
    /// </remarks>
    [Fact]
    public void ASaveThatWroteSaysWhatItWroteAndWhere()
    {
        Directory.CreateDirectory(ConfD);
        File.WriteAllText(Path.Combine(ConfD, "iis.toml"),
            "schema = 1\n\n[job]\nname = \"iis\"\npaths = [\"C:/logs/*.log\"]\nrotate = 7\n");

        var view = JobEditProjection.From(Run("job set", ctx =>
            JobCommand.Set(ctx, "iis", Root, Edits(("rotate", "14"), ("compress", "true")),
                dryRun: false, Elevated)));

        view.Tone.ShouldBe(CheckTone.Clean);
        view.Written.ShouldBeTrue();
        view.Changes.ShouldBe(2);
        view.Message.ShouldBe("iis: 2 changes written to iis.toml.");
    }

    /// <summary>
    /// A job that is not there is refused with the list of jobs that are.
    /// </summary>
    /// <remarks>
    /// This envelope has <c>result: null</c>. It is the shape that threw
    /// <c>KeyNotFoundException</c> out of the old projection, so the assertion that matters is
    /// that the reason comes through rather than a sentence about the response.
    /// </remarks>
    [Fact]
    public void AJobThatIsNotThereIsRefusedWithTheOnesThatAre()
    {
        WriteHandWritten();

        var view = JobEditProjection.From(Run("job set", ctx =>
            JobCommand.Set(ctx, "nginx", Root, Edits(("rotate", "14")), dryRun: false, Elevated)));

        view.Tone.ShouldBe(CheckTone.Error);
        view.Message.ShouldContain("nginx");
        view.Message.ShouldNotContain("Could not read");
        view.Details.ShouldContain("iis");
    }

    /// <summary>An unelevated write is refused as such, in the verb's own words.</summary>
    [Fact]
    public void AnUnelevatedWriteIsRefusedAsSuch()
    {
        WriteHandWritten();

        var view = JobEditProjection.From(Run("job set", ctx =>
            JobCommand.Set(ctx, "iis", Root, Edits(("rotate", "14")), dryRun: false, NotElevated)));

        view.Tone.ShouldBe(CheckTone.Error);
        view.Message.ShouldContain("administrator", Case.Insensitive);
        view.Written.ShouldBeFalse();
    }

    /// <summary>Removal and switching have their own sentences, because they are not edits.</summary>
    /// <remarks>
    /// Over a file with nothing to warn about, so the sentence can be asserted whole. A file that
    /// carries a warning gets it appended, which <see cref="ADryRunThatWouldBeAcceptedSaysSo"/>
    /// pins separately.
    /// </remarks>
    [Fact]
    public void RemovalAndSwitchingSayWhatTheyDid()
    {
        Directory.CreateDirectory(ConfD);
        File.WriteAllText(Path.Combine(ConfD, "iis.toml"),
            "schema = 1\n\n[job]\nname = \"iis\"\npaths = [\"C:/logs/*.log\"]\nrotate = 7\n");

        JobEditProjection.From(Run("job disable", ctx =>
                JobCommand.Switch(ctx, "iis", Root, on: false, dryRun: false, Elevated)))
            .Message.ShouldBe("iis disabled.");

        JobEditProjection.From(Run("job enable", ctx =>
                JobCommand.Switch(ctx, "iis", Root, on: true, dryRun: false, Elevated)))
            .Message.ShouldBe("iis enabled.");

        var removed = JobEditProjection.From(Run("job remove", ctx =>
            JobCommand.Remove(ctx, "iis", Root, dryRun: false, Elevated)));

        removed.Written.ShouldBeTrue();
        removed.Message.ShouldBe("iis removed. iis.toml is gone.");
    }

    /// <summary>
    /// The envelope is found in the file an elevated child writes, behind whatever came first.
    /// </summary>
    /// <remarks>
    /// The elevated path hands over the whole NDJSON file as StdOut. A job verb emits no events
    /// today, but the reader must not depend on that: one progress line ahead of the envelope
    /// would otherwise turn every elevated save into "could not read the response".
    /// </remarks>
    [Fact]
    public void TheEnvelopeIsFoundBehindAStreamedEvent()
    {
        WriteHandWritten();

        var result = Run("job set", ctx =>
            JobCommand.Set(ctx, "iis", Root, Edits(("rotate", "14")), dryRun: false, Elevated));

        var streamed = result with
        {
            StdOut = """{"ts":"2026-09-11T03:00:00+00:00","op":"plan","phase":"plan","result":"ok"}""" + "\n"
                     + result.StdOut,
        };

        var view = JobEditProjection.From(streamed);

        view.Written.ShouldBeTrue();
        view.Message.ShouldContain("written to iis.toml");
    }

    /// <summary>
    /// A response with no envelope in it is reported as unreadable, with what there was.
    /// </summary>
    /// <remarks>
    /// A text answer - which is what every dry run used to produce - has its diagnostics on
    /// standard error, and that is what the details show. The sentence is generic because
    /// nothing better can be said; the point is that it is now the exception rather than the rule.
    /// </remarks>
    [Fact]
    public void AResponseWithNoEnvelopeIsReportedWithWhatThereWas()
    {
        var view = JobEditProjection.From(new CliResult
        {
            ExitCode = ExitCode.ConfigInvalid,
            StdOut = "iis was not written. Nothing has changed.\n",
            StdErr = "error: 'abc' is not a whole number. [LR1007]\n",
            Verb = "job set",
        });

        view.Tone.ShouldBe(CheckTone.Error);
        view.Message.ShouldContain("Could not read");
        view.Details.ShouldContain("not a whole number");
    }

    /// <summary>A verb that never ran is described as itself.</summary>
    [Fact]
    public void AVerbThatNeverRanIsDescribedAsItself()
    {
        var view = JobEditProjection.From(new CliResult
        {
            ExitCode = -1,
            StdOut = "",
            StdErr = "",
            Failure = CliFailure.UacDeclined,
            Verb = "job set",
        });

        view.Tone.ShouldBe(CheckTone.Error);
        view.Message.ShouldContain("Elevation was cancelled");
    }

    /// <summary>
    /// The dry run the editor sends answers in JSON, or there is nothing for this to read.
    /// </summary>
    /// <remarks>
    /// The other half of the defect. The projection can only read an envelope, and the validate
    /// arguments produced none; both halves have to hold for a Check to say anything useful.
    /// </remarks>
    [Fact]
    public void TheDryRunTheEditorSendsAnswersInJson()
    {
        var args = JobEditorModel.ValidateArgs(
            null, "iis", isNew: false, JobEditorModel.Blank(),
            new Dictionary<string, string?> { ["rotate"] = "14" });

        args.ShouldContain("--dry-run");
        args.ShouldContain("--json");
        CommandTree.Build().Parse([.. args, "--no-color"]).Errors.ShouldBeEmpty();
    }
}
