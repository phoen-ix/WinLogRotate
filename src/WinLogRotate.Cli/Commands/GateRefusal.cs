using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Core.Engine;

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
    public static (CliDiagnostic Diagnostic, int ExitCode) For(
        GateHold hold, DateTimeOffset? since, int heldExitCode) => hold switch
        {
            GateHold.Implausible => (new CliDiagnostic
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
