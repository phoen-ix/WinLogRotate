using WinLogRotate.Contracts;

namespace WinLogRotate.Core.Configuration;

/// <summary>What a run would do with one job file.</summary>
public enum JobOutcome
{
    /// <summary>It did not bind at all: no <c>[job]</c> table, no paths, a reserved name.</summary>
    NotBound,

    /// <summary>It bound, and another file already has that name.</summary>
    /// <remarks>
    /// Kept apart from <see cref="Invalid"/> because it is not a fault in this job: the same file
    /// is perfectly good on a machine where nothing else is called that. It is also not
    /// file-scoped, so it stops every job on the machine rather than this one.
    /// </remarks>
    Duplicate,

    /// <summary>It bound, and validation found something that stops this job.</summary>
    Invalid,

    /// <summary>It would run.</summary>
    Ready,
}

/// <summary>One job file, judged the way a run judges it.</summary>
public sealed record JobVerdict
{
    /// <summary>The job as its file writes it, or null when it did not bind.</summary>
    /// <remarks>
    /// The unmerged one. Anything that writes a job file must work from this and never from
    /// <see cref="Effective"/>: writing back a merged job materialises every inherited default
    /// into the file and severs it from <c>[defaults]</c>, silently.
    /// </remarks>
    public JobConfig? Job { get; init; }

    /// <summary>The merged job, or null when binding or the name check stopped short.</summary>
    public EffectiveJob? Effective { get; init; }

    public required JobOutcome Outcome { get; init; }

    /// <summary>
    /// Everything worth saying about it, shaped as the loader files them.
    /// </summary>
    /// <remarks>
    /// Binding's and validation's findings carry <c>Job</c> once a named job has bound, so they
    /// can be weighed against that job. A file that did not bind, and the duplicate-name refusal,
    /// do not, because they are faults with the configuration as a whole and
    /// <c>LoadedConfig.HasErrors</c> reads exactly that distinction.
    /// </remarks>
    public required IReadOnlyList<ConfigDiagnostic> Diagnostics { get; init; }

    /// <summary>True when anything here would stop something.</summary>
    public bool HasErrors => Diagnostics.Any(d => d.Severity >= Severity.Error);
}
