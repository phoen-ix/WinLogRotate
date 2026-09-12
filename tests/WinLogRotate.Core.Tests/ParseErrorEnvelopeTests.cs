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
public sealed class ParseErrorEnvelopeTests
{
    /// <summary>Runs the reporter with both streams captured, as a caller receives them.</summary>
    private static (int Exit, string Out, string Error) Report(params string[] args)
    {
        var parse = Cli.Commands.CommandTree.Build().Parse(args);
        parse.Errors.ShouldNotBeEmpty("the fixture has to be a parse error");

        var beforeOut = Console.Out;
        var beforeError = Console.Error;
        var captureOut = new StringWriter();
        var captureError = new StringWriter();

        try
        {
            Console.SetOut(captureOut);
            Console.SetError(captureError);
            return (ParseErrorReporter.Report(parse, args), captureOut.ToString(), captureError.ToString());
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
}
