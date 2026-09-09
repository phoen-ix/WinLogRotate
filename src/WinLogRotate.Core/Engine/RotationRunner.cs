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
    IJournal journal, PathGuard guard, StateStore state, TimeProvider clock)
{
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

            // Only the manage planner exists so far; rotate lands in milestones 8 and 9.
            if (job.Kind != JobKind.Manage)
            {
                continue;
            }

            var plan = ManageJobPlanner.Plan(job, matched, now);
            plans.Add(plan);
            jobsRun++;

            var result = executor.Execute(plan, job, options.DryRun);
            completed += result.Completed;
            failed += result.Failed;
            freed += result.BytesFreed;
            errors.AddRange(result.Errors);
            diagnostics.AddRange(result.Diagnostics);
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
}
