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
        var completed = 0;
        var failed = 0;
        long freed = 0;
        var jobsRun = 0;

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
            foreach (var pattern in job.Paths)
            {
                var decision = guard.CheckPattern(pattern);
                if (!decision.IsAllowed)
                {
                    failed++;
                    errors.Add(decision.Message ?? $"{pattern} was refused.");
                    continue;
                }

                matched.AddRange(FileEnumerator.Resolve(pattern));
            }

            var count = guard.CheckMatchCount(job.Name, matched.Count);
            if (!count.IsAllowed)
            {
                failed++;
                errors.Add(count.Message ?? "too many matches");
                continue;
            }

            if (matched.Count == 0 && !job.MissingOk)
            {
                failed++;
                errors.Add($"[{job.Name}] matched no files and missingok is not set.");
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
            Plans = plans,
        };
    }
}
