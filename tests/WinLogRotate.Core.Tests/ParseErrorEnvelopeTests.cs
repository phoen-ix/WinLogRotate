using System.Text.Json;
using Shouldly;
using WinLogRotate.Cli.Output;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// A mistyped flag answers in the channel the caller asked for.
/// </summary>
/// <remarks>
/// <para>
/// `README.md` says "Everything speaks <c>--json</c> for scripting" and <c>EnvelopeDetails</c>
/// says a <c>--json</c> verb "writes everything to stdout and nothing to stderr". A parse error
/// was the exception: stderr and exit 2, no envelope at all, so <c>ConvertFrom-Json</c> failed
/// outright on the response to a typo - which is the first thing a script author hits.
/// </para>
/// <para>
/// The product routed around this twice rather than fixing it: <c>CliIdentity</c> and
/// <c>MainForm</c> both carried a paragraph explaining that appending <c>--config-dir</c>
/// short-circuits here and produces no envelope.
/// </para>
/// </remarks>
public sealed class ParseErrorEnvelopeTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-parse-");

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    /// <summary>Runs the reporter with both streams captured, as a caller receives them.</summary>
    private static (int Exit, string Out, string Error) Report(params string[] args)
    {
        var parse = Cli.Commands.CommandTree.Build().Parse(args);
        parse.Errors.ShouldNotBeEmpty("the fixture has to be a parse error");

        return Captured(() => ParseErrorReporter.Report(parse, args));
    }

    /// <summary>
    /// Runs the last resort the way <c>Program</c> reaches it: with the raw arguments, and with
    /// the parse where one was reached.
    /// </summary>
    private static (int Exit, string Out, string Error) Unhandled(
        Exception e, bool parsed, params string[] args)
    {
        var parse = parsed ? Cli.Commands.CommandTree.Build().Parse(args) : null;

        return Captured(() => UnhandledReporter.Report(e, args, parse));
    }

    private static (int Exit, string Out, string Error) Captured(Func<int> report)
    {
        var beforeOut = Console.Out;
        var beforeError = Console.Error;
        var captureOut = new StringWriter();
        var captureError = new StringWriter();

        try
        {
            Console.SetOut(captureOut);
            Console.SetError(captureError);
            return (report(), captureOut.ToString(), captureError.ToString());
        }
        finally
        {
            Console.SetOut(beforeOut);
            Console.SetError(beforeError);
        }
    }

    /// <summary>
    /// A parse error under --json is an envelope on stdout, and nothing on stderr.
    /// </summary>
    /// <remarks>
    /// The verb is the deepest command that did parse, so a script learns which verb it mistyped
    /// rather than only that something was wrong.
    /// </remarks>
    [Fact]
    public void AParseErrorUnderJsonIsAnEnvelope()
    {
        var (exit, stdout, stderr) = Report("run", "--dry-runn", "--json");

        exit.ShouldBe(ExitCode.ConfigInvalid, "nothing was attempted, which is what 2 says");
        stderr.ShouldBeEmpty("a --json verb writes nothing to stderr");

        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;

        root.GetProperty("ok").GetBoolean().ShouldBeFalse();
        root.GetProperty("exitCode").GetInt32().ShouldBe(ExitCode.ConfigInvalid);
        root.GetProperty("verb").GetString().ShouldBe("run");

        var diagnostic = root.GetProperty("diagnostics").EnumerateArray().ShouldHaveSingleItem();
        diagnostic.GetProperty("severity").GetString().ShouldBe(nameof(Severity.Error));
        diagnostic.GetProperty("message").GetString().ShouldNotBeNull().ShouldContain("--dry-runn");
        diagnostic.GetProperty("remedy").GetString().ShouldNotBeNull().ShouldContain("--help");
    }

    /// <summary><c>--json-stream</c> implies <c>--json</c>, here as everywhere else.</summary>
    [Fact]
    public void JsonStreamAlsoGetsAnEnvelope()
    {
        var (_, stdout, _) = Report("run", "--dry-runn", "--json-stream");

        Should.NotThrow(() => JsonDocument.Parse(stdout));
    }

    /// <summary>
    /// Without --json it is unchanged: stderr, exit 2, and nothing on stdout.
    /// </summary>
    /// <remarks>
    /// A person at a terminal is still the commonest caller of a mistyped command, and they are
    /// not being handed JSON.
    /// </remarks>
    [Fact]
    public void WithoutJsonItIsStillPlainText()
    {
        var (exit, stdout, stderr) = Report("run", "--dry-runn");

        exit.ShouldBe(ExitCode.ConfigInvalid);
        stdout.ShouldBeEmpty();
        stderr.ShouldContain("--dry-runn");
        stderr.ShouldContain("--help");
    }

    /// <summary>
    /// An unknown verb answers too, and does not send the caller to its help.
    /// </summary>
    /// <remarks>
    /// The remedy is the root's help, because there is no such verb to ask about - telling
    /// somebody to run <c>winlogrotate rotate-everything --help</c> would be sending them to a
    /// second error. The envelope's <c>verb</c> is the root command's own name, which comes from
    /// the assembly and so is not the same string under the test host as it is in the field;
    /// asserting it here would be pinning the test runner.
    /// </remarks>
    [Fact]
    public void AnUnknownVerbAnswersWithoutSendingTheCallerToItsHelp()
    {
        var (exit, stdout, _) = Report("rotate-everything", "--json");

        exit.ShouldBe(ExitCode.ConfigInvalid);

        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;

        root.GetProperty("ok").GetBoolean().ShouldBeFalse();

        var diagnostic = root.GetProperty("diagnostics").EnumerateArray().ShouldHaveSingleItem();
        diagnostic.GetProperty("message").GetString().ShouldNotBeNull()
            .ShouldContain("rotate-everything");
        diagnostic.GetProperty("remedy").GetString().ShouldNotBeNull()
            .ShouldNotContain("rotate-everything");
    }

    /// <summary>
    /// The verb is the one a person wrote, and so is the help they are sent to.
    /// </summary>
    /// <remarks>
    /// The reporter named the leaf command, so <c>host status --nope</c> answered with
    /// <c>verb: "status"</c> and the remedy <c>winlogrotate status --help</c> - a verb that does not
    /// exist, so the remedy for one error was a second one. Every other envelope carries the
    /// space-separated verb, and <c>docs/automation.md</c> says so.
    /// </remarks>
    [Fact]
    public void TheVerbIsTheOneAPersonWrote()
    {
        var (_, stdout, _) = Report("host", "status", "--nope", "--json");

        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;

        root.GetProperty("verb").GetString().ShouldBe("host status");

        var diagnostic = root.GetProperty("diagnostics").EnumerateArray().ShouldHaveSingleItem();
        diagnostic.GetProperty("remedy").GetString().ShouldNotBeNull()
            .ShouldContain("winlogrotate host status --help", Case.Sensitive, "the help that exists");
    }

    /// <summary>
    /// <c>--output</c> sends the envelope to the file, and nothing to stdout.
    /// </summary>
    /// <remarks>
    /// The one caller that passes <c>--output</c> is the GUI's elevated child, whose stdout the
    /// GUI cannot read. Its parse errors went there anyway, so the events file stayed empty and
    /// the dialog said "the configuration has errors" with nothing underneath - the shape the
    /// GUI is least able to explain, about a mistake in its own command line.
    /// </remarks>
    [Fact]
    public void OutputSendsTheEnvelopeToTheFile()
    {
        var file = Path.Combine(_dir.FullName, "events.ndjson");

        var (exit, stdout, stderr) = Report("run", "--dry-runn", "--json-stream", "--output", file);

        exit.ShouldBe(ExitCode.ConfigInvalid);
        stdout.ShouldBeEmpty("the envelope went to the file the caller named");
        stderr.ShouldBeEmpty();

        using var document = JsonDocument.Parse(File.ReadAllText(file));
        var root = document.RootElement;

        root.GetProperty("verb").GetString().ShouldBe("run");
        root.GetProperty("exitCode").GetInt32().ShouldBe(ExitCode.ConfigInvalid);
        root.GetProperty("diagnostics").EnumerateArray().ShouldHaveSingleItem()
            .GetProperty("message").GetString().ShouldNotBeNull().ShouldContain("--dry-runn");
    }

    /// <summary>
    /// A defect that escaped even the guard answers in JSON when JSON was asked for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The last resort wrote to stderr whatever the caller asked, so a script promised "one
    /// object on stdout" got nothing on stdout and exit 4 - the exact failure the guard was added
    /// to stop, surviving on the one path the guard cannot reach.
    /// </para>
    /// <para>
    /// The diagnostic is the guard's own: <c>LR1006</c>, the exception's type and message, so a
    /// defect reads the same whichever side of the guard it fell on.
    /// </para>
    /// </remarks>
    [Fact]
    public void ADefectUnderJsonIsAnEnvelope()
    {
        var (exit, stdout, stderr) = Unhandled(
            new InvalidOperationException("a torn thing"), parsed: true, "run", "--json");

        exit.ShouldBe(ExitCode.InternalError);
        stderr.ShouldBeEmpty("a --json verb writes nothing to stderr");

        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;

        root.GetProperty("ok").GetBoolean().ShouldBeFalse();
        root.GetProperty("exitCode").GetInt32().ShouldBe(ExitCode.InternalError);
        root.GetProperty("verb").GetString().ShouldBe("run");

        var diagnostic = root.GetProperty("diagnostics").EnumerateArray().ShouldHaveSingleItem();
        diagnostic.GetProperty("severity").GetString().ShouldBe(nameof(Severity.Error));
        diagnostic.GetProperty("code").GetString().ShouldBe(DiagnosticCode.InternalError);
        var message = diagnostic.GetProperty("message").GetString().ShouldNotBeNull();
        message.ShouldContain(nameof(InvalidOperationException), Case.Sensitive, "the type is the only structural clue");
        message.ShouldContain("a torn thing");
    }

    /// <summary>The same defect under <c>--output</c> reaches the file the GUI is tailing.</summary>
    [Fact]
    public void ADefectUnderOutputReachesTheFile()
    {
        var file = Path.Combine(_dir.FullName, "events.ndjson");

        var (exit, stdout, _) = Unhandled(
            new InvalidOperationException("a torn thing"), parsed: true,
            "run", "--json-stream", "--output", file);

        exit.ShouldBe(ExitCode.InternalError);
        stdout.ShouldBeEmpty();

        using var document = JsonDocument.Parse(File.ReadAllText(file));
        document.RootElement.GetProperty("exitCode").GetInt32().ShouldBe(ExitCode.InternalError);
    }

    /// <summary>
    /// A defect before the tree was built still answers, with nothing to name the verb from.
    /// </summary>
    /// <remarks>
    /// <c>CommandTree.Build</c> touches the machine while constructing the secret verb, so this
    /// is a real path. The verb is not asserted: it is the executable's name, which is not the
    /// same string under the test host as in the field.
    /// </remarks>
    [Fact]
    public void ADefectBeforeTheTreeWasBuiltStillAnswers()
    {
        var (exit, stdout, _) = Unhandled(
            new InvalidOperationException("no tree"), parsed: false, "run", "--json");

        exit.ShouldBe(ExitCode.InternalError);

        using var document = JsonDocument.Parse(stdout);
        document.RootElement.GetProperty("diagnostics").EnumerateArray().ShouldHaveSingleItem()
            .GetProperty("code").GetString().ShouldBe(DiagnosticCode.InternalError);
    }
}
