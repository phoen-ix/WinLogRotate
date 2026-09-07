namespace WinLogRotate.Contracts;

/// <summary>How much the reader should care.</summary>
public enum Severity
{
    /// <summary>Progress and outcomes. Suppressed unless --verbose.</summary>
    Info,

    /// <summary>Something was skipped or degraded, but the run continued.</summary>
    Warning,

    /// <summary>A job failed. The run continues with other jobs; exit code becomes 1.</summary>
    Error,

    /// <summary>A security or integrity problem that disables a whole capability -
    /// a loosened conf.d ACL, a refused dangerous path. Always surfaced, never suppressed.</summary>
    Critical,
}

/// <summary>
/// One thing worth telling the caller. Carries a stable <see cref="Code"/> so the GUI can
/// react to a condition and documentation can link to it, rather than matching on prose
/// that will be reworded.
/// </summary>
public sealed record CliDiagnostic
{
    public required Severity Severity { get; init; }

    /// <summary>Stable identifier, e.g. "LR9001". See <see cref="DiagnosticCode"/>.</summary>
    public required string Code { get; init; }

    /// <summary>Human-readable, one sentence, no stack traces.</summary>
    public required string Message { get; init; }

    /// <summary>The file or log path this concerns, when there is one.</summary>
    public string? Path { get; init; }

    /// <summary>1-based line in <see cref="Path"/>, for config diagnostics.</summary>
    public int? Line { get; init; }

    /// <summary>1-based column in <see cref="Path"/>, for config diagnostics.</summary>
    public int? Column { get; init; }

    /// <summary>The job this concerns, when the diagnostic is job-scoped.</summary>
    public string? Job { get; init; }

    /// <summary>What the operator should actually do about it. Populated for everything a
    /// human can fix - an exact icacls command, the directive to set, the account to grant.</summary>
    public string? Remedy { get; init; }

    /// <summary>
    /// The underlying Win32 error, where one caused this. Null everywhere else.
    /// </summary>
    /// <remarks>
    /// This is the error <em>class</em>, and it exists so that grouping can be done on a
    /// number rather than on <see cref="Message"/>. Prose gets reworded between releases; if
    /// anything downstream groups or fingerprints on it, the first improved error message
    /// makes every ongoing failure look brand new. The number never moves.
    /// </remarks>
    public int? NativeError { get; init; }
}
