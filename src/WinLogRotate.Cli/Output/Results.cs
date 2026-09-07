using WinLogRotate.Contracts;
using WinLogRotate.Core.Configuration;

namespace WinLogRotate.Cli.Output;

/// <summary>Payload of a verb that returns no data. Registering a concrete type rather
/// than using <c>object</c> keeps the JSON source generator able to see everything.</summary>
public sealed record EmptyResult;

/// <summary>Payload of <c>winlogrotate --version --json</c>. The GUI reads this on start to
/// check it is talking to a CLI it understands.</summary>
public sealed record VersionResult
{
    public required string Product { get; init; }
    public required string Version { get; init; }
    public required int Schema { get; init; }
    public required string Runtime { get; init; }
    public required string Architecture { get; init; }

    /// <summary>True when this process holds an elevated token. The GUI uses it to decide
    /// whether a button needs a shield.</summary>
    public required bool Elevated { get; init; }
}

/// <summary>Payload of <c>winlogrotate glob</c>.</summary>
public sealed record GlobResult
{
    public required string Pattern { get; init; }

    /// <summary>The directory the walk actually started from - the longest wildcard-free
    /// prefix. Shown because a surprising anchor is the usual cause of a surprising result.</summary>
    public required string Anchor { get; init; }

    public required int Count { get; init; }
    public required long TotalBytes { get; init; }
    public required IReadOnlyList<string> Files { get; init; }
}

/// <summary>Payload of <c>winlogrotate journal</c>.</summary>
public sealed record JournalResult
{
    public required string Directory { get; init; }
    public required int Count { get; init; }

    /// <summary>Lines that could not be parsed - normally a torn final line from a run that
    /// was killed. Surfaced rather than swallowed, because it is a clue.</summary>
    public required int SkippedLines { get; init; }

    public required IReadOnlyList<CliEvent> Entries { get; init; }
}

/// <summary>A config diagnostic in wire form, so the GUI can jump to file, line and column.</summary>
public sealed record ConfigDiagnosticDto
{
    public required string Severity { get; init; }
    public required string Code { get; init; }
    public required string Message { get; init; }
    public required string File { get; init; }
    public int Line { get; init; }
    public int Column { get; init; }
    public string? Remedy { get; init; }
}

/// <summary>Payload of <c>winlogrotate config check</c>.</summary>
public sealed record ConfigCheckResult
{
    public required string Root { get; init; }
    public required int Jobs { get; init; }
    public required int Errors { get; init; }
    public required int Warnings { get; init; }
    public required IReadOnlyList<ConfigDiagnosticDto> Diagnostics { get; init; }
}

/// <summary>Payload of <c>winlogrotate config show</c> - every default resolved.</summary>
public sealed record ConfigShowResult
{
    public required string Root { get; init; }
    public required IReadOnlyList<EffectiveJob> Jobs { get; init; }
}

/// <summary>Payload of <c>winlogrotate run</c>.</summary>
public sealed record RunResult
{
    /// <summary>Groups this run's entries in the journal.</summary>
    public required string RunId { get; init; }

    public required bool DryRun { get; init; }
    public required int JobsConsidered { get; init; }
    public required int JobsRun { get; init; }
    public required int Completed { get; init; }
    public required int Failed { get; init; }
    public required long BytesFreed { get; init; }
    public required IReadOnlyList<string> Errors { get; init; }
}

/// <summary>Payload of <c>winlogrotate doctor</c>: every fact an operator would otherwise
/// gather from four different consoles.</summary>
public sealed record DoctorResult
{
    public required string Version { get; init; }
    public required string Scope { get; init; }
    public required string Root { get; init; }
    public required bool ConfigExists { get; init; }
    public required bool JobsDirectoryExists { get; init; }
    public required bool Elevated { get; init; }
    public required string AclVerdict { get; init; }
    public required bool HooksAllowed { get; init; }
    public string? AclFix { get; init; }
    public required string RunHost { get; init; }
    public required string RunHostDetail { get; init; }
}

/// <summary>Payload of the <c>host</c> verbs.</summary>
public sealed record HostResult
{
    public required string Host { get; init; }
    public required string ConfigRoot { get; init; }
    public required string Scope { get; init; }
}

/// <summary>Payload of <c>winlogrotate probe</c>.</summary>
public sealed record ProbeResultDto
{
    public required string Path { get; init; }
    public required string Verdict { get; init; }
    public required string Explanation { get; init; }

    /// <summary>The best strategy this file actually supports, for the GUI to preselect.</summary>
    public string? Suggested { get; init; }

    public int BlockingError { get; init; }
    public string? BlockingErrorText { get; init; }
}

/// <summary>Payload of <c>winlogrotate import</c>.</summary>
public sealed record ImportResult
{
    public required string Source { get; init; }
    public required string OutputDirectory { get; init; }
    public required int Jobs { get; init; }

    /// <summary>Jobs containing something that could not be translated. Each is written
    /// disabled, with the untranslatable part preserved as a TODO comment.</summary>
    public required int NeedingReview { get; init; }

    public required IReadOnlyList<string> Files { get; init; }
}

/// <summary>Payload of <c>winlogrotate host export-task</c>.</summary>
public sealed record ExportTaskResult
{
    public required string Xml { get; init; }
}

/// <summary>One producer found by <c>winlogrotate scan</c>.</summary>
public sealed record ScanFinding
{
    public required string Producer { get; init; }
    public required string Directory { get; init; }
    public required string Pattern { get; init; }
    public required bool SelfRotates { get; init; }

    /// <summary>Almost always false, and that is the point of the whole verb.</summary>
    public required bool SelfDeletes { get; init; }

    public required string SuggestedKind { get; init; }
    public string? Note { get; init; }
    public int FileCount { get; init; }
    public long TotalBytes { get; init; }
}

/// <summary>Payload of <c>winlogrotate scan</c>.</summary>
public sealed record ScanResult
{
    public required IReadOnlyList<ScanFinding> Findings { get; init; }
}

/// <summary>Payload of <c>winlogrotate host pause</c>.</summary>
public sealed record PauseResult
{
    public required string PausedUntil { get; init; }
}

/// <summary>Payload of the <c>update</c> verbs.</summary>
public sealed record UpdateResult
{
    public required string Current { get; init; }
    public string? Latest { get; init; }
    public required bool UpdateAvailable { get; init; }
    public string? Detail { get; init; }
}
