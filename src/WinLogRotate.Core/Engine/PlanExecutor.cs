using WinLogRotate.Contracts;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Io;
using WinLogRotate.Core.Journaling;
using WinLogRotate.Core.Safety;

namespace WinLogRotate.Core.Engine;

/// <summary>What actually happened when a plan was carried out.</summary>
public sealed record ExecutionResult
{
    public required int Completed { get; init; }
    public required int Failed { get; init; }
    public required int Skipped { get; init; }
    public required long BytesFreed { get; init; }
    public required IReadOnlyList<string> Errors { get; init; }

    /// <summary>The same failures as <see cref="Errors"/>, classified and attributed.
    /// Errors keeps the flat strings the envelope has always carried.</summary>
    public IReadOnlyList<CliDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>
    /// How many of each action actually completed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Completed"/> is one number for a plan that compresses, deletes and renames, so
    /// a caller wanting to say "n compressed, m removed" could not ask and counted the
    /// <i>plan</i> instead - which reports an operation the guard refused as one that happened.
    /// <see cref="JournalMaintenance"/> did exactly that, and additionally subtracted the run's
    /// whole failure count from its compressed tally, so one failed delete could make the number
    /// of compressed files negative.
    /// </para>
    /// <para>
    /// Completions only. A failure is in <see cref="Failed"/> and <see cref="Errors"/>, a skip is
    /// in <see cref="Skipped"/>, and what was merely intended is in the plan.
    /// </para>
    /// </remarks>
    public IReadOnlyDictionary<PlannedAction, int> CompletedBy { get; init; } =
        new Dictionary<PlannedAction, int>();

    /// <summary>
    /// How many attempts were made beyond the first, across every operation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="RetryPolicy"/>'s <c>onRetry</c> is documented as existing so that a contended
    /// file leaves evidence - "useful evidence when someone asks why a rotation was slow" - and no
    /// call site had ever passed one, so the evidence did not exist. A retry sleeps, doubling up
    /// to five seconds a time, and <see cref="PlannedOp"/>'s elapsed milliseconds cannot tell one
    /// slow attempt from five.
    /// </para>
    /// <para>
    /// A count rather than a diagnostic, deliberately. A new diagnostic code would need a row in
    /// docs/diagnostics.md carrying an Event Log id, and at Info that id can never be written -
    /// the sink mirrors Warning and above - so it would publish a fourth unreachable id. At
    /// Warning it would put progress reporting into a system log, which is what that filter exists
    /// to keep out. The number belongs in the run's own summary, which is where somebody asking
    /// why last night was slow is already looking.
    /// </para>
    /// </remarks>
    public int Retries { get; init; }

    /// <summary>
    /// Live logs that were actually moved out of the way.
    /// </summary>
    /// <remarks>
    /// The rotation clock advances from this and from nothing else. Advancing it because a
    /// rotation was <i>planned</i> would record a log that failed on a share violation as
    /// rotated, and it would then never be due again - a locked file rotating exactly once and
    /// falling silent for ever. Only the executor knows which moves really happened, so the
    /// answer is produced here rather than inferred by the caller - and only from the operation
    /// the planner marked <see cref="PlannedOp.IsLiveRotation"/>, never from a shift.
    /// </remarks>
    public IReadOnlyList<string> Rotated { get; init; } = [];
}

/// <summary>
/// Carries out a plan, journaling both halves of every operation.
/// </summary>
/// <remarks>
/// Each destructive step emits a <c>plan</c> event and then an <c>apply</c> event, so a run
/// killed between them leaves a readable record of an intention that was never completed. A
/// dry run emits only the first half - it is the same code path, stopped one step earlier,
/// rather than a separate description that could drift from what the executor really does.
/// </remarks>
public sealed class PlanExecutor(
    IJournal journal, PathGuard guard, TimeProvider clock, ILinkResolver? links = null,
    IPlanApplier? applier = null)
{
    /// <summary>
    /// What touches the files. Defaults to the real thing, so every existing caller is unchanged.
    /// </summary>
    private readonly IPlanApplier _applier = applier ?? new WindowsPlanApplier();

    public ExecutionResult Execute(JobPlan plan, EffectiveJob job, bool dryRun)
    {
        var completed = 0;
        var completedBy = new Dictionary<PlannedAction, int>();
        var retries = 0;
        var failed = 0;
        var skipped = 0;
        long freed = 0;
        var errors = new List<string>();
        var diagnostics = new List<CliDiagnostic>();
        var rotated = new List<string>();

        // For the life of this call. A plan touches a handful of directories and dozens of files.
        var resolved = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        // Every source this plan expected to have vacated and did not: a delete, rename or
        // compress that failed or was refused. Nothing later may write over one of them.
        //
        // A plan is reasoned about against the directory as it is, and each operation assumes the
        // ones before it did what they said. FileOps.Rename replaces its destination silently -
        // that is how a numbered rotation overwrites, and upstream does the same - so with
        // rotate = 3 and app.log.3.zip held open, the delete failed, .2 -> .3 failed against the
        // same lock, and .1 -> .2 then replaced a .2 that had never moved. Under delaycompress
        // the same shape cost the newest generation: the compress of .1 failed on a full disk,
        // its shift failed on the name the compress never produced, and the live log's rename
        // landed on yesterday's .1. Both were reported as one failed operation and several
        // successful ones. logrotate stops a log's chain at the first error; this refuses only
        // the operations that would write over what the error left behind, so a live log still
        // rotates into a slot that really is free.
        var stillThere = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The other half: every name a failed operation was meant to produce and did not, with why.
        // An operation reading one of them is not attempted. With compression on, a live log whose
        // rename the writer's sharing refused left no app.log.1, and the compress of app.log.1 then
        // ran anyway and failed as a second error - "the file no longer exists", about a file that
        // never did - for one locked log. It is recorded as skipped, with the reason, instead; and
        // what it would have produced is never produced either, so the chain behind it stops too.
        var neverProduced = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void NotProduced(PlannedOp failed)
        {
            // Create is exempt, as it is below: it recreates the live log in place, and nothing
            // later in a plan reads that path.
            if (failed.Destination is { } target && failed.Action != PlannedAction.Create)
            {
                neverProduced.TryAdd(target,
                    $"not attempted: '{WinPath.FileName(target)}' was never produced, because the "
                    + $"{failed.Action.ToString().ToLowerInvariant()} of '{WinPath.FileName(failed.Source)}' failed");
            }
        }

        foreach (var op in plan.Operations)
        {
            if (op.Action == PlannedAction.Skip)
            {
                skipped++;
                Emit(plan, op, Phase.Plan, OpResult.Skipped, null, 0);
                continue;
            }

            Emit(plan, op, Phase.Plan, null, null, 0);

            if (dryRun)
            {
                continue;
            }

            if (neverProduced.TryGetValue(op.Source, out var because))
            {
                skipped++;

                if (op.Destination is { } onward && op.Action != PlannedAction.Create)
                {
                    neverProduced.TryAdd(onward, because);
                }

                Emit(plan, op, Phase.Apply, OpResult.Skipped, null, 0, reason: because);
                continue;
            }

            // Re-check immediately before acting, not only at plan time. The plan may be
            // seconds old, and this is the last moment before something is destroyed.
            //
            // Against where the file REALLY is, not where it is spelled. The planner's check was
            // textual, so a directory swapped for a junction between planning and acting - or one
            // reached by an 8.3 name - would pass it and be deleted from anyway.
            // Both ends. op.Destination was handed straight to FileOps and never guarded, so
            // olddir = "C:/Windows/System32" was written to with the guard holding that very path
            // in ProtectedRoots and never being asked about it. Resolved() caches per directory,
            // so forty archives into one olddir still cost one open.
            if (Refused(op.Source) || (op.Destination is { } to && Refused(to)))
            {
                stillThere.Add(op.Source);
                NotProduced(op);
                continue;
            }

            // Create is exempt: it opens with CreateNew and leaves an existing file alone, which
            // is the behaviour a writer that recreated its own log already relies on.
            if (op.Destination is { } target
                && op.Action != PlannedAction.Create
                && stillThere.Contains(target))
            {
                failed++;
                stillThere.Add(op.Source);
                NotProduced(op);
                var blocked = Diagnose.WouldOverwrite(op, plan.JobName);
                errors.Add(blocked.Message);
                diagnostics.Add(blocked);
                Emit(plan, op, Phase.Apply, OpResult.Failed, blocked.Message, 0);
                continue;
            }

            bool Refused(string path)
            {
                // Unverifiable, not allowed. Resolved returns null when it could not establish
                // where a directory really leads, and the guard's own vocabulary already has a
                // verdict for that - reached here by asking it, so the message and the remedy are
                // the ones the enumerator gives for the same condition.
                var decision = Resolved(path, resolved) is { } real
                    ? guard.CheckPath(real, job.GuardScope)
                    : guard.UnresolvableLink(path, "the directory could not be resolved", 0);

                if (decision.IsAllowed)
                {
                    return false;
                }

                failed++;
                var refusal = Diagnose.Refusal(decision, plan.JobName);
                errors.Add(refusal.Message);
                diagnostics.Add(refusal);
                Emit(plan, op, Phase.Apply, OpResult.Failed, refusal.Message, 0);
                return true;
            }

            var started = clock.GetTimestamp();
            try
            {
                var applied = _applier.Apply(op, job, (_, _) => retries++);
                completed++;

                if (applied.Truncation is { } truncation)
                {
                    RecordTruncation?.Invoke(op.Source, truncation);
                }

                completedBy[op.Action] = completedBy.GetValueOrDefault(op.Action) + 1;
                if (op.Action == PlannedAction.Delete)
                {
                    freed += op.Bytes;
                }

                // Only the operation that moves the live log out of the way. Compressing or
                // deleting an older generation is not what the rotation clock measures - and
                // neither is shifting one: the numbered chain moves by renames too, and counting
                // those marked a log as rotated, and ran its postrotate hook, on a night its own
                // rename had failed.
                if (op.IsLiveRotation)
                {
                    rotated.Add(op.Source);
                }

                Emit(plan, op, Phase.Apply, OpResult.Ok, null,
                    (long)clock.GetElapsedTime(started).TotalMilliseconds, applied.BytesAfter);
            }
            // ExternalException covers Win32Exception, which nothing here threw until hooks
            // existed and which this filter did not match. It escaped Execute, and then RunCommand
            // and Program - neither of which has a catch at all - so a missing executable ended the
            // process with the journal's run.end record never written. RetryPolicy.ErrorCode has
            // always understood the type; only the filter stood in the way.
            catch (Exception e) when (
                e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
            {
                failed++;
                stillThere.Add(op.Source);
                NotProduced(op);
                var failure = Diagnose.Failure(op.Action, op.Source, e, plan.JobName, op.Destination);
                errors.Add(failure.Message);
                diagnostics.Add(failure);
                Emit(plan, op, Phase.Apply, OpResult.Failed, failure.Message,
                    (long)clock.GetElapsedTime(started).TotalMilliseconds);
            }
        }

        return new ExecutionResult
        {
            Completed = completed,
            Failed = failed,
            Skipped = skipped,
            BytesFreed = freed,
            Errors = errors,
            Diagnostics = diagnostics,
            CompletedBy = completedBy,
            Retries = retries,
            Rotated = rotated,
        };
    }

    /// <summary>ERROR_FILE_NOT_FOUND and ERROR_PATH_NOT_FOUND.</summary>
    /// <remarks>
    /// Named rather than inlined because the distinction they draw is the whole of the rule
    /// above: absent is not the same as unverifiable, and conflating them refuses every plan
    /// whose destination does not exist yet - which is every numbered archive.
    /// </remarks>
    private const int NotFound = 2;

    /// <inheritdoc cref="NotFound"/>
    private const int PathNotFound = 3;

    /// <summary>
    /// The path as the file system sees it, with each directory resolved at most once - or null
    /// when where it really leads could not be established.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Per directory rather than per operation: a job of forty archives in one folder pays one
    /// open, not forty.
    /// </para>
    /// <para>
    /// Null rather than the spelled path, but only when the directory is really there and its
    /// location could not be established. This used to fall back to the spelling whenever the
    /// directory would not resolve, on the argument that an unresolvable path was then no worse
    /// guarded than before - but the spelling is exactly what the guard cannot trust here, and
    /// this is the last check before a file is deleted. <c>FileEnumerator.Vet</c> has answered
    /// the identical question by refusing since it was written, and one product cannot hold two
    /// opinions about whether an unverifiable link may be acted on. The bias is stated in
    /// <c>ConfDirGuard</c>'s own words: a false refusal costs an operation and prints why; a
    /// false acceptance deletes files behind a junction somebody planted.
    /// </para>
    /// </remarks>
    private string? Resolved(string path, Dictionary<string, string?> cache)
    {
        var directory = WinPath.DirectoryName(path);

        if (directory.Length == 0)
        {
            return path;
        }

        if (!cache.TryGetValue(directory, out var real))
        {
            var target = (links ?? new LinkResolver()).Resolve(directory);

            real = target switch
            {
                { Resolved: true, FinalPath: { } final } => final,

                // A directory that is not there is not a link whose target could not be
                // established - there is nothing behind it to have been swapped, so the spelled
                // path is the whole truth about it and the guard can judge that textually. The
                // plan names destinations that do not exist yet by design: createolddir, and
                // every numbered archive.
                { Error: NotFound or PathNotFound } => directory,

                // Anything else means the directory is there and where it leads could not be
                // established. That is the case this refuses.
                _ => null,
            };

            cache[directory] = real;
        }

        if (real is null)
        {
            return null;
        }

        // Rebuilt only when the directory really moved. Recombining an unchanged path would put
        // it through Combine and FileName for no reason, and those answer in Windows spelling -
        // which is a needless way to change a path that nothing asked to change.
        return WinPath.CanonicalKey(real) == WinPath.CanonicalKey(directory)
            ? path
            : WinPath.Combine(real, WinPath.FileName(path));
    }

    /// <summary>
    /// Called after a truncation with the size the file had beforehand, so the caller can store
    /// it in state. Without that number the NUL-fill detector has nothing to compare against on
    /// the following run, and the failure it exists to catch stays invisible. The numbers come
    /// back from <see cref="IPlanApplier.Apply"/> rather than being raised inside it, so a fake
    /// applier reports a truncation the same way the real one does.
    /// </summary>
    public Action<string, TruncationOutcome>? RecordTruncation { get; set; }

    private void Emit(
        JobPlan plan, PlannedOp op, string phase, string? result,
        string? error, long ms, long? bytesAfter = null, string? reason = null) =>
        journal.Write(new CliEvent
        {
            Ts = string.Empty,
            Run = string.Empty,
            Operation = op.Action switch
            {
                PlannedAction.Compress => Op.Compress,
                PlannedAction.Delete => Op.Delete,
                PlannedAction.Rename => Op.Rename,
                PlannedAction.CopyTruncate => Op.CopyTruncate,
                PlannedAction.Copy => Op.Copy,
                PlannedAction.Create => Op.Create,
                PlannedAction.CreateDirectory => Op.CreateDir,
                _ => Op.Plan,
            },
            Phase = phase,
            Result = result,
            Job = plan.JobName,
            Src = op.Source,
            Dst = op.Destination,
            BytesBefore = op.Bytes,
            BytesAfter = bytesAfter,

            // CliEvent.Strategy is documented as "which locked-file strategy was used, when one
            // was" and was set by nothing, so it was null on every journal line this product has
            // ever written. It is also the only way to tell afterwards what lockstrategy = "auto"
            // actually resolved to, which is now a question worth being able to answer.
            Strategy = op.Strategy?.ToString().ToLowerInvariant(),
            Reason = reason ?? op.Reason,
            Error = error,
            Ms = ms == 0 ? null : ms,
        });
}
