using System.Text.Json;
using Shouldly;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// What a verb says when it throws.
/// </summary>
/// <remarks>
/// <para>
/// Nothing, and worse than nothing. <c>RunCommand</c> had no try across fifteen unguarded I/O,
/// registry, ACL, mutex and network calls; none of the command tree's actions had one; Main had
/// none. What caught an escape instead was System.CommandLine's default handler, which printed a
/// stack-free line to stderr and returned exit 1 - the code this product defines as "the run
/// happened and something went wrong, and state was written". A crash before the rotation gate
/// was even taken reported itself to monitoring as a rotation that had happened.
/// </para>
/// <para>
/// The fixture is a real crash on a real product path, reached by the real entry point with no
/// seam anywhere: a non-dry run saves state unconditionally once the work is done, and saving
/// creates the state file's directory. Point <c>--state</c> inside an ordinary file and that
/// throws, on Linux and Windows alike - and it throws <i>after</i> the run has journaled
/// <c>run.end</c>, which is exactly the shape that used to be reported as a success.
/// </para>
/// </remarks>
[Collection(RotationGateCollection.Name)]
public sealed class SafetyNetTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-safety-");

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    private string Config()
    {
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

        return _dir.FullName;
    }

    /// <summary>
    /// A verb that throws, for a reason the product genuinely does not anticipate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This drove the real <c>run</c> verb against a state path whose parent was an ordinary
    /// file, which threw <c>IOException</c> out of the save. That is a machine condition - a full
    /// disk and a locked file arrive the same way - and milestone 27 catches it and reports
    /// <c>LR3105</c>, because the rotations really did happen and calling it "a defect in the
    /// product" was false in both directions.
    /// </para>
    /// <para>
    /// Every other fault that used to reach here has gone the same way: the journal that cannot
    /// be opened, the one that stops accepting entries, the secret store that cannot be written.
    /// That is the milestone working, and it leaves this class without a reachable crash to use -
    /// which is the right problem to have. So the fault is now handed to
    /// <see cref="Cli.Commands.CommandContext.Guarded"/> directly, which is this class's subject
    /// anyway: not that <c>run</c> crashes, but that a verb which does still leaves an envelope,
    /// an honest exit code and a closed file behind it.
    /// </para>
    /// <para>
    /// The two events are emitted first, through the real sink and into the real
    /// <c>--output</c> file, because the crash this guards against is the one that arrives after
    /// the work - and what the run reached has to survive it.
    /// </para>
    /// </remarks>
    private (int Exit, string[] Lines) Crash(string? output = null)
    {
        var file = output ?? Path.Combine(_dir.FullName, $"out-{Guid.NewGuid():N}.ndjson");

        var parse = Cli.Commands.CommandTree.Build().Parse(
            ["run", "--no-notify", "--no-event-log", "--config-dir", Config(),
             "--json-stream", "--output", file]);

        parse.Errors.ShouldBeEmpty();

        var exit = Cli.Commands.CommandContext.Guarded(parse, ctx =>
        {
            foreach (var operation in new[] { Op.RunStart, Op.RunEnd })
            {
                ctx.Output.Event(new CliEvent
                {
                    Ts = string.Empty,
                    Run = string.Empty,
                    Operation = operation,
                    Phase = Phase.Apply,
                    Result = OpResult.Ok,
                });
            }

            throw new IOException("the fault nobody anticipated");
        });

        return (exit, File.Exists(file) ? File.ReadAllLines(file) : []);
    }

    private static JsonElement Envelope(string[] lines) =>
        lines.Where(l => l.Length > 0)
            .Select(l => JsonDocument.Parse(l).RootElement)
            .Last(e => e.TryGetProperty("schema", out _));

    /// <summary>
    /// A verb that throws still produces an envelope, and an exit code that does not lie.
    /// </summary>
    /// <remarks>
    /// The whole milestone in one assertion. Before it: no envelope at all, and exit 1 - which
    /// the GUI renders as "Completed, but some files could not be rotated." for a process that
    /// did not complete anything of the sort.
    /// </remarks>
    [Fact]
    public void ACrashInAVerbIsAnEnvelopeAndAnHonestExitCode()
    {
        var (exit, lines) = Crash();

        exit.ShouldBe(ExitCode.InternalError);

        var envelope = Envelope(lines);
        envelope.GetProperty("verb").GetString().ShouldBe("run");
        envelope.GetProperty("ok").GetBoolean().ShouldBeFalse();
        envelope.GetProperty("exitCode").GetInt32().ShouldBe(ExitCode.InternalError);

        var diagnostic = envelope.GetProperty("diagnostics").EnumerateArray()
            .Where(d => d.GetProperty("code").GetString() == DiagnosticCode.InternalError)
            .ShouldHaveSingleItem();

        // The type name, because StackTraceSupport is off and it is the only structural clue
        // that survives to whoever reads this.
        diagnostic.GetProperty("message").GetString().ShouldNotBeNull()
            .ShouldStartWith("IOException: ");
    }

    /// <summary>
    /// And it does not claim a rotation happened.
    /// </summary>
    /// <remarks>
    /// Stated as its own assertion because it is the property that matters to an alert rule, and
    /// because reusing <c>ExitCode.Errors</c> was the obvious cheap alternative: 1 promises a run
    /// happened and that state was written, and the fixture crashes in the act of writing state.
    /// </remarks>
    [Fact]
    public void ACrashedRunIsNotReportedAsARotationThatHappened()
    {
        var (exit, _) = Crash();

        exit.ShouldNotBe(ExitCode.Errors, "1 promises a run happened and state was written");
        exit.ShouldNotBe(ExitCode.Ok);
        exit.ShouldNotBe(ExitCode.LockHeld);
    }

    /// <summary>
    /// The file the crash was writing to is closed by the time the verb returns.
    /// </summary>
    /// <remarks>
    /// The second symptom of the same defect, and the one that turned the Windows leg red: the
    /// --output writer was disposed only at the end of Complete, which an exception never
    /// reaches, so the handle outlived the verb and the next read of that file was a sharing
    /// violation. Asserted by opening exclusively, which .NET enforces on Unix through flock as
    /// well.
    /// </remarks>
    [Fact]
    public void TheFileTheCrashWroteToIsClosed()
    {
        var file = Path.Combine(_dir.FullName, "closed.ndjson");
        Crash(file);

        Should.NotThrow(
            () =>
            {
                using var exclusive = new FileStream(
                    file, FileMode.Open, FileAccess.Read, FileShare.None);
            },
            "the verb has returned and something still holds its --output file open");
    }

    /// <summary>
    /// Exactly one envelope reaches the stream.
    /// </summary>
    /// <remarks>
    /// This fixture crashes inside the engine, before the verb reaches its own Complete, so the
    /// guard's envelope is the only one - which is what this asserts. It does not exercise the
    /// sink's second-envelope guard; see <see cref="TheLastWordIsSaidOnce"/> for that, and for
    /// why the two are separate tests.
    /// </remarks>
    [Fact]
    public void OnlyOneEnvelopeReachesTheFile()
    {
        var (_, lines) = Crash();

        lines.Where(l => l.Length > 0)
            .Count(l => JsonDocument.Parse(l).RootElement.TryGetProperty("schema", out _))
            .ShouldBe(1);
    }

    /// <summary>
    /// A sink that has said its last word does not say another.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asserted against the sink rather than through a verb, deliberately and with the milestone's
    /// usual rule in mind. The rule exists because a sink-level test can pass while the wiring is
    /// broken; here the wiring is pinned by every other test in this class, and what is left is a
    /// property of the sink itself.
    /// </para>
    /// <para>
    /// The path that reaches it is real but awkward to stage: a verb's disposals run as its
    /// method unwinds, after its own Complete and still inside the guard's try, so a journal that
    /// fails to flush would have the guard complete an invocation that had already completed.
    /// Arranging a broken flush is more fixture than the assertion is worth.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheLastWordIsSaidOnce()
    {
        var written = new StringWriter();
        var sink = new Cli.Output.JsonOutputSink(verbose: false, stream: true, written);

        sink.Complete("run", ExitCode.Ok, new Cli.Output.EmptyResult());
        sink.Complete<Cli.Output.EmptyResult>("run", ExitCode.InternalError, null);

        written.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Count(l => JsonDocument.Parse(l).RootElement.TryGetProperty("schema", out _))
            .ShouldBe(1, "the first answer stands; a second would overwrite it in the reader");
    }

    /// <summary>
    /// The run's own account of what it reached is still there.
    /// </summary>
    /// <remarks>
    /// The guard must not swallow the stream. This crash happens after the work, so the events
    /// are the only record of what was done before it - and the diagnostic's remedy sends the
    /// reader to exactly them.
    /// </remarks>
    [Fact]
    public void WhatTheRunReachedBeforeTheCrashIsStillReported()
    {
        var (_, lines) = Crash();

        var operations = lines.Where(l => l.Length > 0)
            .Select(l => JsonDocument.Parse(l).RootElement)
            .Where(e => e.TryGetProperty("operation", out _))
            .Select(e => e.GetProperty("operation").GetString())
            .ToList();

        operations.ShouldContain(Op.RunStart);
        operations.ShouldContain(Op.RunEnd, "the crash came after the run had finished its work");
    }

    /// <summary>
    /// An --output file that cannot be opened is reported rather than thrown.
    /// </summary>
    /// <remarks>
    /// The context is built before the verb runs, so its own construction is outside anything the
    /// verb could guard. Nothing was opened in this case, so nothing leaks - but the caller still
    /// asked for JSON, and stdout is the one channel that is definitely there.
    /// </remarks>
    [Fact]
    public void AnOutputFileThatCannotBeOpenedIsReportedRatherThanCrashing()
    {
        var blocker = Path.Combine(_dir.FullName, "nodir");
        File.WriteAllText(blocker, "not a directory");

        var parse = Cli.Commands.CommandTree.Build().Parse(
            ["doctor", "--no-event-log", "--config-dir", Config(),
             "--json-stream", "--output", Path.Combine(blocker, "out.ndjson")]);

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

        var envelope = JsonDocument.Parse(captured.ToString().Trim()).RootElement;
        envelope.GetProperty("verb").GetString().ShouldBe("doctor");
        envelope.GetProperty("exitCode").GetInt32().ShouldBe(ExitCode.InternalError);
    }
}
