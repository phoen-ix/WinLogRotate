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
    IWriterInspector? inspector = null)
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
    private readonly FileEnumerator _files = new(guard);

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

            var matched = new List<MatchedFile>();
            var refused = false;
            foreach (var pattern in job.Paths)
            {
                var decision = guard.CheckPattern(pattern);
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
                    var diagnostic = Diagnose.Refusal(refusal, job.Name);

                    // On severity, not on "there was a refusal at all". The flag exists to stop a
                    // single cause being counted twice, and a Warning is not a failure - so a
                    // link we merely could not verify must still let "matched no files" through,
                    // or a job that fails today would start exiting 0 in silence.
                    refused |= diagnostic.Severity >= Severity.Error;
                    Report(diagnostic);
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

            var count = guard.CheckMatchCount(job.Name, matched.Count);
            if (!count.IsAllowed)
            {
                Report(Diagnose.Refusal(count, job.Name));
                continue;
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

        Settle(path, judged, now);

        if (judged.Diagnostic is { } d)
        {
            report(d);
        }
    }

    /// <summary>Writes a reached verdict, and the numbers that reached it.</summary>
    private void Settle(string path, NulFillJudgement judged, DateTimeOffset now) =>
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

        foreach (var file in matched)
        {
            // Always recorded, whatever --catchup says. RotationCriteria refuses a log with no
            // recorded rotation - "anyone who has deleted a state file and wondered why nothing
            // rotated that night has met this rule" - so a first sighting that skipped this would
            // leave the clock null for ever and the log would never become due at all.
            var first = state.RecordFirstSighting(file.Path, now);

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

            // Phase A - judge the truncation recorded last time, whether or not the log is due.
            // Gating this on dueness would leave a monthly job's evidence unexamined for a month,
            // and a minsize-suppressed job's unexamined for ever.
            if (state.Get(file.Path) is { LastTruncatedFrom: not null } pending)
            {
                var sample = _inspector.Sample(file.Path, pending.LastTruncatedTo ?? 0);
                var judged = LockChoice.Judge(job.Name, file.Path, pending, sample);

                if (!options.DryRun)
                {
                    Settle(file.Path, judged, now);
                }

                if (judged.Diagnostic is { } verdictNews)
                {
                    report(verdictNews);
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

        return RotateJobPlanner.Plan(job, LogSeries.Discover(job, consider, _archives), due, now);
    }
}
