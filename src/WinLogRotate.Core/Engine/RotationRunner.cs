using WinLogRotate.Contracts;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Globbing;
using WinLogRotate.Core.Hooks;
using WinLogRotate.Core.Io;
using WinLogRotate.Core.Journaling;
using WinLogRotate.Core.Safety;
using WinLogRotate.Core.State;

namespace WinLogRotate.Core.Engine;

/// <summary>Options for one invocation.</summary>
public sealed record RunOptions
{
    public bool DryRun { get; init; }
    public bool Force { get; init; }

    /// <summary>Rotate a log the first time it is seen instead of recording a baseline.
    /// Not logrotate behaviour, hence opt-in.</summary>
    public bool Catchup { get; init; }

    public string? OnlyJob { get; init; }

    /// <summary>
    /// Whether this run reports its outcome to notification targets.
    /// </summary>
    /// <remarks>
    /// Defaults to true so the scheduled task - which passes nothing - notifies. The GUI's
    /// "run now" passes --no-notify, because a button somebody pressed while watching should not
    /// page whoever is on call.
    /// </remarks>
    public bool Notify { get; init; } = true;

    /// <summary>
    /// How long the whole invocation has before its host kills it, or null when nothing will.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Passed by the registered scheduled task as <c>--run-deadline</c>, generated from the same
    /// value that writes the task's <c>ExecutionTimeLimit</c> element. It exists so the
    /// notification phase can stop short rather than let a fifty-nine-minute rotation plus a
    /// thirty-second notification phase reach the one-hour limit: Task Scheduler reports that
    /// termination as <c>0x41306</c>, which is indistinguishable from an operator pressing Stop,
    /// so a rotation that actually succeeded leaves evidence that says it was killed.
    /// </para>
    /// <para>
    /// Null for a hand-run rotation, which is never truncated - nothing is going to kill it, and
    /// inventing a deadline would make the interactive case behave differently from the scheduled
    /// one for no benefit.
    /// </para>
    /// </remarks>
    public TimeSpan? RunDeadline { get; init; }

    /// <summary>
    /// When the invocation began, or null to measure from when the rotation itself starts.
    /// </summary>
    /// <remarks>
    /// Paired with <see cref="RunDeadline"/>, and it has to be the process's own start rather than
    /// the runner's: configuration loading, journal maintenance and taking the rotation gate all
    /// happen first and all count against the scheduled task's limit. Measuring from here would
    /// hand a hook time that has already been spent.
    /// </remarks>
    public DateTimeOffset? Started { get; init; }
}

/// <summary>What a whole run did.</summary>
public sealed record RunReport
{
    public required string RunId { get; init; }
    public required int JobsConsidered { get; init; }
    public required int JobsRun { get; init; }
    public required int Completed { get; init; }
    public required int Failed { get; init; }
    public required long BytesFreed { get; init; }
    public required IReadOnlyList<string> Errors { get; init; }

    /// <summary>
    /// The same failures as <see cref="Errors"/>, carrying the severity, the stable code, the
    /// Win32 error and the job that produced them.
    /// </summary>
    /// <remarks>
    /// <see cref="Errors"/> keeps the flat strings the <c>--json</c> envelope has always
    /// carried, so nothing downstream breaks. This is what a caller groups by: attributing a
    /// failure to its job and its error class is impossible from prose.
    /// </remarks>
    public IReadOnlyList<CliDiagnostic> Diagnostics { get; init; } = [];

    public required IReadOnlyList<JobPlan> Plans { get; init; }

    public int ExitCode => Failed > 0 ? Core.ExitCode.Errors : Core.ExitCode.Ok;
}

/// <summary>Runs every due job.</summary>
public sealed class RotationRunner(
    IJournal journal, PathGuard guard, StateStore state, TimeProvider clock,
    IArchiveSource? archiveSource = null, IHookHost? hookHost = null, HookGate? hookGate = null,
    IWriterInspector? inspector = null, IFileSource? files = null)
{
    /// <summary>
    /// What can be asked of a live log: what its writer permits, and what it looks like now.
    /// </summary>
    /// <remarks>
    /// Optional and defaulted for <see cref="IArchiveSource"/>'s reason. Unlike the hook gate it
    /// defaults to the real thing rather than to refusing: there is no trust decision here, a
    /// probe opens a handle and closes it, and a wrong answer costs a fallback to rename - the
    /// documented default, which fails loudly.
    /// </remarks>
    private readonly IWriterInspector _inspector = inspector ?? new WriterInspector();

    /// <summary>
    /// Which class of token this run's probes were reached with.
    /// </summary>
    /// <remarks>
    /// Taken once, for the reason the hook gate is: nothing inside one run changes it. Recorded
    /// beside every verdict because a verdict reached by SYSTEM says nothing about what a desktop
    /// user can do to the same file - which is what <see cref="ProbeIdentity"/> exists to say, and
    /// what nothing has ever recorded.
    /// </remarks>
    private readonly ProbeIdentity _identity =
        Privilege.IsElevated() ? ProbeIdentity.Elevated : ProbeIdentity.Standard;

    /// <summary>
    /// The bracket around each job's execution.
    /// </summary>
    /// <remarks>
    /// Both halves are optional and both default to refusing. Core cannot reference Hosting, where
    /// process spawning and the ACL check live, so the host and the gate are handed in by the CLI
    /// - and a runner built without them runs no hooks at all rather than running them ungated. A
    /// gate that opens because nobody supplied one is indistinguishable from no gate.
    /// </remarks>
    private readonly HookRunner _hooks =
        new(journal, hookHost, hookGate ?? HookGate.Unknown, clock);

    /// <summary>
    /// Where a rotate job looks for the archives it wrote last time.
    /// </summary>
    /// <remarks>
    /// Optional, so every existing caller is unchanged, and injectable because discovery is the
    /// code that decides which files the planner may delete - and that decision deserves tests
    /// that do not need a file system.
    /// </remarks>
    /// <summary>
    /// One enumerator for the run, so a link is resolved and reported once however many patterns
    /// or jobs meet it.
    /// </summary>
    private readonly IFileSource _files = files ?? new FileEnumerator(guard);

    private readonly IArchiveSource _archives = archiveSource ?? new FileArchiveSource(new FileEnumerator(guard));

    public RunReport Run(LoadedConfig config, RunOptions options)
    {
        var now = clock.GetUtcNow();
        var plans = new List<JobPlan>();
        var errors = new List<string>();
        var diagnostics = new List<CliDiagnostic>();
        var completed = 0;
        var failed = 0;
        long freed = 0;
        var jobsRun = 0;

        // Both lists are appended together, always. Keeping them in step is the whole contract
        // between the flat strings the envelope carries and the classified form callers group by.
        //
        // It also counts, which it did not: every call site incremented `failed` separately, so a
        // diagnostic raised from inside PlanRotation could be an Error and cost nothing. And the
        // FirstRunBaseline Info went into `errors`, which RunCommand publishes as the --json
        // errors array - so a healthy first run reported an error it had not had.
        void Report(CliDiagnostic d)
        {
            if (d.Severity >= Severity.Error)
            {
                failed++;
            }

            if (d.Severity >= Severity.Warning)
            {
                errors.Add(d.Message);
            }

            diagnostics.Add(d);
        }

        journal.Write(new CliEvent
        {
            Ts = string.Empty,
            Run = string.Empty,
            Operation = Op.RunStart,
            Phase = options.DryRun ? Phase.Plan : Phase.Apply,
            Reason = options.DryRun ? "dry run" : null,
        });

        // What was truncated this run, so the same run can look at the result. The callback has
        // existed since copytruncate did and was never once assigned, so the number it offers was
        // computed on every truncation and dropped on the floor every time.
        var truncated = new Dictionary<string, TruncationOutcome>(StringComparer.OrdinalIgnoreCase);

        var executor = new PlanExecutor(journal, guard, clock)
        {
            RecordTruncation = (path, outcome) => truncated[path] = outcome,
        };

        foreach (var job in config.Jobs)
        {
            if (!job.Enabled)
            {
                continue;
            }

            if (options.OnlyJob is { } only
                && !string.Equals(job.Name, only, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Brackets the job's operations, the way run.start/run.end bracket the run.
            // Emitted after the enabled and --job filters, so the journal does not record a job
            // this invocation was never going to look at.
            journal.Write(new CliEvent
            {
                Ts = string.Empty,
                Run = string.Empty,
                Operation = Op.JobStart,
                Phase = options.DryRun ? Phase.Plan : Phase.Apply,
                Job = job.Name,
            });

            // Counted before the job runs so the closing event can say whether this job failed,
            // rather than whether anything had failed by the time it finished.
            var failedBefore = failed;

            // try/finally rather than an emit before each exit: the body leaves four different
            // ways - a refused pattern, a refused count, no files matched, an abandoned
            // prerotate - and a closing event that four separate returns have to remember is one
            // somebody eventually forgets.
            try
            {
                var matched = new List<MatchedFile>();
                var refused = false;
                foreach (var pattern in job.Paths)
                {
                    var decision = guard.CheckPattern(pattern, job.GuardScope);

                    // Journaled either way. A refusal is a security decision and an override is a
                    // security exception, and until now both left nothing behind but a console
                    // line that scrolls away - while GuardDecision.Overridden's own doc comment
                    // promised "always journaled". Six months later, "when did this machine start
                    // deleting inside System32, and who permitted it" had no answer anywhere.
                    JournalGuard(job.Name, decision);

                    if (!decision.IsAllowed)
                    {
                        refused = true;
                        Report(Diagnose.Refusal(decision, job.Name));
                        continue;
                    }

                    var found = _files.Resolve(pattern);

                    // A link we would not follow is named, every time. This used to be a bare
                    // `continue` inside the walk, so a junctioned log directory was never rotated and
                    // nothing anywhere said so.
                    foreach (var refusal in found.Refusals)
                    {
                        JournalGuard(job.Name, refusal);

                        var diagnostic = Diagnose.Refusal(refusal, job.Name);

                        // On severity, not on "there was a refusal at all". The flag exists to stop a
                        // single cause being counted twice, and a Warning is not a failure - so a
                        // link we merely could not verify must still let "matched no files" through,
                        // or a job that fails today would start exiting 0 in silence.
                        refused |= diagnostic.Severity >= Severity.Error;
                        Report(diagnostic);
                    }

                    // Per pattern, which is what maxfiles has always been documented to mean -
                    // JobSettings.MaxFiles, GuardOptions.MaxMatches and docs/configuration.md all
                    // say "a pattern". Enforced over the job's accumulated total, five patterns
                    // of 250 files were refused at the default of 1000 though none was close to
                    // it, and the message read "'iis-logs' matches 1,240 files" - naming a job
                    // where the operator needs to know which of five patterns to narrow.
                    var count = guard.CheckMatchCount(pattern, found.Files.Count, job.GuardScope);
                    if (!count.IsAllowed)
                    {
                        // The same flag the pattern refusal above sets, so "matched no files"
                        // does not arrive on top of this as if it were a second problem. And
                        // continue to the next pattern, not the next job: one runaway pattern
                        // used to abandon every healthy pattern beside it.
                        refused = true;
                        Report(Diagnose.Refusal(count, job.Name));
                        continue;
                    }

                    matched.AddRange(found.Files);
                }

                // Whatever the archive glob refused, reported against this job and then cleared, so a
                // later job cannot inherit it.
                if (_archives is FileArchiveSource source)
                {
                    foreach (var refusal in source.Refused)
                    {
                        Report(Diagnose.Refusal(refusal, job.Name));
                    }

                    source.Refused.Clear();
                }

                // Not "&& !refused": a pattern the guard turned down has already been reported, with
                // the actual reason and the actual fix. Adding "matched no files" on top describes
                // the consequence as if it were a second, separate problem - which doubles the
                // failure count and puts two lines in front of an operator for one cause.
                if (matched.Count == 0 && !job.MissingOk && !refused)
                {
                    Report(new CliDiagnostic
                    {
                        Severity = Severity.Error,
                        Code = DiagnosticCode.FileMissing,
                        Message = $"[{job.Name}] matched no files and missingok is not set.",
                        Job = job.Name,
                        Remedy = "Set missingok = true if this job's logs are not always present.",
                    });
                    continue;
                }

                var plan = job.Kind == JobKind.Manage
                    ? ManageJobPlanner.Plan(job, matched, now)
                    : PlanRotation(job, matched, options, now, Report);

                // Only when a live log is actually moving. A job whose plan is "compress an archive
                // from last month" has not rotated anything, and a reload hook that fired for it would
                // signal a service on a night nothing happened.
                var rotating = plan.RotatesALiveLog;
                var started = options.Started ?? now;

                if (rotating)
                {
                    var pre = _hooks.Run(
                        job.Name, HookStage.PreRotate, job.PreRotate, job.HookTimeout,
                        options.DryRun, started, options.RunDeadline, job.SourceFile);

                    foreach (var d in pre.Diagnostics)
                    {
                        Report(d);
                    }

                    // logrotate's asymmetry, and the reason the two stages are not one loop: a
                    // prerotate hook is the job's precondition. If the service did not stop or the
                    // buffer was not flushed, rotating anyway does the exact damage the hook was
                    // written to prevent - so nothing is touched, and the diagnostic above says so.
                    if (!pre.Ok)
                    {
                        // Reported as what happened rather than as what was intended. Adding the
                        // original plan here would print "did rename C:\logs\app.log" for a file
                        // that was never touched - the plan is the engine's intention, and once the
                        // job is abandoned the intention is not what an operator needs to read.
                        plans.Add(Abandoned(plan, "the prerotate hook did not succeed"));
                        jobsRun++;
                        continue;
                    }
                }

                plans.Add(plan);
                jobsRun++;

                var result = executor.Execute(plan, job, options.DryRun);
                completed += result.Completed;
                failed += result.Failed;
                freed += result.BytesFreed;
                errors.AddRange(result.Errors);
                diagnostics.AddRange(result.Diagnostics);

                // The clock advances from what the executor actually did, never from what was
                // planned. A dry run reports no moves and so writes no state, which is what makes
                // --dry-run safe against production: it cannot change when anything next rotates.
                foreach (var path in result.Rotated)
                {
                    state.Update(path, existing => existing with { LastRotated = now });
                }

                // From what the executor did, not from what was planned - except under --dry-run,
                // where nothing was done and the plan is the only thing there is to describe. A
                // postrotate hook that ran after every rename failed on a share violation would be
                // reporting a rotation that did not happen.
                if (rotating && (options.DryRun || result.Rotated.Count > 0))
                {
                    var post = _hooks.Run(
                        job.Name, HookStage.PostRotate, job.PostRotate, job.HookTimeout,
                        options.DryRun, started, options.RunDeadline, job.SourceFile);

                    // The other half of the asymmetry. The files have already moved, so there is
                    // nothing to undo and nothing to skip: the rotation stands and the failure is
                    // reported against the job. Rolling a rotation back because a reload script
                    // exited 1 would be much the more surprising of the two.
                    foreach (var d in post.Diagnostics)
                    {
                        Report(d);
                    }
                }

                // After the hook, deliberately. service:paramchange: is the Windows kill -HUP: it
                // tells the writer to reopen, which is precisely the remedy for a cached offset.
                // Looking before it would quarantine a path whose hook fixes it every single night.
                foreach (var (path, outcome) in truncated)
                {
                    Verify(job, path, outcome, now, Report);
                }

                truncated.Clear();
            }
            finally
            {
                journal.Write(new CliEvent
                {
                    Ts = string.Empty,
                    Run = string.Empty,
                    Operation = Op.JobEnd,
                    Phase = options.DryRun ? Phase.Plan : Phase.Apply,
                    Job = job.Name,
                    Result = failed > failedBefore ? OpResult.Failed : OpResult.Ok,
                });
            }
        }

        journal.Write(new CliEvent
        {
            Ts = string.Empty,
            Run = string.Empty,
            Operation = Op.RunEnd,
            Phase = options.DryRun ? Phase.Plan : Phase.Apply,
            Result = failed > 0 ? OpResult.Failed : OpResult.Ok,
        });

        // State is written even when jobs failed - deliberately, and matching logrotate. A
        // failing job must not cause the healthy ones to re-rotate on every subsequent run.
        if (!options.DryRun)
        {
            // Only on a run that looked at everything. A --job run has not seen the other paths,
            // so ageing them off would forget a clock because of a filter - the same mistake
            // NotifyStateStore.Prune's own remarks refuse to make about --job, in its state-file
            // form. Nothing is reported: every row this can remove is a clock for a path no run
            // has matched in three months, and the one row that would matter is never removed.
            if (options.OnlyJob is null)
            {
                state.Prune(now, TimeSpan.FromDays(StateStore.ForgetAfterDays));
            }

            state.Save(clock);
        }

        return new RunReport
        {
            RunId = journal.RunId,
            JobsConsidered = config.Jobs.Count,
            JobsRun = jobsRun,
            Completed = completed,
            Failed = failed,
            BytesFreed = freed,
            Errors = errors,
            Diagnostics = diagnostics,
            Plans = plans,
        };
    }

    /// <summary>
    /// The diagnostic for a log that was due and was held back, or null for anything else.
    /// </summary>
    /// <remarks>
    /// Info, not Warning: nothing is wrong. These are the gates working, and an operator reading
    /// one wants the reason rather than an alarm. Both codes have been published in
    /// <c>docs/diagnostics.md</c> since before they had anything to raise them.
    /// </remarks>
    private static CliDiagnostic? Suppression(DueVerdict verdict) => verdict.Reason switch
    {
        DueReason.Empty => new CliDiagnostic
        {
            Severity = Severity.Info,
            Code = DiagnosticCode.FileEmpty,
            Message = verdict.Explanation,
            Remedy = "Set notifempty = false to rotate an empty log anyway.",
        },

        DueReason.TooSmall or DueReason.TooYoung => new CliDiagnostic
        {
            Severity = Severity.Info,
            Code = DiagnosticCode.NotDueYet,
            Message = verdict.Explanation,
            Remedy = "minsize and minage hold a rotation back until a log is big enough or old "
                   + "enough. It will rotate on the first run after that.",
        },

        _ => null,
    };

    /// <summary>
    /// Looks at a log we truncated moments ago, and records only bad news.
    /// </summary>
    /// <remarks>
    /// A confirmed NUL-fill here saves a whole interval: the alternative is learning about it on
    /// the next run, which for a daily job is one more multi-gigabyte night. Anything else is
    /// <b>not</b> recorded, because a clean reading this soon may only mean the writer has not
    /// written yet - the baseline is left in place for the next run to settle with a full
    /// interval of evidence behind it.
    /// </remarks>
    private void Verify(
        EffectiveJob job, string path, TruncationOutcome outcome, DateTimeOffset now,
        Action<CliDiagnostic> report)
    {
        state.Update(path, existing => existing with
        {
            LastTruncatedFrom = outcome.SizeBefore,
            LastTruncatedTo = outcome.SizeAfter,
            LastTruncatedAt = now,
            TruncationChecks = 0,
        });

        var sample = _inspector.Sample(path, outcome.SizeAfter);
        var judged = LockChoice.Judge(job.Name, path, state.Get(path)!, sample);

        if (judged.Verdict != NulFillVerdict.Confirmed)
        {
            // Including the file's identity, so the next run can tell "the writer behaved" from
            // "somebody replaced the file".
            if (sample.Identity is not null)
            {
                state.Update(path, existing => existing with { FileIdentity = sample.Identity });
            }

            return;
        }

        Settle(job.Name, path, judged, now);

        if (judged.Diagnostic is { } d)
        {
            report(d);
        }
    }

    /// <summary>Writes a reached verdict, and the numbers that reached it.</summary>
    /// <summary>
    /// Records what the guard decided about a path, where it decided anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Allowed-and-unremarkable is not written: the journal is size-capped and rotated by this
    /// product's own maintenance pass, and a line per permitted pattern per run would crowd out
    /// the operations it exists to record. What is written is the two answers somebody may have
    /// to account for later - we refused to touch something, or we touched something we would
    /// normally refuse.
    /// </para>
    /// <para>
    /// The subject rather than the pattern, because a link refusal names the link and a pattern
    /// refusal names the pattern, and in both cases that is the thing an operator has to go and
    /// look at.
    /// </para>
    /// </remarks>
    private void JournalGuard(string job, GuardDecision decision)
    {
        if (decision.IsAllowed && !decision.Overridden)
        {
            return;
        }

        journal.Write(new CliEvent
        {
            Ts = string.Empty,
            Run = string.Empty,
            Operation = decision.Overridden ? Op.GuardOverride : Op.GuardRefuse,
            Phase = Phase.Plan,
            Result = decision.Overridden ? OpResult.Ok : OpResult.Skipped,
            Job = job,
            Src = decision.Subject,
            Reason = decision.Message,
        });
    }

    /// <param name="job">
    /// For the journal line. A NUL-fill verdict is permanent for the path, so it is one of the
    /// few decisions this product makes that somebody may have to account for months later -
    /// and until now it produced a diagnostic at the time and nothing queryable afterwards.
    /// </param>
    private void Settle(string job, string path, NulFillJudgement judged, DateTimeOffset now)
    {
        if (judged.Verdict is { } reached)
        {
            journal.Write(new CliEvent
            {
                Ts = string.Empty,
                Run = string.Empty,
                Operation = Op.NulFill,
                Phase = Phase.Apply,

                // Confirmed means a log was destroyed once already and copytruncate is refused
                // for this path from now on. Clean is the ordinary answer and is recorded too,
                // because "we looked and it was fine" is what makes the absence of a Confirmed
                // line mean something.
                Result = reached == NulFillVerdict.Confirmed ? OpResult.Failed : OpResult.Ok,
                Job = job,
                Src = path,
                Reason = judged.Evidence.ToString(),
                BytesBefore = judged.SizeBefore ?? 0,
                BytesAfter = judged.SizeAfter,
            });
        }

        state.Update(path, existing => existing with
        {
            NulFill = judged.Verdict ?? existing.NulFill,
            NulFillEvidence = judged.Verdict is not null ? judged.Evidence : existing.NulFillEvidence,
            NulFillAt = judged.Verdict is not null ? now : existing.NulFillAt,
            NulFillSizeBefore = judged.SizeBefore ?? existing.NulFillSizeBefore,
            NulFillSizeAfter = judged.SizeAfter ?? existing.NulFillSizeAfter,
            LastTruncatedFrom = judged.ClearBaseline ? null : existing.LastTruncatedFrom,
            LastTruncatedTo = judged.ClearBaseline ? null : existing.LastTruncatedTo,
            TruncationChecks = judged.ClearBaseline ? 0 : existing.TruncationChecks + 1,
        });
    }

    /// <summary>
    /// The same plan, restated as the nothing that actually happened.
    /// </summary>
    /// <remarks>
    /// One line per file rather than one per operation: a log that was going to be renamed,
    /// compressed and have its oldest generation deleted was not touched once, and saying so three
    /// times reads like three separate decisions.
    /// </remarks>
    private static JobPlan Abandoned(JobPlan plan, string reason) => plan with
    {
        Operations =
        [
            .. plan.Operations
                .DistinctBy(o => o.Source, StringComparer.OrdinalIgnoreCase)
                .Select(o => new PlannedOp
                {
                    Action = PlannedAction.Skip,
                    Source = o.Source,
                    Reason = reason,
                }),
        ],
    };

    /// <summary>
    /// Decides what a rotate job should do to each of its logs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the branch that did not exist. The runner skipped every job whose kind was not
    /// <c>manage</c>, behind a comment saying rotate would land in milestone 8 - and
    /// <see cref="JobKind.Rotate"/> is the DEFAULT, so a job that simply did not say which kind
    /// it was did nothing at all, silently, and reported success.
    /// </para>
    /// <para>
    /// Everything it calls was already written and tested and had no production caller.
    /// <see cref="LogSeries.Discover"/> is new; <see cref="RotationCriteria.Evaluate"/>,
    /// <see cref="RotateJobPlanner.Plan"/> and <see cref="StateStore.RecordFirstSighting"/> were
    /// all reachable only from tests.
    /// </para>
    /// </remarks>
    internal JobPlan PlanRotation(
        EffectiveJob job, IReadOnlyList<MatchedFile> matched, RunOptions options,
        DateTimeOffset now, Action<CliDiagnostic> report)
    {
        var due = new Dictionary<string, DueVerdict>(StringComparer.OrdinalIgnoreCase);
        var consider = new List<MatchedFile>(matched.Count);

        // Per distinct archive directory, not per file. A relative olddir resolves against each
        // log's own directory - matching logrotate - so a job can legitimately have several, while
        // the ordinary case of forty logs sharing one pays a single Exists and a single guard call.
        var oldDirs = new Dictionary<string, OldDirDecision>(StringComparer.OrdinalIgnoreCase);
        var creates = new List<PlannedOp>();

        foreach (var file in matched)
        {
            // Always recorded, whatever --catchup says. RotationCriteria refuses a log with no
            // recorded rotation - "anyone who has deleted a state file and wondered why nothing
            // rotated that night has met this rule" - so a first sighting that skipped this would
            // leave the clock null for ever and the log would never become due at all.
            var first = state.RecordFirstSighting(file.Path, now);

            // Every sighting, rotated or not. Prune ages off this rather than LastRotated, which
            // only advances on an actual rotation - so a log held back every night by notifempty
            // or minsize would otherwise look untouched for ever and be forgotten while still
            // being watched. Not gated on --dry-run: state.Save is the single place that enforces
            // a dry run changing nothing, and RecordFirstSighting above works the same way.
            state.Update(file.Path, existing => existing with { LastSeen = now });

            // A log this machine has never seen is recorded, not rotated. Without that, the first
            // night after installing rotates every log on the server at once, purely because none
            // of them has a recorded rotation yet - indistinguishable from the tool
            // malfunctioning, and how a product gets uninstalled on day one.
            if (first && !options.Catchup)
            {
                report(new CliDiagnostic
                {
                    Severity = Severity.Info,
                    Code = DiagnosticCode.FirstRunBaseline,
                    Message = $"[{job.Name}] {file.Path} was seen for the first time, so its "
                            + "clock starts now rather than rotating it immediately.",
                    Job = job.Name,
                    Path = file.Path,
                    Remedy = "Pass --catchup to rotate a log the first time it is seen instead.",
                });
            }

            // Kept in the plan even when it is being baselined, rather than dropped. JobPlan
            // documents MatchedFiles as what the job matched "whether or not it plans to act on
            // them", and dropping it made a dry run report "0 file(s) matched" for a directory
            // that plainly had one - while saying nothing at all about the file it had silently
            // set aside. PlannedAction.Skip exists precisely so that "why was this file not
            // touched?" has an answer.
            consider.Add(file);

            // Synthesised rather than evaluated in both first-sighting cases: the baseline above
            // has just set the clock to now, so asking the criteria would answer "rotated a moment
            // ago" - which is true, and useless, and would make --catchup silently do nothing.
            due[file.Path] = first
                ? new DueVerdict
                {
                    Due = options.Catchup,
                    Reason = DueReason.FirstSighting,
                    Explanation = options.Catchup
                        ? "--catchup: rotating on the first sighting rather than baselining"
                        : "first time this log has been seen; its clock starts now",
                }
                : RotationCriteria.Evaluate(
                    job, state.Get(file.Path)?.LastRotated, now, file.Length, file.LastWriteUtc,
                    options.Force);

            // A log that was due and was held back anyway is worth saying out loud. "Why did this
            // not rotate last night?" is the question an operator actually asks, and the three
            // gates that answer it - notifempty, minsize, minage - used to produce a verdict
            // nothing read and a Skip line nobody sees without --verbose.
            //
            // DueReason.NotDue is deliberately absent: "the schedule says not tonight" is the
            // ordinary case for most logs on most runs, and reporting it would bury the rest.
            if (Suppression(due[file.Path]) is { } suppressed)
            {
                report(suppressed with { Job = job.Name, Path = file.Path });
            }

            // Phase A - judge the truncation recorded last time, whether or not the log is due.
            // Gating this on dueness would leave a monthly job's evidence unexamined for a month,
            // and a minsize-suppressed job's unexamined for ever.
            if (state.Get(file.Path) is { LastTruncatedFrom: not null } pending)
            {
                var sample = _inspector.Sample(file.Path, pending.LastTruncatedTo ?? 0);
                var judged = LockChoice.Judge(job.Name, file.Path, pending, sample);

                if (!options.DryRun)
                {
                    Settle(job.Name, file.Path, judged, now);
                }

                if (judged.Diagnostic is { } verdictNews)
                {
                    report(verdictNews);
                }
            }

            // Where the archive is going to land, before the strategy is resolved. A job that
            // cannot write its archives should not pay a probe per file for a decision nobody will
            // act on, and must not leave a probe verdict in state for a rotation that was never
            // going to happen.
            if (due[file.Path].Due && job.OldDir is { Length: > 0 })
            {
                var directory = ArchiveNaming.ResolveDirectory(job, file.Path);

                if (!oldDirs.TryGetValue(directory, out var destination))
                {
                    // The guard first, then existence. Asked the other way round, createolddir
                    // could make a directory somewhere we would then refuse to write to.
                    destination = OldDirGate.Check(
                        job, directory, Directory.Exists(directory),
                        guard.CheckPath(directory, job.GuardScope));

                    oldDirs[directory] = destination;

                    if (destination.Diagnostic is { } news)
                    {
                        report(news);
                    }

                    if (destination.Create is { } mkdir)
                    {
                        creates.Add(mkdir);
                    }
                }

                if (!destination.Usable)
                {
                    // Reported as not due, the way StrategyRefused is, so the planner's existing
                    // Skip path carries the reason, the clock does not advance and the hooks do
                    // not fire. The diagnostic above has already said what is wrong, once.
                    due[file.Path] = new DueVerdict
                    {
                        Due = false,
                        Reason = DueReason.DestinationUnusable,
                        Explanation = $"nothing can be written to '{directory}'",
                    };
                }
            }

            // Phase B - resolve the strategy, for a log that is actually going to move. Probing a
            // file nothing will touch is an open per file per run buying a decision nobody acts on.
            if (due[file.Path].Due)
            {
                var decision = LockChoice.Choose(
                    job, file.Path, state.Get(file.Path)?.NulFill ?? NulFillVerdict.Unknown,
                    _inspector);

                if (decision.Diagnostic is { } choiceNews)
                {
                    report(choiceNews);
                }

                if (decision.Probed && !options.DryRun)
                {
                    state.Update(file.Path, existing => existing with
                    {
                        Probe = decision.Probe,
                        ProbedAs = _identity,
                        ProbedAt = now,
                        ProbeError = decision.ProbeError,
                    });
                }

                due[file.Path] = decision.Strategy is { } chosen
                    ? due[file.Path] with { Strategy = chosen, Explanation = decision.Explanation }

                    // Nothing can touch it. Reported as not due rather than as a special case,
                    // so the planner's existing Skip path carries the reason, the clock does not
                    // advance and the hooks do not fire - all of which is already correct.
                    : new DueVerdict
                    {
                        Due = false,
                        Reason = DueReason.StrategyRefused,
                        Explanation = decision.Explanation,
                    };
            }
        }

        var plan = RotateJobPlanner.Plan(job, LogSeries.Discover(job, consider, _archives), due, now, report);

        // Prepended rather than threaded through the planner, which stays pure and knows nothing
        // about the file system. A directory has to exist before anything is moved into it.
        return creates.Count == 0
            ? plan
            : plan with { Operations = [.. creates, .. plan.Operations] };
    }
}
