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
/// <item><description>condemn by age, against the names the files have now, before anything
/// moves;</description></item>
/// <item><description>compress the generation <c>delaycompress</c> deferred last time, so the
/// chain is uniformly named before anything moves - and so the shift moves the compressed
/// name;</description></item>
/// <item><description>shift the numbered chain upward, from the top of the chain down, so nothing
/// is overwritten before it has been moved. Whatever the count disposes of falls out of this same
/// pass rather than being collected afterwards;</description></item>
/// <item><description>rename the live log into the first generation.</description></item>
/// </list>
/// <para>
/// The first item is the one that is easy to get wrong, and was. A plan is a list executed in
/// order, so a deletion decided after a rename has been queued names a path that by then holds a
/// different file - and <see cref="PlannedOp.Reason"/> promises to say which rule condemned
/// <i>this</i> file, which it cannot do if the file it names is not the file that goes.
/// </para>
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

        // Step 1: age. Decided against the names the files have now and emitted before anything
        // moves, so the reason names the file that goes. See AddMaxAgeDeletions.
        var condemned = AddMaxAgeDeletions(job, generation.Archives, operations, now);

        // Whether the count will dispose of this index rather than shift it. Asked before the
        // catch-up compress as well as inside the shift, because compressing a generation this
        // same pass is about to delete is work done to fill a bin - and with rotate = 1 that is
        // the newest archive, every run, for ever.
        bool Doomed(int index) =>
            job.Rotate >= 0 && index >= job.Rotate + job.Start - 1 && byIndex.Count >= job.Rotate;

        // Step 2: the generation delaycompress deferred last time. Compressing it now means the
        // shift below operates on a uniformly-named chain. This also covers the awkward case
        // where an operator turned delaycompress off and left one uncompressed file stranded
        // among compressed ones.
        if (job is { DelayCompress: true, CompressType: not CompressType.None }
            && !Doomed(job.Start)
            && byIndex.TryGetValue(job.Start, out var newest)
            && newest.FirstOrDefault(a => !a.IsCompressed && !condemned.Contains(a.Path)) is { } deferred)
        {
            var archive = deferred.Path + Compressor.Extension(job.CompressType);

            operations.Add(new PlannedOp
            {
                Action = PlannedAction.Compress,
                Source = deferred.Path,
                Destination = archive,
                Reason = "delaycompress deferred this from the previous rotation",
                Bytes = deferred.Length,
            });

            // The shift must move what compression leaves behind, not what it consumed.
            // Compressor.Compress deletes its source, and the chain was read before this step, so
            // the shift went on to rename a file that no longer existed - and to rename it to the
            // uncompressed name, because the entry still said IsCompressed = false. That is one
            // failed operation and one archive stranded below the retention window, every night,
            // on every job with delaycompress and compression both on.
            newest[newest.IndexOf(deferred)] = deferred with
            {
                File = deferred.File with { Path = archive },
                IsCompressed = true,
            };
        }

        // Step 3: shift downward from the top so a move never lands on a file that has not yet
        // been moved itself. rotate = -1 keeps everything, so nothing is disposed by count.
        //
        // The top of the chain as it actually is, not the top of the retention window. Starting at
        // rotate + start - 1 meant every index above the window was read out of the directory,
        // held in byIndex, and then never visited: not shifted, not deleted, invisible to
        // everything except maxage. That is exactly the state LogSeries.MaxNumberedProbe exists to
        // find - its remark names an operator who lowers rotate from 14 to 7 and says nothing else
        // will ever tidy app.log.8 through app.log.14 - and the planner threw the list away, so
        // the probes bought nothing.
        var highest = byIndex.Keys.DefaultIfEmpty(job.Start - 1).Max();

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
            var doomed = Doomed(index);

            foreach (var archive in at)
            {
                // Age already took it. Shifting or deleting it again would name a file this plan
                // removed a few operations ago.
                if (condemned.Contains(archive.Path))
                {
                    continue;
                }

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

        // Step 4: the live log itself.
        AddLiveRotation(
            job, live, ArchiveNaming.FirstRotation(job, live.Path, now), operations, verdict);
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

        // Retention decided before anything is written, for the reason the numbered path decides
        // it first: a file this pass is going to delete must not be compressed on its way to the
        // bin, and must not be condemned twice.
        var condemned = AddMaxAgeDeletions(job, generation.Archives, operations, now);

        // Ordered newest-first by parsed date, so retention keeps the newest N regardless of how
        // the format happens to sort as a string.
        //
        // By generation, not by file. rotate is "how many old generations to keep", and the run
        // about to happen produces one of them - counting only the archives already on disk kept
        // rotate of those plus the new one, so a dateext job held one more generation than it was
        // configured for, for ever, while the numbered path landed on exactly rotate.
        //
        // And one dated generation is one generation however many spellings of it are present.
        // ArchiveNaming.DateExtGlob ends in a wildcard, so app.log-20260905 and
        // app.log-20260905.gz are both discovered and both parse to the same stamp; counting
        // files would push one of the pair past the window and delete it, chosen by nothing
        // better than which path sorts first.
        if (job.Rotate >= 0)
        {
            var keep = Math.Max(job.Rotate - 1, 0);

            var doomed = generation.Archives
                .GroupBy(DatedGeneration, StringComparer.OrdinalIgnoreCase)
                .Skip(keep)
                .SelectMany(g => g);

            foreach (var archive in doomed)
            {
                if (!condemned.Add(archive.Path))
                {
                    continue;
                }

                operations.Add(new PlannedOp
                {
                    Action = PlannedAction.Delete,
                    Source = archive.Path,
                    Reason = $"rotate = {job.Rotate} keeps the newest {job.Rotate} archive(s)",
                    Bytes = archive.Length,
                });
            }
        }

        // Deferred compression from the previous run, if any remain uncompressed and survive.
        if (job is { DelayCompress: true, CompressType: not CompressType.None })
        {
            foreach (var stale in generation.Archives.Where(a => !a.IsCompressed && !condemned.Contains(a.Path)))
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
    }

    /// <summary>
    /// What counts as one dated generation: the parsed stamp, or the path where none parsed.
    /// </summary>
    /// <remarks>
    /// Falling back to the path rather than to a shared null keeps an unparseable name its own
    /// generation. Collapsing them would make every such file one generation between them, which
    /// is how a retention pass deletes a pile of archives it never counted.
    /// </remarks>
    private static string DatedGeneration(ArchiveFile archive) =>
        archive.Stamp is { } stamp
            ? stamp.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
            : archive.Path;

    /// <param name="discard">
    /// Whether the archive this rotation produces is kept. <c>rotate = 0</c> keeps no generations,
    /// so it is deleted in the same pass and never compressed on the way.
    /// </param>
    /// <remarks>
    /// The log is still rotated rather than simply emptied, and that is not decoration: the
    /// rotation clock advances from <c>ExecutionResult.Rotated</c>, which the executor fills from
    /// renames and copies alone. A plan for <c>rotate = 0</c> that deleted the live log instead of
    /// moving it would never mark the log as rotated, so the job would be due again every time it
    /// was considered, for ever.
    /// </remarks>
    private static void AddLiveRotation(
        EffectiveJob job, MatchedFile live, string target, List<PlannedOp> operations,
        DueVerdict verdict)
    {
        var discard = job.Rotate == 0;

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
        // delaycompress is set - that is the entire point of the directive. And an archive this
        // same pass is about to discard is not worth reading and writing first.
        if (job.CompressType != CompressType.None && !job.DelayCompress && !discard)
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

        if (discard)
        {
            operations.Add(new PlannedOp
            {
                Action = PlannedAction.Delete,
                Source = target,
                Reason = "rotate = 0 keeps no generations",
                Bytes = live.Length,
            });
        }
    }

    /// <summary>
    /// Condemns every archive past <c>maxage</c>, and reports which.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This ran last, after the shift had already been queued, and condemned
    /// <c>archive.Path</c> - the name the file had <b>before</b> the shift. A plan is a list
    /// executed in order, so by the time the deletion was reached the shift had moved a different
    /// file under that name. With <c>rotate = 10</c>, <c>maxage = 30</c>, yesterday's
    /// <c>app.log.1.gz</c> and a 45-day-old <c>app.log.2.gz</c>, the plan renamed the old one to
    /// <c>.3.gz</c>, renamed yesterday's to <c>.2.gz</c>, and then deleted <c>.2.gz</c> - taking
    /// yesterday's archive and reporting, in the journal and in the envelope, that it had removed
    /// a file from a month ago. The one thing this reason string exists to say was the one thing
    /// it got wrong.
    /// </para>
    /// <para>
    /// Deciding first, against the names on disk, is what makes the sentence true. The shift then
    /// sees a gap, which it already treats as ordinary - <c>LogSeries</c> probes the whole window
    /// because "a gap in the chain is ordinary - somebody deleted one".
    /// </para>
    /// <para>
    /// Where both rules condemn one file, age is now the reason recorded rather than the count.
    /// That is the deliberate consequence of deciding first: age is the rule evaluated against the
    /// name the file actually has, and the count is a statement about a position the file has not
    /// reached yet.
    /// </para>
    /// </remarks>
    /// <returns>The paths condemned, so no later pass acts on a file this one removed.</returns>
    private static HashSet<string> AddMaxAgeDeletions(
        EffectiveJob job, IReadOnlyList<ArchiveFile> archives,
        List<PlannedOp> operations, DateTimeOffset now)
    {
        var condemned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (job.MaxAge is not { } maxAge)
        {
            return condemned;
        }

        var cutoff = now.AddDays(-maxAge);

        foreach (var archive in archives)
        {
            var age = archive.Stamp ?? archive.LastWriteUtc;
            if (age >= cutoff || !condemned.Add(archive.Path))
            {
                continue;
            }

            operations.Add(new PlannedOp
            {
                Action = PlannedAction.Delete,
                Source = archive.Path,
                Reason = $"maxage = {maxAge} days; this one is from {age:yyyy-MM-dd}",
                Bytes = archive.Length,
            });
        }

        return condemned;
    }
}
