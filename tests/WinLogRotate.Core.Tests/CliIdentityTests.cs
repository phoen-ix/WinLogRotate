using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;
using WinLogRotate.Cli.Output;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Gui.Cli;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// Whether the window can tell what it is talking to.
/// </summary>
/// <remarks>
/// It could not. Four places said it compared the envelope's schema on start - one of them real
/// code, written to make <c>--version --json</c> produce an envelope for a reader that did not
/// exist - and a grep for "Schema" across both GUI projects returned nothing at all.
/// </remarks>
public sealed class CliIdentityTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-identity-");

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    /// <summary>A result carrying what the real verb actually wrote.</summary>
    private CliResult RealAnswer()
    {
        var file = Path.Combine(_dir.FullName, "version.ndjson");

        var parse = Cli.Commands.CommandTree.Build().Parse(
            [.. CliIdentity.Arguments, "--json-stream", "--output", file]);

        parse.Errors.ShouldBeEmpty("the probe's own command line must parse");
        var exit = parse.Invoke();

        return new CliResult { ExitCode = exit, StdOut = File.ReadAllText(file), StdErr = "" };
    }

    /// <summary>An envelope built by the CLI's own serializer, then altered at the root.</summary>
    private static CliResult Altered(Action<JsonObject> change)
    {
        var envelope = JsonSerializer.Serialize(
            new CliEnvelope<EmptyResult>
            {
                Schema = ProductInfo.ContractSchema,
                Product = ProductInfo.Name,
                Version = ProductInfo.Version,
                Verb = "version",
                Ok = true,
                ExitCode = 0,
                Diagnostics = [],
            },
            typeof(CliEnvelope<EmptyResult>),
            Cli.CliJsonContext.Default);

        var node = JsonNode.Parse(envelope)!.AsObject();
        change(node);

        return new CliResult { ExitCode = 0, StdOut = node.ToJsonString(), StdErr = "" };
    }

    /// <summary>
    /// The real verb, read by the real reader.
    /// </summary>
    /// <remarks>
    /// End to end and no seam: the CLI writes the envelope through its own serializer and the
    /// window's reader makes sense of it. If either side moves, this is what notices.
    /// </remarks>
    [Fact]
    public void TheRealVerbIsUnderstood()
    {
        var identity = CliIdentity.Inspect(RealAnswer());

        identity.Ok.ShouldBeTrue(identity.Describe());
        identity.Verdict.ShouldBe(CliIdentityVerdict.Ok);
        identity.Product.ShouldBe(ProductInfo.Name);
        identity.Schema.ShouldBe(ProductInfo.ContractSchema);
        identity.Describe().ShouldBeEmpty("there is nothing to say about a CLI we understand");
    }

    /// <summary>
    /// An envelope that carries no result still says who it is.
    /// </summary>
    /// <remarks>
    /// The test that earns reading the root rather than the payload. When a verb throws, the
    /// guard completes the invocation with <c>Complete&lt;EmptyResult&gt;(…, null)</c> - an
    /// envelope with no <c>result</c> at all, which still identifies the product and the schema.
    /// A reader that went to the payload would call that unreadable, in the one case where
    /// knowing what you are talking to matters most.
    /// </remarks>
    [Fact]
    public void AnEnvelopeWithNoResultStillIdentifiesItself()
    {
        var identity = CliIdentity.Inspect(Altered(node => node.Remove("result")));

        identity.Verdict.ShouldBe(CliIdentityVerdict.Ok);
        identity.Product.ShouldBe(ProductInfo.Name);
    }

    /// <summary>Something else of the same name is not mistaken for us.</summary>
    /// <remarks>
    /// Live, not hypothetical: <c>CliRunner.Resolve</c>'s last fallback is the bare name
    /// "winlogrotate.exe" resolved against PATH, so the window can be handed a stranger.
    /// </remarks>
    [Fact]
    public void SomethingThatIsNotOurProgramIsNotMistakenForIt()
    {
        var identity = CliIdentity.Inspect(Altered(node => node["product"] = "logrotate-ng"));

        identity.Verdict.ShouldBe(CliIdentityVerdict.Foreign);
        identity.Severity.ShouldBe(Severity.Error);
        identity.Describe().ShouldContain("logrotate-ng");
    }

    /// <summary>A mismatch says which of the two to update.</summary>
    /// <remarks>
    /// The direction is the only actionable part. A partial upgrade is how this happens, and it
    /// leaves the operator holding both halves with no way to tell which is behind.
    /// </remarks>
    [Fact]
    public void ASchemaMismatchSaysWhichWayRoundItIs()
    {
        var older = CliIdentity.Inspect(
            Altered(node => node["schema"] = ProductInfo.ContractSchema - 1));

        var newer = CliIdentity.Inspect(
            Altered(node => node["schema"] = ProductInfo.ContractSchema + 1));

        older.Verdict.ShouldBe(CliIdentityVerdict.SchemaMismatch);
        newer.Verdict.ShouldBe(CliIdentityVerdict.SchemaMismatch);

        older.Describe().ShouldContain("older");
        older.Describe().ShouldContain("Update winlogrotate.exe");

        newer.Describe().ShouldContain("newer");
        newer.Describe().ShouldContain("Update this window");

        // A display problem, not a reason to stop: every action still shells out and the child
        // validates its own arguments.
        older.Severity.ShouldBe(Severity.Warning);
    }

    /// <summary>Nothing usable is not the same as a mismatch.</summary>
    /// <remarks>
    /// Saying "your two halves disagree about the wire format" when the truth is "it said
    /// nothing" sends the operator to upgrade something that was never the problem.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("""{"product":"WinLogRotate"}""")]
    [InlineData("""{"schema":1}""")]
    [InlineData("""{"schema":"one","product":"WinLogRotate"}""")]
    public void AnUnreadableAnswerIsNotAMismatch(string stdout)
    {
        var identity = CliIdentity.Inspect(
            new CliResult { ExitCode = 0, StdOut = stdout, StdErr = "" });

        identity.Verdict.ShouldBe(CliIdentityVerdict.Unreadable);
        identity.Ok.ShouldBeFalse();
    }

    /// <summary>A missing executable is named as missing, not as unreadable.</summary>
    [Fact]
    public void AMissingExecutableIsNamedAsMissing()
    {
        var identity = CliIdentity.Inspect(new CliResult
        {
            ExitCode = -1,
            StdOut = "",
            StdErr = "",
            Failure = CliFailure.NotFound,
        });

        identity.Verdict.ShouldBe(CliIdentityVerdict.Missing);
        identity.Describe().ShouldContain("could not be found");
    }

    /// <summary>
    /// The probe is believed even when the verb exited badly.
    /// </summary>
    /// <remarks>
    /// The rule stated in <c>Inspect</c>: the verdict follows what the child said, not how it
    /// exited. An envelope identifies itself whatever the exit code, and a check that believed
    /// only successful runs would go blind exactly when something is wrong.
    /// </remarks>
    [Fact]
    public void AnAnswerIsBelievedWhateverTheExitCode()
    {
        var answer = RealAnswer() with { ExitCode = ExitCode.InternalError };

        CliIdentity.Inspect(answer).Verdict.ShouldBe(CliIdentityVerdict.Ok);
    }

    /// <summary>
    /// A configuration directory turns the probe into a parse error.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reason <c>CliIdentity.Arguments</c> exists and the reason the probe must not go
    /// through <c>CliArgs.For</c> - recorded as an outcome rather than as a comment somebody can
    /// delete. The root command declares no <c>--config-dir</c>, so appending one never reaches a
    /// handler: it short-circuits to <c>ParseErrorReporter</c> and returns exit 2. That reporter
    /// now answers in JSON when the caller asked for it, so an envelope <i>is</i> produced - but
    /// it is the root command's parse failure, carrying no version, so the probe is no better off.
    /// </para>
    /// <para>
    /// Driven through the real reporter, so the second half is the product's own output: what the
    /// window would then be handed reads as <c>Unreadable</c>, and an operator who launched the
    /// GUI with a directory would be told their CLI was broken.
    /// </para>
    /// </remarks>
    [Fact]
    public void AConfigDirectoryTurnsTheProbeIntoAParseError()
    {
        var wrong = Cli.Commands.CommandTree.Build()
            .Parse(CliArgs.For(_dir.FullName, CliIdentity.Arguments));

        wrong.Errors.ShouldNotBeEmpty("the root verb takes no --config-dir");

        var beforeError = Console.Error;
        var beforeOut = Console.Out;
        var capturedError = new StringWriter();
        var capturedOut = new StringWriter();

        int exit;
        try
        {
            Console.SetError(capturedError);
            Console.SetOut(capturedOut);
            exit = ParseErrorReporter.Report(wrong, CliArgs.For(_dir.FullName, CliIdentity.Arguments));
        }
        finally
        {
            Console.SetError(beforeError);
            Console.SetOut(beforeOut);
        }

        exit.ShouldBe(ExitCode.ConfigInvalid);

        // Both streams, as the GUI would receive them. The probe passes --json, so the parse
        // error now answers as an envelope rather than as bare stderr - and the point of this
        // test survives that: an envelope from the root command carries no version, so the window
        // still cannot learn what it is talking to.
        CliIdentity.Inspect(new CliResult
        {
            ExitCode = exit,
            StdOut = capturedOut.ToString(),
            StdErr = capturedError.ToString(),
        }).Verdict.ShouldBe(CliIdentityVerdict.Unreadable);

        // And the arguments the probe actually uses parse cleanly.
        Cli.Commands.CommandTree.Build().Parse(CliIdentity.Arguments).Errors.ShouldBeEmpty();
    }
}
