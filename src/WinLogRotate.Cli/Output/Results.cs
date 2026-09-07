using WinLogRotate.Contracts;

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

    /// <summary>True for the NativeAOT build. A framework-dependent CLI would mean someone
    /// built it themselves, which is worth knowing in a bug report.</summary>
    public required bool NativeAot { get; init; }
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
