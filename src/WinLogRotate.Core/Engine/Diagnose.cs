using WinLogRotate.Contracts;
using WinLogRotate.Core.Io;
using WinLogRotate.Core.Safety;

namespace WinLogRotate.Core.Engine;

/// <summary>
/// Turns the engine's refusals and failures into diagnostics.
/// </summary>
/// <remarks>
/// <para>
/// The engine used to hand the CLI a list of bare strings, which the CLI then labelled
/// uniformly as <see cref="DiagnosticCode.RotationFailed"/> at <see cref="Severity.Error"/>.
/// That reported a refused dangerous path - a security decision - as a rotation failure, and
/// threw away the job name and the Win32 code on the way.
/// </para>
/// <para>
/// Classifying at the point of failure, where the verdict is actually known, is what lets a
/// caller group by error class and attribute a failure to the job that caused it.
/// </para>
/// </remarks>
public static class Diagnose
{
    /// <summary>A guard refusal, with the guard's own wording and remedy preserved.</summary>
    public static CliDiagnostic Refusal(GuardDecision decision, string? job = null)
    {
        var (code, severity) = decision.Verdict switch
        {
            // The one genuine Critical. Any user can create a junction with no privilege at
            // all, so one appearing inside a directory we are walking as SYSTEM is the
            // signature of a privilege-escalation attempt, not a typo - and it is the only
            // verdict here that means someone may be doing this to you on purpose.
            GuardVerdict.ReparsePoint => (DiagnosticCode.ReparsePointRefused, Severity.Critical),

            // Error, matching what ConfigValidator has always reported for the identical
            // condition. A pattern pointing somewhere dangerous is a misconfiguration, and the
            // same condition arriving at two severities depending on which code path noticed
            // it is the kind of inconsistency that quietly breaks a severity threshold.
            GuardVerdict.ProtectedLocation => (DiagnosticCode.DangerousPathRefused, Severity.Error),
            GuardVerdict.VolumeRoot => (DiagnosticCode.DangerousPathRefused, Severity.Error),
            GuardVerdict.TooManyMatches => (DiagnosticCode.DangerousPathRefused, Severity.Error),

            // A malformed path is a mistake in the configuration, not a security event, and
            // labelling it one would train operators to ignore the band that matters.
            GuardVerdict.InvalidPath => (DiagnosticCode.ConfigInvalid, Severity.Error),
            _ => (DiagnosticCode.RotationFailed, Severity.Error),
        };

        return new CliDiagnostic
        {
            Severity = severity,
            Code = code,
            Message = decision.Message ?? $"{decision.Subject} was refused.",
            Path = decision.Subject,
            Job = job,
            Remedy = decision.Remedy,
        };
    }

    /// <summary>A file operation that threw.</summary>
    public static CliDiagnostic Failure(
        PlannedAction action, string path, Exception e, string? job = null)
    {
        var code = RetryPolicy.ErrorCode(e);
        var message = code == 0
            ? $"{action} {path}: {e.Message}"
            : $"{action} {path}: {Win32Error.Describe(code)}";

        return new CliDiagnostic
        {
            // A share violation is the one failure an operator can act on differently - it
            // means a lockstrategy question, not a permissions question - so it keeps its own
            // code rather than being folded into the general rotation failure.
            Severity = Severity.Error,
            Code = code is Win32Error.SharingViolation or Win32Error.LockViolation
                ? DiagnosticCode.FileLocked
                : DiagnosticCode.RotationFailed,
            Message = message,
            Path = path,
            Job = job,
            NativeError = code == 0 ? null : code,
            Remedy = code is Win32Error.SharingViolation or Win32Error.LockViolation
                ? "Set lockstrategy = \"copytruncate\" for this job, or run "
                  + $"\"winlogrotate probe {path}\" to see which strategies the writer permits."
                : null,
        };
    }
}
