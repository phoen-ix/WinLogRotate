using System.Text.Json;
using Shouldly;
using WinLogRotate.Cli.Output;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Gui.Cli;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// What a failed elevated operation is able to tell the person who asked for it.
/// </summary>
/// <remarks>
/// Nothing, until this milestone. Three dialogs passed <c>CliResult.StdErr</c> as their details
/// and it was hardcoded to the empty string on the elevated path - correctly, because a "runas"
/// child cannot have its pipes redirected and has no stderr to read. The child said what was
/// wrong in its envelope, in the file it was told to write, and nothing opened it.
/// </remarks>
[Collection(RotationGateCollection.Name)]
public sealed class EnvelopeDetailsTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-details-");

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// A real failing run, streamed to a file exactly as the GUI asks for it, read back exactly
    /// as the GUI reads it.
    /// </summary>
    private string Streamed(string job)
    {
        File.WriteAllText(Path.Combine(_dir.FullName, "config.toml"), "schema = 1\n");
        var confd = Directory.CreateDirectory(Path.Combine(_dir.FullName, "conf.d"));
        File.WriteAllText(Path.Combine(confd.FullName, "a.toml"), job);

        var file = Path.Combine(_dir.FullName, "events.ndjson");
        var parse = Cli.Commands.CommandTree.Build().Parse(
            ["run", "--no-notify", "--json-stream", "--output", file, "--no-event-log",
             "--config-dir", _dir.FullName]);

        parse.Errors.ShouldBeEmpty();
        parse.Invoke();

        return File.Exists(file) ? File.ReadAllText(file) : string.Empty;
    }

    /// <summary>
    /// A configuration the child refused is explained, not merely reported as a failure.
    /// </summary>
    /// <remarks>
    /// The dialog used to read "The configuration has errors, so nothing was attempted." over an
    /// empty expander. The reason - which file, which line, and what to do - was in the envelope
    /// the whole time.
    /// </remarks>
    [Fact]
    public void AFailedRunExplainsItself()
    {
        var details = EnvelopeDetails.From(Streamed("""
            schema = 1
            [job]
            name  = "app"
            paths = ["relative/logs/*.log"]
            """));

        details.ShouldNotBeEmpty("the dialog had nothing to show");
        details.ShouldContain("error: ");
        details.ShouldContain("LR9002", Case.Sensitive);
    }

    /// <summary>A run with nothing to say says nothing, rather than an empty box.</summary>
    /// <remarks>
    /// The other half of the same defect: LrDialog builds an expander and a Copy button whenever
    /// its details are not null, and the empty string is not null.
    /// </remarks>
    [Fact]
    public void ARunWithNoDiagnosticsHasNoDetails()
    {
        EnvelopeDetails.From(Streamed("""
            schema = 1
            [job]
            name      = "app"
            kind      = "manage"
            paths     = ["C:/app/logs/*.log"]
            missingok = true
            """)).ShouldBeEmpty();
    }

    /// <summary>A torn or unreadable line does not take the explanation down with it.</summary>
    /// <remarks>
    /// The GUI sweeps this directory while the child may still be exiting, so a half-written
    /// last line is ordinary. A failure dialog that throws while explaining a failure is not.
    /// </remarks>
    [Fact]
    public void ATornLineIsSteppedOver()
    {
        var whole = Streamed("""
            schema = 1
            [job]
            name  = "app"
            paths = ["relative/logs/*.log"]
            """);

        EnvelopeDetails.From(whole + "{\"schema\":1,\"diagno")
            .ShouldBe(EnvelopeDetails.From(whole));
    }

    /// <summary>
    /// The details read the same as the terminal's, because they are the same renderer.
    /// </summary>
    /// <remarks>
    /// The CLI's text sink had this wording and the GUI had none. Two renderings would be two
    /// answers to "what exactly did it say?" - the first question in every support conversation.
    /// </remarks>
    [Fact]
    public void TheWordingIsTheCliSOwn()
    {
        var diagnostic = new CliDiagnostic
        {
            Severity = Severity.Error,
            Code = DiagnosticCode.DangerousPathRefused,
            Message = "a message",
            Path = @"C:\ProgramData\WinLogRotate\conf.d\a.toml",
            Line = 4,
            Column = 1,
            Remedy = "a remedy",
        };

        var envelope = JsonSerializer.Serialize(
            new CliEnvelope<EmptyResult>
            {
                Schema = 1,
                Product = "winlogrotate",
                Version = "0.0.0",
                Verb = "run",
                Ok = false,
                ExitCode = ExitCode.Errors,
                Diagnostics = [diagnostic],
            },
            typeof(CliEnvelope<EmptyResult>),
            Cli.CliJsonContext.Default);

        EnvelopeDetails.From(envelope).ShouldBe(CliDiagnosticText.Describe(diagnostic));
    }

    /// <summary>
    /// A diagnostic says where, what, and what to do about it.
    /// </summary>
    /// <remarks>
    /// The layout itself, not a comparison of the renderer with itself. The first version of
    /// <see cref="TheWordingIsTheCliSOwn"/> called <c>Describe</c> on both sides of its
    /// assertion, so deleting the remedy line entirely left it green - the same shape of hole
    /// this milestone keeps finding. Every line the CLI has ever printed for a diagnostic comes
    /// out of here now, which makes this the wording of the product's console output too.
    /// </remarks>
    [Fact]
    public void ADiagnosticSaysWhereWhatAndWhatToDo()
    {
        var located = new CliDiagnostic
        {
            Severity = Severity.Error,
            Code = DiagnosticCode.DangerousPathRefused,
            Message = "a message",
            Path = @"C:\conf.d\a.toml",
            Line = 4,
            Column = 1,
            Remedy = "a remedy",
        };

        CliDiagnosticText.Describe(located).ShouldBe(
            $@"error: C:\conf.d\a.toml(4,1): a message [{DiagnosticCode.DangerousPathRefused}]"
            + Environment.NewLine + "        a remedy");

        // No file, no position, nothing to suggest: the head alone, and no trailing blank line
        // where the remedy would have been.
        CliDiagnosticText.Describe(located with { Path = null, Line = null, Column = null, Remedy = null })
            .ShouldBe($"error: a message [{DiagnosticCode.DangerousPathRefused}]");

        // A file with no line: located to the file and not to a position inside it.
        CliDiagnosticText.Describe(located with { Line = null, Column = null, Remedy = null })
            .ShouldBe($@"error: C:\conf.d\a.toml: a message [{DiagnosticCode.DangerousPathRefused}]");
    }

    /// <summary>Every severity has the word the CLI has always printed for it.</summary>
    [Theory]
    [InlineData(Severity.Critical, "critical")]
    [InlineData(Severity.Error, "error")]
    [InlineData(Severity.Warning, "warning")]
    [InlineData(Severity.Info, "info")]
    public void EverySeverityHasItsWord(Severity severity, string word) =>
        CliDiagnosticText.Label(severity).ShouldBe(word);

    /// <summary>
    /// An unelevated verb that crashes explains itself too.
    /// </summary>
    /// <remarks>
    /// The defect. <c>EnvelopeDetails.From</c> was called from one place - the elevated path - so
    /// milestone 21's LR1006 reached four call sites and was lost on the other twelve, where
    /// Details was standard error and a <c>--json</c> verb writes nothing there. The operator got
    /// "Something went wrong that WinLogRotate did not anticipate." above an empty expander,
    /// which is the defect this type was written to remove, recreated for the new exit code.
    /// </remarks>
    [Fact]
    public void AnUnelevatedJsonVerbThatCrashesExplainsItself()
    {
        var blocker = Path.Combine(_dir.FullName, "blocker");
        File.WriteAllText(blocker, "not a directory");

        // The crash fixture milestone 21 established: state is saved after the run is journaled,
        // and saving creates the state file's directory.
        File.WriteAllText(Path.Combine(_dir.FullName, "config.toml"), "schema = 1\n");
        var confd = Directory.CreateDirectory(Path.Combine(_dir.FullName, "conf.d"));
        File.WriteAllText(Path.Combine(confd.FullName, "a.toml"), """
            schema = 1
            [job]
            name      = "app"
            kind      = "manage"
            paths     = ["C:/app/logs/*.log"]
            missingok = true
            """);

        var parse = Cli.Commands.CommandTree.Build().Parse(
            ["run", "--json", "--no-notify", "--no-event-log",
             "--config-dir", _dir.FullName,
             "--state", Path.Combine(blocker, "state.json")]);

        parse.Errors.ShouldBeEmpty();

        var before = Console.Out;
        var captured = new StringWriter();

        int exit;
        try
        {
            Console.SetOut(captured);
            exit = parse.Invoke();
        }
        finally
        {
            Console.SetOut(before);
        }

        exit.ShouldBe(ExitCode.InternalError);

        // Exactly what the GUI's unelevated path builds from a completed process.
        var result = new CliResult { ExitCode = exit, StdOut = captured.ToString(), StdErr = "" };

        result.IsDefect.ShouldBeTrue();
        result.Details.ShouldContain(DiagnosticCode.InternalError);
    }

    /// <summary>
    /// And a text verb still says what it put on standard error.
    /// </summary>
    /// <remarks>
    /// The fallback the envelope-first order depends on. Four of the GUI's invocations are text
    /// mode, where there is no envelope at all and the diagnostics go to stderr - so preferring
    /// the envelope must not mean ignoring the other channel.
    /// </remarks>
    [Fact]
    public void ATextVerbStillSaysWhatItPutOnStandardError()
    {
        var result = new CliResult
        {
            ExitCode = ExitCode.ConfigInvalid,
            StdOut = "some human-readable report",
            StdErr = "error: something went wrong [LR1003]",
        };

        result.Details.ShouldBe("error: something went wrong [LR1003]");
    }

    /// <summary>
    /// The last resort reaches the dialog too.
    /// </summary>
    /// <remarks>
    /// The inverse case: <c>UnhandledReporter</c> runs when the guard itself was not reached, so
    /// it writes to stderr and emits no envelope at all. Driven through the real reporter rather
    /// than a hand-written string, so this is the product's own output.
    /// </remarks>
    [Fact]
    public void TheLastResortReachesTheDialogToo()
    {
        var before = Console.Error;
        var captured = new StringWriter();

        int exit;
        try
        {
            Console.SetError(captured);
            exit = Cli.Output.UnhandledReporter.Report(new IOException("a torn thing"));
        }
        finally
        {
            Console.SetError(before);
        }

        var result = new CliResult { ExitCode = exit, StdOut = "", StdErr = captured.ToString() };

        result.IsDefect.ShouldBeTrue();
        result.Details.ShouldContain("IOException");
    }

    /// <summary>
    /// Only a defect interrupts.
    /// </summary>
    /// <remarks>
    /// A configuration with an error is exit 2 and belongs in a status line; turning that into a
    /// dialog would put one in front of the operator on every visit to the Jobs page. And the
    /// runner's own failures exit -1 - they are not the CLI's verdict on anything.
    /// </remarks>
    [Theory]
    [InlineData(ExitCode.Ok, false)]
    [InlineData(ExitCode.Errors, false)]
    [InlineData(ExitCode.ConfigInvalid, false)]
    [InlineData(ExitCode.LockHeld, false)]
    [InlineData(ExitCode.InternalError, true)]
    public void OnlyADefectInterrupts(int exitCode, bool expected)
    {
        new CliResult { ExitCode = exitCode, StdOut = "", StdErr = "" }
            .IsDefect.ShouldBe(expected);

        // Never the runner's own failures, whatever exit code they carry.
        new CliResult
        {
            ExitCode = exitCode,
            StdOut = "",
            StdErr = "",
            Failure = CliFailure.NotFound,
        }.IsDefect.ShouldBeFalse();
    }

    /// <summary>An empty details box is not a details box.</summary>
    /// <remarks>
    /// Asserted here rather than against LrDialog, which cannot be constructed on this leg.
    /// What the dialog does with the answer is one line; getting the answer right is the part
    /// that was wrong.
    /// </remarks>
    [Fact]
    public void NothingToSayIsNotSomethingToShow()
    {
        EnvelopeDetails.From(string.Empty).ShouldBeEmpty();
        EnvelopeDetails.From("\n\n").ShouldBeEmpty();
        EnvelopeDetails.From("not json at all").ShouldBeEmpty();
    }
}
