using System.Diagnostics;
using Shouldly;
using WinLogRotate.Core.Hooks;
using WinLogRotate.Core.Notify;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// The three ways spawning a child process goes wrong, against real children.
/// </summary>
/// <remarks>
/// <para>
/// Nothing in this product had ever bounded or killed a child before this milestone, and the one
/// existing precedent is the wrong one to copy: <c>TaskRunHost.TryRun</c> reads stdout to the end,
/// then stderr to the end, then waits, and survives only because <c>schtasks</c> prints two lines.
/// </para>
/// <para>
/// Deliberately run on both legs. <c>WindowsHookHost</c> is annotated for Windows because of the
/// two Win32 primitives beside this one - service control and named events - but spawning a
/// process is portable, and the deadlock these tests exist to prevent is portable too. Running
/// them only where the annotation says would mean the leg that runs on every push never touched
/// the code path.
/// </para>
/// </remarks>
public sealed class HookProcessTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-proc-");

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    /// <summary>A shell, and the flag that makes it read a command from the next argument.</summary>
    private static (string Program, string Flag) Shell() =>
        OperatingSystem.IsWindows()
            ? (Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c")
            : ("/bin/sh", "-c");

    private static PlannedHook Hook(string script)
    {
        var (program, flag) = Shell();

        return new PlannedHook
        {
            Action = HookAction.Create(HookScheme.Command, $"command:{program}", program),
            Stage = HookStage.PostRotate,
            JobName = "app",
            Program = program,
            Arguments = [flag, script],
        };
    }

#pragma warning disable CA1416 // See the class remarks: the process path is portable, the annotation covers its neighbours.
    private static readonly WindowsHookHostShim Host = new();

    private sealed class WindowsHookHostShim
    {
        private readonly Hosting.Hooks.WindowsHookHost _inner = new();

        public HookOutcome Run(PlannedHook hook, TimeSpan timeout) => _inner.Run(hook, timeout);
    }
#pragma warning restore CA1416

    // ---- the deadlock ------------------------------------------------------------------------

    /// <summary>
    /// A hook that writes more than the pipe buffer holds still finishes.
    /// </summary>
    /// <remarks>
    /// The failure <c>TaskRunHost</c> still has. Under its pattern - read stdout to the end, then
    /// stderr, then wait - a child that fills the stderr buffer blocks in its own write while this
    /// side blocks in a read that will never complete, and the rotation stops for ever. Both pipes
    /// are drained on their own tasks, started before the wait, which is why this returns.
    /// </remarks>
    [Fact]
    public void AHookThatFloodsStdoutDoesNotDeadlockTheRun()
    {
        // Comfortably past the 64 KB a pipe buffer holds, on both.
        var script = OperatingSystem.IsWindows()
            ? "for /L %i in (1,1,4000) do @echo the quick brown fox jumped over the lazy dog again"
            : "i=0; while [ $i -lt 4000 ]; do echo the quick brown fox jumped over the lazy dog again; i=$((i+1)); done";

        var outcome = Host.Run(Hook(script), TimeSpan.FromSeconds(60));

        outcome.Result.ShouldBe(HookResult.Ok);
    }

    /// <summary>The same, on stderr, which is the pipe TaskRunHost reads second.</summary>
    [Fact]
    public void AHookThatFloodsStderrDoesNotDeadlockEither()
    {
        var script = OperatingSystem.IsWindows()
            ? "for /L %i in (1,1,4000) do @echo the quick brown fox jumped over the lazy dog again 1>&2"
            : "i=0; while [ $i -lt 4000 ]; do echo flooding stderr with a reasonably long line >&2; i=$((i+1)); done";

        Host.Run(Hook(script), TimeSpan.FromSeconds(60)).Result.ShouldBe(HookResult.Ok);
    }

    // ---- the timeout -------------------------------------------------------------------------

    /// <summary>
    /// A hook that outlives its timeout is killed.
    /// </summary>
    /// <remarks>
    /// Without this the child outlives the rotation, Task Scheduler reaches ExecutionTimeLimit and
    /// reports 0x41306 - indistinguishable from an operator pressing Stop, on a run that succeeded.
    /// </remarks>
    [Fact]
    public void AHookThatOutlivesItsTimeoutIsKilled()
    {
        var clock = Stopwatch.StartNew();
        var script = OperatingSystem.IsWindows() ? "ping -n 30 127.0.0.1 >nul" : "sleep 30";

        var outcome = Host.Run(Hook(script), TimeSpan.FromSeconds(1));

        outcome.Result.ShouldBe(HookResult.TimedOut);
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(20), "it must not have waited out the sleep");
        outcome.Detail.ShouldNotBeNull().ShouldContain("1s");
    }

    /// <summary>
    /// And so are its children.
    /// </summary>
    /// <remarks>
    /// <c>Process.Kill(entireProcessTree: true)</c> appears nowhere else in this repository. A hook
    /// that launches a helper and returns would otherwise leave the helper running - holding the
    /// pipe this side has stopped reading, and outliving the rotation it belonged to. Asserted by
    /// what the grandchild would have written, because a pid is gone by the time it is checked.
    /// </remarks>
    [Fact]
    public void AndItsChildrenAre()
    {
        var witness = Path.Combine(_dir.FullName, "grandchild-survived.txt");

        var script = OperatingSystem.IsWindows()
            ? $"start /b cmd /c \"ping -n 4 127.0.0.1 >nul & echo alive > \"\"{witness}\"\"\" & ping -n 20 127.0.0.1 >nul"
            : $"sh -c 'sleep 3; echo alive > \"{witness}\"' & sleep 20";

        Host.Run(Hook(script), TimeSpan.FromSeconds(1)).Result.ShouldBe(HookResult.TimedOut);

        // Long enough that a surviving grandchild would have written by now.
        Thread.Sleep(TimeSpan.FromSeconds(6));

        File.Exists(witness).ShouldBeFalse("the whole process tree must have been killed");
    }

    // ---- exit codes --------------------------------------------------------------------------

    /// <summary>A hook's own verdict is carried through, not flattened to "it failed".</summary>
    [Fact]
    public void ACommandHookReportsItsExitCode()
    {
        var outcome = Host.Run(Hook("exit 3"), TimeSpan.FromSeconds(30));

        outcome.Result.ShouldBe(HookResult.Failed);
        outcome.ExitCode.ShouldBe(3);
    }

    /// <summary>What a failing hook printed comes back with it, because that is the diagnosis.</summary>
    [Fact]
    public void WhatAFailingHookPrintedIsKept()
    {
        var script = OperatingSystem.IsWindows()
            ? "echo could not reach the service 1>&2 & exit 1"
            : "echo could not reach the service >&2; exit 1";

        Host.Run(Hook(script), TimeSpan.FromSeconds(30))
            .Detail.ShouldNotBeNull().ShouldContain("could not reach the service");
    }

    /// <summary>
    /// What a failing hook printed to standard OUTPUT is not repeated anywhere.
    /// </summary>
    /// <remarks>
    /// The tail travels: HookRunner puts it in an LR3103 message, which reaches the run's
    /// diagnostics, which NotifyPhase snapshots into the digest that is mailed or posted to a
    /// webhook, and which EventLogSink mirrors into the Windows Application log. stdout is a
    /// program's product - a token, a connection string - and plenty of tools print theirs and only
    /// then fail. Falling back to it selected exactly the case where the output is most likely to
    /// be both sensitive and broadcast.
    /// </remarks>
    [Fact]
    public void WhatAFailingHookPrintedToStdoutIsNotRepeated()
    {
        var script = OperatingSystem.IsWindows()
            ? "echo hunter2-the-vault-token & exit 1"
            : "echo hunter2-the-vault-token; exit 1";

        var outcome = Host.Run(Hook(script), TimeSpan.FromSeconds(30));

        outcome.Result.ShouldBe(HookResult.Failed);
        outcome.ExitCode.ShouldBe(1);
        (outcome.Detail ?? string.Empty).ShouldNotContain("hunter2");
    }

    /// <summary>And the pipe is still drained, or a chatty failing hook would hang the run.</summary>
    [Fact]
    public void StdoutIsStillDrainedEvenThoughItIsNotRepeated()
    {
        var script = OperatingSystem.IsWindows()
            ? "for /L %i in (1,1,4000) do @echo the quick brown fox jumped over the lazy dog again & exit 1"
            : "i=0; while [ $i -lt 4000 ]; do echo the quick brown fox jumped over the lazy dog again; i=$((i+1)); done; exit 1";

        Host.Run(Hook(script), TimeSpan.FromSeconds(60)).Result.ShouldBe(HookResult.Failed);
    }

    // ---- the missing executable --------------------------------------------------------------

    /// <summary>
    /// A hook whose program is not there is reported, never thrown.
    /// </summary>
    /// <remarks>
    /// <c>Process.Start</c> throws <c>Win32Exception</c> for ERROR_FILE_NOT_FOUND, and until this
    /// milestone <c>PlanExecutor</c>'s catch filter did not match it, RotationRunner had no try,
    /// and neither RunCommand nor Program had a catch. It would have left the process by the front
    /// door, mid-rotation, with the journal's run.end record never written.
    /// </remarks>
    [Fact]
    public void AMissingExecutableIsReportedNotThrown()
    {
        var missing = Path.Combine(_dir.FullName, "definitely-not-here.exe");

        var hook = new PlannedHook
        {
            Action = HookAction.Create(HookScheme.Command, $"command:{missing}", missing),
            Stage = HookStage.PostRotate,
            JobName = "app",
            Program = missing,
        };

        var outcome = Host.Run(hook, TimeSpan.FromSeconds(30));

        outcome.Result.ShouldBe(HookResult.CouldNotStart);
        outcome.Detail.ShouldNotBeNull().ShouldContain("definitely-not-here.exe");
    }
}
