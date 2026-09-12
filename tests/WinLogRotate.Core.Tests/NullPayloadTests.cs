using System.Text.Json;
using Shouldly;
using WinLogRotate.Cli.Output;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// Verbs that succeeded without saying which of their outcomes had happened.
/// </summary>
/// <remarks>
/// <para>
/// <c>README.md</c> says "Everything speaks <c>--json</c> for scripting" and
/// <c>EnvelopeDetails</c> says a <c>--json</c> verb "writes everything to stdout". Both of these
/// wrote everything to a console line, which <c>JsonOutputSink.Line</c> discards with the comment
/// "the same information is carried as diagnostics and events" - and for a payload that is
/// neither, it was carried nowhere.
/// </para>
/// <para>
/// So <c>host pause</c> returned the same envelope whether it resumed rotations or found them
/// already running, and <c>host path</c> returned the same one whether it added a directory,
/// removed it, or found it already there.
/// </para>
/// </remarks>
public sealed class NullPayloadTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-payload-");

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    /// <summary>A sink that keeps the payload, which is the thing under test.</summary>
    private sealed class Capturing : IOutputSink
    {
        public object? Result { get; private set; }

        public List<string> Lines { get; } = [];

        public bool Verbose => false;

        public IReadOnlyList<CliDiagnostic> Diagnostics => [];

        public void Diagnostic(CliDiagnostic diagnostic)
        {
        }

        public void Event(CliEvent evt)
        {
        }

        public void Line(string text) => Lines.Add(text);

        public int Complete<T>(string verb, int exitCode, T? result)
        {
            Result = result;
            return exitCode;
        }
    }

    private (Capturing Sink, Cli.Commands.CommandContext Ctx) Context(params string[] args)
    {
        var sink = new Capturing();
        return (sink, new Cli.Commands.CommandContext(sink, Cli.Commands.CommandTree.Build().Parse(args)));
    }

    /// <summary>
    /// Resuming and finding nothing to resume are different answers.
    /// </summary>
    /// <remarks>
    /// Both are correct and both are exit 0. Only one of them changed anything, and a script
    /// reconciling state - "is this machine paused?" - was told the same thing either way.
    /// </remarks>
    [Fact]
    public void PauseSaysWhichOfItsOutcomesHappened()
    {
        var (paused, pauseCtx) = Context("host", "pause", "--for", "01:00:00");
        Cli.Commands.PauseCommand.Run(pauseCtx, "01:00:00", _dir.FullName).ShouldBe(ExitCode.Ok);

        var pausedResult = paused.Result.ShouldBeOfType<PauseResult>();
        pausedResult.Action.ShouldBe("paused");
        pausedResult.PausedUntil.ShouldNotBeNull();

        var (resumed, resumeCtx) = Context("host", "pause");
        Cli.Commands.PauseCommand.Run(resumeCtx, null, _dir.FullName).ShouldBe(ExitCode.Ok);

        var resumedResult = resumed.Result.ShouldBeOfType<PauseResult>();
        resumedResult.Action.ShouldBe("resumed");
        resumedResult.PausedUntil.ShouldBeNull("nothing is paused any more");

        // And again, with nothing to resume.
        var (again, againCtx) = Context("host", "pause");
        Cli.Commands.PauseCommand.Run(againCtx, null, _dir.FullName).ShouldBe(ExitCode.Ok);

        again.Result.ShouldBeOfType<PauseResult>().Action.ShouldBe("unchanged");
    }

    /// <summary>
    /// The payload really reaches the wire, rather than only the sink.
    /// </summary>
    /// <remarks>
    /// A result type that is not in <c>CliJsonContext</c> serialises to nothing useful under
    /// NativeAOT, so the record existing is not the same as a caller being able to read it. This
    /// asserts the round trip that a script actually performs.
    /// </remarks>
    [Fact]
    public void ThePayloadSurvivesTheWire()
    {
        var envelope = new CliEnvelope<PathResult>
        {
            Schema = 1,
            Product = "WinLogRotate",
            Version = "0.0.0",
            Verb = "host path",
            Ok = true,
            ExitCode = ExitCode.Ok,
            Result = new PathResult
            {
                Directory = @"C:\Program Files\WinLogRotate",
                Scope = "Machine",
                Action = "added",
            },
            Diagnostics = [],
        };

        var json = JsonSerializer.Serialize(
            envelope, typeof(CliEnvelope<PathResult>), Cli.CliJsonContext.Default);

        using var document = JsonDocument.Parse(json);
        var result = document.RootElement.GetProperty("result");

        result.GetProperty("action").GetString().ShouldBe("added");
        result.GetProperty("scope").GetString().ShouldBe("Machine");
        result.GetProperty("directory").GetString().ShouldNotBeNullOrEmpty();
    }
}
