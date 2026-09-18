using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Core.Engine;
using WinLogRotate.Hosting;

namespace WinLogRotate.Cli.Commands;

/// <summary>
/// What a run that could not take the rotation gate says, and what it exits with.
/// </summary>
/// <remarks>
/// Pure, and out of <c>RunCommand</c>, so both halves run on both legs. The decision is the
/// whole of the fix and the platform has nothing to do with it - which matters here more than
/// usual, because the thing being fixed survived by living inside a Windows-only code path
/// nobody could execute.
/// </remarks>
internal static class GateRefusal
{
    /// <param name="hold">How long the gate has been refusing runs, judged from the record.</param>
    /// <param name="since">When the record says the refusals began.</param>
    /// <param name="heldExitCode">What the invocation asked to exit with when the gate is held.</param>
    /// <param name="outcome">What the gate itself said, which tells a held gate from one that could not be opened.</param>
    public static (CliDiagnostic Diagnostic, int ExitCode) For(
        GateHold hold, DateTimeOffset? since, int heldExitCode, GateOutcome outcome) => (hold, outcome) switch
        {
            (GateHold.Implausible, _) => (new CliDiagnostic
            {
                Severity = Severity.Error,
                Code = DiagnosticCode.RotationGateHeld,
                Message = $"The rotation gate has been held by another process since {since:u}; "
                        + "no rotation has run on this machine since then.",
                Remedy = "No rotation takes this long. Global\\WinLogRotate.Rotation grants every "
                       + "local account the right to synchronise on it, which is what lets the GUI "
                       + "and the SYSTEM task share one gate and also what lets any account hold "
                       + "it. Find the holder with Process Explorer or handle.exe and end it.",
            },

            // Never the task's --lock-held-exit. That option exists so an overlapping manual run
            // does not paint Last Run Result red, and RunLockOptions.HeldExitCode's own remarks
            // treat 0x0 as a reporting cost worth paying for that. It was never meant to cover a
            // machine on which nothing rotates at all, and letting it do so is the whole of why
            // this was silent: Task Scheduler showed 0x0 every night for a week.
            ExitCode.Errors),

            // The same code and the same exit as a held gate, because the run did the same
            // thing - nothing - and the same record is kept, so a name still unusable four
            // hours on is reported above as a gate held too long, with the remedy that fits:
            // find what holds the name and end it. Said in its own words here, because
            // "another rotation is already running" is not what happened, and an operator told
            // that would wait for a rotation that is not there to finish.
            // Warning, unlike a gate that is merely busy: a busy gate is a normal night, a name
            // nothing can open is not, and at Info - with the task's --lock-held-exit 0 - the
            // first night of it reached neither the Event Log nor Task Scheduler's result column.
            (_, GateOutcome.Unopenable) => (new CliDiagnostic
            {
                Severity = Severity.Warning,
                Code = DiagnosticCode.AlreadyRunning,
                Message = "The rotation gate could not be opened: its name is held by a kernel "
                        + "object that is not a mutex, or by a mutex this account may not open, "
                        + "so this run cannot tell whether another rotation is running; nothing "
                        + "was done.",
                Remedy = "Global\\WinLogRotate.Rotation should be a mutex every local account may "
                       + "synchronise on. Find what holds the name with Process Explorer or "
                       + "handle.exe and end it.",
            }, heldExitCode),

            _ => (new CliDiagnostic
            {
                // Said as a diagnostic and not only as a line, because ok is false whenever the
                // exit code is not zero and an empty diagnostics array beside it leaves a caller
                // with a bare 3. Info, not an error: this is the expected outcome of an
                // overlapping manual run, which is why the registered task passes
                // --lock-held-exit 0.
                Severity = Severity.Info,
                Code = DiagnosticCode.AlreadyRunning,
                Message = "Another rotation is already running; nothing was done.",
                Remedy = "This is normal when a manual run overlaps the scheduled one.",
            }, heldExitCode),
        };
}
