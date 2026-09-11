using WinLogRotate.Core;

namespace WinLogRotate.Gui.Cli;

/// <summary>Why an invocation did not produce a result.</summary>
public enum CliFailure
{
    None,
    NotFound,
    UacDeclined,
    Timeout,
}

/// <summary>What one invocation produced.</summary>
public sealed record CliResult
{
    public required int ExitCode { get; init; }
    public required string StdOut { get; init; }
    public required string StdErr { get; init; }
    public CliFailure Failure { get; init; }

    /// <summary>
    /// What to show a person when this failed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Computed, not assigned. It used to be set at each of the runner's three exit points, and
    /// only one of them looked at the envelope - so a diagnostic reached the four elevated call
    /// sites and was lost on the twelve unelevated ones, where this was standard error and a
    /// <c>--json</c> verb writes nothing there. That recreated, for milestone 21's new exit code,
    /// the empty-expander defect this type was written to remove.
    /// </para>
    /// <para>
    /// Deriving it means a third entry point cannot repeat that. Deleting the setter is what
    /// enforces it, and the enforcement lands on the Linux leg: <c>EnableWindowsTargeting</c>
    /// puts the WinForms project through <c>linux-typecheck</c>, so an assignment left behind is
    /// a compile error on the one leg that cannot otherwise see this code at all.
    /// </para>
    /// </remarks>
    public string Details => EnvelopeDetails.From(StdOut, StdErr);

    // Core.ExitCode is qualified because the property below shadows the type name inside
    // this record.
    public bool Ok => Failure == CliFailure.None && ExitCode == Core.ExitCode.Ok;

    /// <summary>
    /// The CLI reported a defect in itself, rather than a problem with the machine.
    /// </summary>
    /// <remarks>
    /// <c>ExitCode.InternalError</c> is the only code that says nothing about what was or was not
    /// done can be relied on, which is what makes it the only one worth interrupting for. A
    /// configuration with an error is exit 2 and belongs in a status line; turning that into a
    /// dialog would put one in front of the operator on every visit to the Jobs page.
    /// <para>
    /// <c>Failure</c> must be <c>None</c>: the runner's own failures exit -1 and are not the
    /// CLI's verdict on anything.
    /// </para>
    /// </remarks>
    public bool IsDefect => Failure == CliFailure.None && ExitCode == Core.ExitCode.InternalError;

    /// <summary>
    /// Plain wording for an exit code, so a dialog never shows a bare number.
    /// </summary>
    /// <remarks>
    /// Shown in nine places and, until this project existed, pinned by nothing - a new exit code
    /// falls through to "Exited with code 4", which is the sentence this method exists to avoid.
    /// </remarks>
    public string Describe() => Failure switch
    {
        CliFailure.NotFound => "winlogrotate.exe could not be found.",
        CliFailure.UacDeclined => "Elevation was cancelled. Nothing was changed.",
        CliFailure.Timeout => "The operation took too long and was stopped.",
        _ => ExitCode switch
        {
            Core.ExitCode.Ok => "Completed.",
            Core.ExitCode.Errors => "Completed, but some files could not be rotated.",
            Core.ExitCode.ConfigInvalid => "The configuration has errors, so nothing was attempted.",
            Core.ExitCode.LockHeld => "Another rotation is already running.",
            Core.ExitCode.InternalError => "Something went wrong that WinLogRotate did not anticipate.",
            _ => $"Exited with code {ExitCode}.",
        },
    };
}
