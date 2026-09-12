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

            // Deliberately not in the security band, and deliberately not a failure. We could not
            // prove a link safe, which is not the same as finding it unsafe - a disconnected share
            // looks exactly like this. Reporting it as an escalation attempt would put the one
            // Critical this product has in front of an operator whose network was briefly down.
            GuardVerdict.Unverifiable => (DiagnosticCode.JobSkipped, Severity.Warning),
            _ => (DiagnosticCode.RotationFailed, Severity.Error),
        };

        return new CliDiagnostic
        {
            Severity = severity,
            Code = code,
            Message = decision.Message ?? $"{decision.Subject} was refused.",
            Path = decision.Subject,
            Job = job,
            NativeError = decision.NativeError == 0 ? null : decision.NativeError,
            Remedy = decision.Remedy,
        };
    }

    /// <summary>A file operation that threw.</summary>
    /// <param name="destination">
    /// Where it was writing, when it was writing somewhere. Named in the message because the
    /// failure is often about the destination and reads as if it were about the source: a missing
    /// olddir made MoveFileEx return ERROR_PATH_NOT_FOUND, which was rendered against the live
    /// log - a file that plainly existed. Taken from the plan rather than the exception, because
    /// CopyTruncate stages through a ".part" sibling the operator has never heard of.
    /// </param>
    public static CliDiagnostic Failure(
        PlannedAction action, string path, Exception e, string? job = null, string? destination = null)
    {
        var code = RetryPolicy.ErrorCode(e);
        var where = destination is { Length: > 0 } to && !to.Equals(path, StringComparison.OrdinalIgnoreCase)
            ? $"{path} -> {to}"
            : path;

        var message = code == 0
            ? $"{action} {where}: {e.Message}"
            : $"{action} {where}: {Win32Error.Describe(code)}";

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
            // Not "try copytruncate" unconditionally: this fires for a failed copytruncate too,
            // and telling an operator to switch to the strategy that just failed is advice that
            // cannot work. The probe answers the question either way, so it leads.
            Remedy = code is Win32Error.SharingViolation or Win32Error.LockViolation
                ? $"Run \"winlogrotate probe {path}\" to see which strategies this writer permits"
                  + (action == PlannedAction.CopyTruncate
                      ? ". Something else held the file while it was being copied or emptied; a "
                        + "retry on the next run may be all it needs."
                      : ", and set lockstrategy accordingly.")
                : null,
        };
    }
}
