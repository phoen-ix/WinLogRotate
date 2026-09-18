using Shouldly;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Core.Engine;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// <c>run --job</c> names a job that exists, or is told so.
/// </summary>
/// <remarks>
/// <c>RotationRunner</c> applied the filter by skipping every job whose name did not match, and
/// nothing checked that anything had. So <c>run --job iss</c> exited 0, printed "0 job(s)" and
/// raised nothing - the shape of a log that quietly stops being rotated, which is the outcome
/// this product most wants to avoid, produced by a slip at a prompt.
/// </remarks>
[Collection(RotationGateCollection.Name)]
public sealed class RunJobFilterTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-jobfilter-");

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    private string StatePath => Path.Combine(_dir.FullName, "state.json");

    /// <summary>The real verb, over a configuration holding these jobs, filtered to one name.</summary>
    /// <remarks>
    /// The jobs' paths are spelled for Windows and match nothing here, which is the point: what
    /// is asserted is settled before a file is touched.
    /// </remarks>
    private (int Exit, RecordingSink Sink) Run(string only, params (string Name, bool Enabled)[] jobs)
    {
        var conf = Directory.CreateDirectory(Path.Combine(_dir.FullName, "conf"));
        File.WriteAllText(Path.Combine(conf.FullName, "config.toml"), "schema = 1\n");
        var confd = Directory.CreateDirectory(Path.Combine(conf.FullName, "conf.d"));

        foreach (var (name, enabled) in jobs)
        {
            File.WriteAllText(Path.Combine(confd.FullName, $"{name}.toml"), $"""
                schema = 1
                [job]
                name      = "{name}"
                paths     = ["C:/logs/{name}/*.log"]
                missingok = true
                enabled   = {(enabled ? "true" : "false")}
                """);
        }

        var sink = new RecordingSink();
        var parse = Cli.Commands.CommandTree.Build().Parse(
            ["run", "--job", only, "--no-notify", "--no-event-log"]);

        var exit = Cli.Commands.RunCommand.Run(
            new Cli.Commands.CommandContext(sink, parse),
            new RunOptions { OnlyJob = only },
            conf.FullName,
            StatePath);

        return (exit, sink);
    }

    /// <summary>
    /// A name nobody configured is refused before anything runs, with the names that exist.
    /// </summary>
    /// <remarks>
    /// Exit 2 and <c>LR1007</c>, the same answer as a mistyped flag: the command line named
    /// something the verb cannot use, and nothing was attempted. The remedy lists the configured
    /// jobs so the slip can be seen, not guessed at.
    /// </remarks>
    [Fact]
    public void AJobNobodyConfiguredIsRefusedWithTheNamesThatExist()
    {
        var (exit, sink) = Run("iss", ("app", true), ("iis", true));

        exit.ShouldBe(ExitCode.ConfigInvalid, "nothing was attempted, which is what 2 says");

        var diagnostic = sink.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(DiagnosticCode.ArgumentUnusable);
        diagnostic.Severity.ShouldBe(Severity.Error);
        diagnostic.Message.ShouldContain("iss");

        var remedy = diagnostic.Remedy.ShouldNotBeNull();
        remedy.ShouldContain("app");
        remedy.ShouldContain("iis");

        File.Exists(StatePath).ShouldBeFalse("nothing ran, so no clock was written");
    }

    /// <summary>
    /// A disabled job named by hand is said to be disabled, and the run is not a failure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Disabling is a deliberate state, so this is a Warning on exit 0 - the same footing as the
    /// pause - and not a refusal. The person who typed the name is the one person who may not
    /// know the job is in that state, which is why it is said at all.
    /// </para>
    /// <para>
    /// Typed in the other case, because the runner matches names without regard to it, and a
    /// check in front of the runner that was stricter would refuse a name the run accepts.
    /// </para>
    /// </remarks>
    [Fact]
    public void ADisabledJobNamedByHandIsSaidToBeDisabled()
    {
        var (exit, sink) = Run("APP", ("app", false));

        exit.ShouldBe(ExitCode.Ok, "a disabled job is a deliberate state, not a failure");
        sink.Diagnostics.ShouldNotContain(
            d => d.Code == DiagnosticCode.ArgumentUnusable,
            "the runner matches names case-insensitively, and so must this");

        sink.Diagnostics.ShouldContain(
            d => d.Code == DiagnosticCode.JobSkipped && d.Job == "app",
            "the disabled job is named, in its configured spelling");

        var skipped = sink.Diagnostics.Single(d => d.Code == DiagnosticCode.JobSkipped && d.Job == "app");
        skipped.Severity.ShouldBe(Severity.Warning);
        skipped.Message.ShouldContain("disabled");
        skipped.Remedy.ShouldNotBeNull().ShouldContain("job enable app");
    }
}
