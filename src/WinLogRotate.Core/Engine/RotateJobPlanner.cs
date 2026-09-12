using WinLogRotate.Contracts;
using WinLogRotate.Core.Compression;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Globbing;
using WinLogRotate.Core.Safety;

namespace WinLogRotate.Core.Engine;

/// <summary>The live log plus everything already rotated from it.</summary>
public sealed record LogGeneration
{
    public required MatchedFile Live { get; init; }
    public required IReadOnlyList<ArchiveFile> Archives { get; init; }
}

/// <summary>
/// Plans a <see cref="JobKind.Rotate"/> job: the classic logrotate behaviour.
/// </summary>
/// <remarks>
/// The ordering within one pass matters and is taken from upstream:
/// <list type="number">
/// <item><description>compress the generation <c>delaycompress</c> deferred last time, so the
/// chain is uniformly named before anything moves;</description></item>
/// <item><description>shift the numbered chain upward, from the highest index down, so nothing
/// is overwritten before it has been moved;</description></item>
/// <item><description>rename the live log into the first generation;</description></item>
/// <item><description>dispose of whatever fell off the end.</description></item>
/// </list>
/// </remarks>
public static class RotateJobPlanner
{
    /// <param name="report">
    /// Where the planner says something that is not an operation. It stays a pure function of its
    /// inputs - it opens nothing and asks the file system nothing - but "this chain holds two
    /// spellings of one generation" is not expressible as a <see cref="PlannedOp"/>, because both
    /// files are acted on and neither is the subject.
    /// </param>
    public static JobPlan Plan(
        EffectiveJob job,
        IReadOnlyList<LogGeneration> generations,
        IReadOnlyDictionary<string, DueVerdict> verdicts,
        DateTimeOffset now,
        Action<CliDiagnostic>? report = null)
    {
        var operations = new List<PlannedOp>();

        foreach (var generation in generations)
        {
            var live = generation.Live;
            var verdict = verdicts.GetValueOrDefault(live.Path);

            if (verdict is null || !verdict.Due)
            {
                operations.Add(new PlannedOp
                {
                    Action = PlannedAction.Skip,
                    Source = live.Path,
                    Reason = verdict?.Explanation ?? "not due",
                    Bytes = live.Length,
                });
                continue;
            }

            if (job.DateExt)
            {
                PlanDateExt(job, generation, operations, now, verdict);
            }
            else
            {
                PlanNumbered(job, generation, operations, now, verdict, report);
            }
        }

        return new JobPlan
        {
            JobName = job.Name,
            Operations = operations,
            MatchedFiles = generations.Count,
        };
    }

    private static void PlanNumbered(
        EffectiveJob job, LogGeneration generation, List<PlannedOp> operations, DateTimeOffset now,
        DueVerdict verdict, Action<CliDiagnostic>? report)
    {
        var live = generation.Live;
        var byIndex = ChainByIndex(job, generation, report);

        // Step 1: the generation delaycompress deferred last time. Compressing it now means the
        // shift below operates on a uniformly-named chain. This also covers the awkward case
        // where an operator turned delaycompress off and left one uncompressed file stranded
        // among compressed ones.
        if (job is { DelayCompress: true, CompressType: not CompressType.None }
            && byIndex.TryGetValue(job.Start, out var newest)
            && newest.FirstOrDefault(a => !a.IsCompressed) is { } deferred)
        {
            operations.Add(new PlannedOp
            {
                Action = PlannedAction.Compress,
                Source = deferred.Path,
                Destination = deferred.Path + Compressor.Extension(job.CompressType),
                Reason = "delaycompress deferred this from the previous rotation",
                Bytes = deferred.Length,
            });
        }

        // Step 2: shift downward from the top so a move never lands on a file that has not yet
        // been moved itself. rotate = -1 keeps everything, so nothing is disposed by count.
        var highest = job.Rotate < 0 ? byIndex.Keys.DefaultIfEmpty(job.Start - 1).Max() : job.Rotate + job.Start - 1;

        for (var index = highest; index >= job.Start; index--)
        {
            if (!byIndex.TryGetValue(index, out var at))
            {
                continue;
            }

            // Anything past the retention count falls off rather than shifting up. Where one index
            // holds two spellings they go together, in both directions: the pair is one generation
            // as far as retention is concerned, and splitting it would delete an archive on the
            // strength of a guess about which of the two is redundant.
            var doomed = job.Rotate >= 0
                && index >= job.Rotate + job.Start - 1
                && byIndex.Count >= job.Rotate;

            foreach (var archive in at)
            {
                operations.Add(doomed
                    ? new PlannedOp
                    {
                        Action = PlannedAction.Delete,
                        Source = archive.Path,
                        Reason = $"rotate = {job.Rotate} keeps {job.Rotate} generation(s)",
                        Bytes = archive.Length,
                    }
                    : new PlannedOp
                    {
                        Action = PlannedAction.Rename,
                        Source = archive.Path,
                        Destination = ArchiveNaming.Numbered(job, live.Path, index + 1, archive.IsCompressed),
                        Reason = $"shifting generation {index} to {index + 1}",
                        Bytes = archive.Length,
                    });
            }
        }

        // Step 3: the live log itself.
        AddLiveRotation(job, live, ArchiveNaming.FirstRotation(job, live.Path, now), operations, verdict);

        // Step 4: age-based disposal, on top of the count.
        AddMaxAgeDeletions(job, generation.Archives, operations, now);
    }

    /// <summary>
    /// The numbered chain grouped by index, in the order <see cref="FileSeries.Order"/> put it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A list per index rather than one file per index, because an index can legitimately hold
    /// two files. <see cref="LogSeries"/> probes both spellings of every index on purpose, and
    /// <see cref="FileSeries.Classify"/> strips the compression suffix before reading the index -
    /// so <c>app.log.1</c> and <c>app.log.1.gz</c> both answer 1. Keying a dictionary on that
    /// threw <see cref="ArgumentException"/>, which is in no catch filter anywhere: the run ended
    /// at <c>ExitCode.InternalError</c> and abandoned every job after it.
    /// </para>
    /// <para>
    /// It is not an exotic state. <c>RunCommand</c>'s own recovery path warns that a killed run
    /// may have left "an uncompressed archive about" and says the planners are written to cope -
    /// so the run that told the operator it had happened was the run that died on it.
    /// </para>
    /// <para>
    /// Both files are kept and both shift, because neither can be shown to be the redundant one.
    /// <see cref="Compressor.Compress"/> stamps the archive with the source's own modification
    /// time, so a pair left by an interrupted compression is indistinguishable by date; and a pair
    /// left by a <c>delaycompress</c> chain that failed to shift is two genuinely different
    /// generations. Guessing wrong deletes an archive, so the pair travels up the chain together
    /// and falls off the end together, and the operator is told it is there.
    /// </para>
    /// </remarks>
    private static Dictionary<int, List<ArchiveFile>> ChainByIndex(
        EffectiveJob job, LogGeneration generation, Action<CliDiagnostic>? report)
    {
        var byIndex = new Dictionary<int, List<ArchiveFile>>();

        foreach (var archive in generation.Archives)
        {
            if (archive.Index is not { } index)
            {
                continue;
            }

            if (!byIndex.TryGetValue(index, out var at))
            {
                byIndex[index] = at = [];
            }

            at.Add(archive);
        }

        if (report is null)
        {
            return byIndex;
        }

        foreach (var (index, at) in byIndex.OrderBy(p => p.Key))
        {
            if (at.Count < 2)
            {
                continue;
            }

            report(new CliDiagnostic
            {
                Severity = Severity.Warning,
                Code = DiagnosticCode.DuplicateGeneration,
                Message = $"'{job.Name}' generation {index} is held by "
                        + string.Join(" and ", at.Select(a => WinPath.FileName(a.Path)))
                        + ".",
                Remedy = "Both were kept and both shift together, because they cannot be told "
                       + "apart from here - an interrupted compression leaves a copy with the same "
                       + "timestamp as its archive, and so does a chain that failed to shift. "
                       + "Compare the contents before removing either.",
            });
        }

        return byIndex;
    }

    private static void PlanDateExt(
        EffectiveJob job, LogGeneration generation, List<PlannedOp> operations, DateTimeOffset now,
        DueVerdict verdict)
    {
        var live = generation.Live;
        var target = ArchiveNaming.FirstRotation(job, live.Path, now);

        // dateext refuses to overwrite, by design - unlike numbered rotation, which silently
        // replaces. Rotating twice within one date-format period is therefore an error rather
        // than a silent loss of the earlier archive. config check catches the configuration
        // that guarantees this, but a --force can still reach it.
        // Compare against both spellings: the rename produces an uncompressed name, but last
        // period's archive has usually been compressed since, so only checking one of the two
        // would let a second rotation quietly overwrite the first.
        var compressedTarget = target + Compressor.Extension(job.CompressType);

        if (generation.Archives.Any(a =>
                string.Equals(a.Path, target, StringComparison.OrdinalIgnoreCase)
                || string.Equals(a.Path, compressedTarget, StringComparison.OrdinalIgnoreCase)))
        {
            operations.Add(new PlannedOp
            {
                Action = PlannedAction.Skip,
                Source = live.Path,
                Reason = $"'{WinPath.FileName(target)}' already exists; dateext never overwrites an archive",
                Bytes = live.Length,
            });
            return;
        }

        // Deferred compression from the previous run, if any remain uncompressed.
        if (job is { DelayCompress: true, CompressType: not CompressType.None })
        {
            foreach (var stale in generation.Archives.Where(a => !a.IsCompressed))
            {
                operations.Add(new PlannedOp
                {
                    Action = PlannedAction.Compress,
                    Source = stale.Path,
                    Destination = stale.Path + Compressor.Extension(job.CompressType),
                    Reason = "delaycompress deferred this from a previous rotation",
                    Bytes = stale.Length,
                });
            }
        }

        AddLiveRotation(job, live, target, operations, verdict);

        // Ordered newest-first by parsed date, so retention keeps the newest N regardless of
        // how the format happens to sort as a string.
        if (job.Rotate >= 0)
        {
            foreach (var doomed in generation.Archives.Skip(job.Rotate))
            {
                operations.Add(new PlannedOp
                {
                    Action = PlannedAction.Delete,
                    Source = doomed.Path,
                    Reason = $"rotate = {job.Rotate} keeps the newest {job.Rotate} archive(s)",
                    Bytes = doomed.Length,
                });
            }
        }

        AddMaxAgeDeletions(job, generation.Archives, operations, now);
    }

    private static void AddLiveRotation(
        EffectiveJob job, MatchedFile live, string target, List<PlannedOp> operations,
        DueVerdict verdict)
    {
        // The resolved strategy where the caller worked one out, the configured one otherwise.
        var strategy = verdict.Strategy ?? job.LockStrategy;

        var action = strategy switch
        {
            LockStrategy.Rename => PlannedAction.Rename,
            LockStrategy.CopyTruncate => PlannedAction.CopyTruncate,
            LockStrategy.Copy => PlannedAction.Copy,

            // Exhaustive on purpose, and this arm is the point of the milestone. Auto used to
            // fall into a discard `_ => Rename`, so the documented safe option silently became
            // the one that fails outright on a writer withholding FILE_SHARE_DELETE - while the
            // reason line printed "lockstrategy = auto". Throwing makes that unreachable rather
            // than merely fixed.
            _ => throw new InvalidOperationException(
                $"lockstrategy = {strategy.ToString().ToLowerInvariant()} must be resolved to a "
                + "concrete strategy before planning."),
        };

        operations.Add(new PlannedOp
        {
            Action = action,
            Source = live.Path,
            Destination = target,
            Reason = $"due; {verdict.Explanation}",
            Bytes = live.Length,
            Strategy = strategy,
        });

        // Under copytruncate the inode never changes, so there is nothing to recreate; under a
        // rename the log has to reappear or the writer keeps appending to the archive.
        if (action == PlannedAction.Rename)
        {
            operations.Add(new PlannedOp
            {
                Action = PlannedAction.Create,
                Source = live.Path,
                Destination = live.Path,
                Reason = "recreating the log the writer expects to find",
            });
        }

        // Compression of the newest generation is deferred by exactly one cycle when
        // delaycompress is set - that is the entire point of the directive.
        if (job.CompressType != CompressType.None && !job.DelayCompress)
        {
            operations.Add(new PlannedOp
            {
                Action = PlannedAction.Compress,
                Source = target,
                Destination = target + Compressor.Extension(job.CompressType),
                Reason = $"compress = {job.CompressType.ToString().ToLowerInvariant()}",
                Bytes = live.Length,
            });
        }
    }

    private static void AddMaxAgeDeletions(
        EffectiveJob job, IReadOnlyList<ArchiveFile> archives,
        List<PlannedOp> operations, DateTimeOffset now)
    {
        if (job.MaxAge is not { } maxAge)
        {
            return;
        }

        var cutoff = now.AddDays(-maxAge);
        var already = operations
            .Where(o => o.Action == PlannedAction.Delete)
            .Select(o => o.Source)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var archive in archives)
        {
            if (already.Contains(archive.Path))
            {
                continue;
            }

            var age = archive.Stamp ?? archive.LastWriteUtc;
            if (age < cutoff)
            {
                operations.Add(new PlannedOp
                {
                    Action = PlannedAction.Delete,
                    Source = archive.Path,
                    Reason = $"maxage = {maxAge} days; this one is from {age:yyyy-MM-dd}",
                    Bytes = archive.Length,
                });
            }
        }
    }
}
