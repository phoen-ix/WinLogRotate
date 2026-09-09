using WinLogRotate.Contracts;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Globbing;
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
    IArchiveSource? archiveSource = null)
{
    /// <summary>
    /// Where a rotate job looks for the archives it wrote last time.
    /// </summary>
    /// <remarks>
    /// Optional, so every existing caller is unchanged, and injectable because discovery is the
    /// code that decides which files the planner may delete - and that decision deserves tests
    /// that do not need a file system.
    /// </remarks>
    private readonly IArchiveSource _archives = archiveSource ?? new FileArchiveSource();

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
        void Fail(CliDiagnostic d)
        {
            errors.Add(d.Message);
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

        var executor = new PlanExecutor(journal, guard, clock);

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
                    failed++;
                    refused = true;
                    Fail(Diagnose.Refusal(decision, job.Name));
                    continue;
                }

                matched.AddRange(FileEnumerator.Resolve(pattern));
            }

            var count = guard.CheckMatchCount(job.Name, matched.Count);
            if (!count.IsAllowed)
            {
                failed++;
                Fail(Diagnose.Refusal(count, job.Name));
                continue;
            }

            // Not "&& !refused": a pattern the guard turned down has already been reported, with
            // the actual reason and the actual fix. Adding "matched no files" on top describes
            // the consequence as if it were a second, separate problem - which doubles the
            // failure count and puts two lines in front of an operator for one cause.
            if (matched.Count == 0 && !job.MissingOk && !refused)
            {
                failed++;
                Fail(new CliDiagnostic
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
                : PlanRotation(job, matched, options, now, Fail);

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
        }

        return RotateJobPlanner.Plan(job, LogSeries.Discover(job, consider, _archives), due, now);
    }
}
