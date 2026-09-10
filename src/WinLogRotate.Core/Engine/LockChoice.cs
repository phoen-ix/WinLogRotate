using WinLogRotate.Contracts;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Io;
using WinLogRotate.Core.State;

namespace WinLogRotate.Core.Engine;

/// <summary>What one run concluded about a pending truncation.</summary>
public sealed record NulFillJudgement
{
    /// <summary>The verdict to store, or null to leave the stored one alone.</summary>
    public NulFillVerdict? Verdict { get; init; }

    public NulFillEvidence Evidence { get; init; }

    /// <summary>Whether the recorded truncation has been used up and should be forgotten.</summary>
    public bool ClearBaseline { get; init; }

    public long? SizeBefore { get; init; }
    public long? SizeAfter { get; init; }
    public CliDiagnostic? Diagnostic { get; init; }

    public static readonly NulFillJudgement Nothing = new();

    /// <summary>Nothing could be concluded; look again next run.</summary>
    public static NulFillJudgement Defer { get; } = new();
}

/// <summary>Which strategy one rotation gets, and what to remember about the choice.</summary>
public sealed record StrategyDecision
{
    /// <summary>The strategy to use, or null when this file must not be rotated at all.</summary>
    public LockStrategy? Strategy { get; init; }

    /// <summary>The clause completing "due; ", or the reason for the refusal.</summary>
    public required string Explanation { get; init; }

    public ProbeVerdict Probe { get; init; } = ProbeVerdict.Unknown;
    public int ProbeError { get; init; }

    /// <summary>True when the file was opened on this run, so the verdict is worth recording.</summary>
    public bool Probed { get; init; }

    public CliDiagnostic? Diagnostic { get; init; }
}

/// <summary>
/// The two decisions a rotation makes about a file another process is writing.
/// </summary>
/// <remarks>
/// <para>
/// Pure - no file system beyond the readings it is handed, no Win32 - so every rule here is
/// provable on the Linux leg. That matters as much as it did for hooks: these decide whether a
/// customer's log is truncated in place, and one of them can refuse a path permanently.
/// </para>
/// <para>
/// <b>Only a NUL run confirms.</b> <see cref="NulFillDetector.Judge"/> will also confirm on the
/// size ratio alone, and that rule is sound within minutes of a truncation and worthless a day
/// later: a log producing four gigabytes a day is back at four gigabytes every morning for
/// entirely innocent reasons. Since the verdict is permanent, accepting the ratio here would
/// quarantine every steadily-growing log on the machine. The ratio still supplies the numbers the
/// explanation quotes.
/// </para>
/// </remarks>
public static class LockChoice
{
    /// <summary>How many runs may fail to reach a verdict before the evidence is abandoned.</summary>
    /// <remarks>
    /// A baseline that never expires is not harmless: it is evidence that gets staler every night
    /// while still being compared against.
    /// </remarks>
    public const int MaxChecks = 3;

    /// <summary>
    /// Judges the truncation recorded last time, if there is one.
    /// </summary>
    /// <param name="sample">The file as it is now, read at <c>LastTruncatedTo</c>.</param>
    public static NulFillJudgement Judge(
        string jobName, string path, PathState remembered, WriterSample sample)
    {
        if (remembered.LastTruncatedFrom is not { } before)
        {
            return NulFillJudgement.Nothing;
        }

        if (!sample.Opened)
        {
            // Gone is not a failure to observe - there is simply nothing left to judge, and the
            // next truncation will record its own baseline.
            if (sample.Error is Win32Error.FileNotFound or Win32Error.PathNotFound)
            {
                return new NulFillJudgement { ClearBaseline = true };
            }

            return Deferred(jobName, path, remembered);
        }

        // A different file wearing the same name. The baseline belongs to the one that is gone;
        // the verdict does not, because the defect belongs to the writer rather than to the inode.
        if (remembered.FileIdentity is { } was && sample.Identity is { } now && was != now)
        {
            return new NulFillJudgement { ClearBaseline = true };
        }

        // Nothing has been written past the point the truncation left it, so there is nothing to
        // look at. Recording Clean here would be a positive claim about the writer's behaviour
        // made on no evidence at all.
        if (sample.NulRunOrNull is not { } run)
        {
            return Deferred(jobName, path, remembered);
        }

        var observed = NulFillDetector.Judge(before, sample.Size, run);

        var confirmed = observed is
        {
            Verdict: NulFillVerdict.Confirmed,
            Evidence: NulFillEvidence.NulSignature,
        };

        return new NulFillJudgement
        {
            // Anything that is not the signature is the writer having written real text where the
            // truncation left the file, which is what a healthy writer does.
            Verdict = confirmed ? NulFillVerdict.Confirmed : NulFillVerdict.Clean,
            Evidence = confirmed ? NulFillEvidence.NulSignature : observed.Evidence,
            ClearBaseline = true,
            SizeBefore = before,
            SizeAfter = sample.Size,
            Diagnostic = confirmed
                ? new CliDiagnostic
                {
                    Severity = Severity.Error,
                    Code = DiagnosticCode.NulFillDetected,
                    Message = $"[{jobName}] {path}: {observed.Explanation}",
                    Job = jobName,
                    Path = path,
                    Remedy = "copytruncate is refused for this path from now on. Fix the writer so "
                           + "it reopens or seeks to zero (log4net: lockingModel MinimalLock), or "
                           + "use lockstrategy = \"rename\" if it permits FILE_SHARE_DELETE, or "
                           + "kind = \"manage\" if the application rotates its own logs.",
                }
                : null,
        };
    }

    private static NulFillJudgement Deferred(string jobName, string path, PathState remembered)
    {
        if (remembered.TruncationChecks < MaxChecks)
        {
            return NulFillJudgement.Defer;
        }

        return new NulFillJudgement
        {
            ClearBaseline = true,
            Diagnostic = new CliDiagnostic
            {
                Severity = Severity.Info,
                Code = DiagnosticCode.StrategyUnavailable,
                Message = $"[{jobName}] {path} could not be examined after {MaxChecks} runs, so "
                        + "whether its last truncation behaved is no longer knowable.",
                Job = jobName,
                Path = path,
                Remedy = "Nothing is wrong with the rotation itself. The next truncation starts a "
                       + "fresh check.",
            },
        };
    }

    /// <summary>
    /// Decides which strategy this rotation uses.
    /// </summary>
    /// <param name="probe">Consulted only for <c>auto</c>. An explicit strategy says what it means.</param>
    public static StrategyDecision Choose(
        EffectiveJob job, string path, NulFillVerdict nulFill, IWriterInspector probe)
    {
        var quarantined = nulFill == NulFillVerdict.Confirmed;

        if (job.LockStrategy != LockStrategy.Auto)
        {
            // Probing an explicit strategy would be an open per file per run buying nothing: the
            // operator has said what to do, and a failure is already classified and explained by
            // Diagnose, which tells them to run 'winlogrotate probe'.
            if (job.LockStrategy == LockStrategy.CopyTruncate && quarantined)
            {
                return Refused(job, path);
            }

            return new StrategyDecision
            {
                Strategy = job.LockStrategy,
                Explanation = $"lockstrategy = {Name(job.LockStrategy)}",
            };
        }

        var result = probe.Classify(path);
        var chosen = ProbeSupport.Best(result.Verdict, allowCopyTruncate: !quarantined);

        if (result.Verdict == ProbeVerdict.Unknown)
        {
            // Nothing could be asked - this is not Windows, or the probe itself failed. Falling
            // back to the documented default rather than refusing: rename is what the job would
            // have got without auto, and it fails loudly if the writer does not permit it.
            return new StrategyDecision
            {
                Strategy = LockStrategy.Rename,
                Explanation = "lockstrategy = auto: nothing could be asked, so the default stands",
                Diagnostic = Warn(job, path,
                    $"{path} could not be probed, so lockstrategy = auto fell back to rename.",
                    "A rename fails loudly if the writer withholds FILE_SHARE_DELETE, so nothing "
                    + "is done silently. Run 'winlogrotate probe' to see what it permits."),
            };
        }

        if (chosen is not { } strategy)
        {
            return new StrategyDecision
            {
                Probe = result.Verdict,
                ProbeError = result.BlockingError,
                Probed = true,
                Explanation = "lockstrategy = auto: nothing can be done with this file - "
                            + (result.Explanation ?? "it cannot be opened"),
                Diagnostic = new CliDiagnostic
                {
                    Severity = Severity.Error,
                    Code = DiagnosticCode.FileLocked,
                    Message = $"[{job.Name}] {path} cannot be rotated by any strategy: "
                            + $"{Win32Error.Describe(result.BlockingError)}.",
                    Job = job.Name,
                    Path = path,
                    NativeError = result.BlockingError,
                    Remedy = "The writer permits nothing at all - log4net's default ExclusiveLock "
                           + "looks like this. Change its locking model, or use kind = \"manage\" "
                           + "and let the application rotate its own logs.",
                },
            };
        }

        // Degrading to copy is worth saying every run, not once: the live file keeps growing for
        // as long as this lasts, which is a consequence an operator has to be able to see coming.
        var degraded = quarantined && result.Verdict == ProbeVerdict.CopyTruncate;

        return new StrategyDecision
        {
            Strategy = strategy,
            Probe = result.Verdict,
            ProbeError = result.BlockingError,
            Probed = true,
            Explanation = $"lockstrategy = auto: probed {result.Verdict}, using {Name(strategy)}",
            Diagnostic = strategy == LockStrategy.Copy
                ? Warn(job, path,
                    degraded
                        ? $"{path} has a confirmed NUL-fill, so lockstrategy = auto is archiving a "
                          + "copy instead of truncating."
                        : $"{path} permits nothing better than a copy, so the original keeps growing.",
                    "The archive is complete, but the live file is never emptied. Fix the writer, "
                    + "or use kind = \"manage\" if the application rotates its own logs.")
                : null,
        };
    }

    /// <summary>An explicit copytruncate job on a path that has been refused.</summary>
    /// <remarks>
    /// No substitution, deliberately. A rename would leave the writer appending to the archive
    /// while the recreated log stays empty - the log simply stops, which is a worse symptom than
    /// the one being refused. A copy would write a second full copy of a NUL-filled file every
    /// night, multiplying the disk-fill by the retention count. Both would also be choosing on
    /// behalf of an operator who said exactly what they wanted.
    /// </remarks>
    private static StrategyDecision Refused(EffectiveJob job, string path) => new()
    {
        Explanation = "copytruncate is refused for this path: the writer resumed at a cached "
                    + "offset and NTFS zero-filled the gap",
        Diagnostic = new CliDiagnostic
        {
            Severity = Severity.Error,
            Code = DiagnosticCode.StrategyUnavailable,
            Message = $"[{job.Name}] {path} is not being rotated: copytruncate was refused for it "
                    + "after a confirmed NUL-fill, and the job asks for no other strategy.",
            Job = job.Name,
            Path = path,
            Remedy = "Fix the writer so it reopens or seeks to zero (log4net: lockingModel "
                   + "MinimalLock); or set lockstrategy = \"rename\" if it permits "
                   + "FILE_SHARE_DELETE; or lockstrategy = \"copy\" to archive a snapshot and "
                   + "accept that the original keeps growing; or lockstrategy = \"auto\" to let "
                   + "the file decide.",
        },
    };

    private static CliDiagnostic Warn(EffectiveJob job, string path, string message, string remedy) => new()
    {
        Severity = Severity.Warning,
        Code = DiagnosticCode.StrategyUnavailable,
        Message = $"[{job.Name}] {message}",
        Job = job.Name,
        Path = path,
        Remedy = remedy,
    };

    private static string Name(LockStrategy strategy) => strategy.ToString().ToLowerInvariant();
}
