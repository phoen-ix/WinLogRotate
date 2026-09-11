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
