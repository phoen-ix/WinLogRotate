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

    // Core.ExitCode is qualified because the property below shadows the type name inside
    // this record.
    public bool Ok => Failure == CliFailure.None && ExitCode == Core.ExitCode.Ok;

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
            _ => $"Exited with code {ExitCode}.",
        },
    };
}
