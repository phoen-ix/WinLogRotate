using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Engine;
using WinLogRotate.Core.Globbing;
using WinLogRotate.Core.Safety;

namespace WinLogRotate.Core.Journaling;

/// <summary>What a maintenance pass did to the journal's own directory.</summary>
public sealed record JournalMaintenanceResult
{
    public required int Compressed { get; init; }
    public required int Deleted { get; init; }
    public required long BytesFreed { get; init; }
    public required IReadOnlyList<string> Errors { get; init; }

    public bool DidAnything => Compressed > 0 || Deleted > 0;
}

/// <summary>
/// Keeps the journal from growing without limit.
/// </summary>
/// <remarks>
/// <para>
/// This runs through <see cref="ManageJobPlanner"/> rather than reimplementing retention,
/// which is the whole point: the journal is a directory of dated files written by a producer
/// that rolls them itself and never cleans up — exactly the situation manage mode exists for.
/// So the tool's own output is its first customer, and if manage mode ever regresses, it shows
/// up here before it shows up in anyone's IIS logs.
/// </para>
/// <para>
/// It also inherits <c>livefiles = 1</c> for free, which is precisely the guarantee needed:
/// today's journal is being appended to right now and must not be compressed out from under
/// the writer.
/// </para>
/// </remarks>
public static class JournalMaintenance
{
    /// <summary>
    /// The name the journal's own upkeep reports under.
    /// </summary>
    /// <remarks>
    /// Attributed rather than left run-scoped, for two reasons. A failure to tidy the journal is
    /// the product's own housekeeping and should not page anybody unless asked for - and while it
    /// carried no job it merged into the run scope's aggregation, so a full journal directory
    /// made "the configuration is broken" look like a different problem every time it changed.
    /// </remarks>
    public const string JobName = "journal";

    /// <summary>
    /// Works out what the journal directory needs, without touching anything.
    /// </summary>
    /// <remarks>
    /// Split from <see cref="Run"/> so the decision - which files to compress, which to delete,
    /// and above all which to leave alone - is a pure function that can be tested on any
    /// platform. Only the execution below needs Windows.
    /// <para>
    /// The directory is enumerated directly rather than through the glob machinery: it is ours,
    /// created by the installer with a known shape, so the pattern-refusal and match-ceiling
    /// logic that protects an operator from their own typo has nothing to protect here.
    /// </para>
    /// </remarks>
    public static JobPlan PlanFor(string journalDirectory, JournalSettings settings, DateTimeOffset now)
    {
        var job = JobFor(settings);

        if (!settings.Enabled || !Directory.Exists(journalDirectory))
        {
            return new JobPlan { JobName = job.Name, Operations = [], MatchedFiles = 0 };
        }

        var matched = new DirectoryInfo(journalDirectory)
            .GetFiles("journal-*")
            .Select(f => new MatchedFile
            {
                Path = f.FullName,
                Length = f.Length,
                LastWriteUtc = new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero),
            })
            .ToArray();

        return ManageJobPlanner.Plan(job, matched, now);
    }

    /// <summary>
    /// Tidies the journal directory. Call this <b>before</b> opening today's writer, so the
    /// pass never touches a file it is itself holding open.
    /// </summary>
    /// <param name="links">
    /// The link resolver the executor guards against, for the reason
    /// <see cref="PlanExecutor"/> takes one. Tests need a pass in which every operation is
    /// refused - the only state in which the old arithmetic could go negative - and the refusal
    /// has to happen on both CI legs, so it cannot be borrowed from the Linux leg's habit of
    /// turning down a Unix path as not absolute.
    /// </param>
    public static JournalMaintenanceResult Run(
        string journalDirectory, JournalSettings settings, TimeProvider clock,
        Io.ILinkResolver? links = null)
    {
        if (!settings.Enabled)
        {
            return Nothing;
        }

        var job = JobFor(settings);
        var plan = PlanFor(journalDirectory, settings, clock.GetUtcNow());

        if (plan.Operations.Count == 0)
        {
            return Nothing;
        }

        // A NullJournal on purpose: journaling the journal's own tidy-up into the file being
        // tidied is a small infinite regress, and the summary is reported to the caller
        // instead, which records one line once the real writer is open.
        var executor = new PlanExecutor(new NullJournal(), Guard, clock, links);
        var result = executor.Execute(plan, job, dryRun: false);

        return new JournalMaintenanceResult
        {
            // What the executor did, not what the plan intended. Counting the plan reported a
            // delete the guard refused as one that happened, and subtracting the run's whole
            // failure count from the compressed tally let one failed delete make it negative.
            Compressed = result.CompletedBy.GetValueOrDefault(PlannedAction.Compress),
            Deleted = result.CompletedBy.GetValueOrDefault(PlannedAction.Delete),
            BytesFreed = result.BytesFreed,
            Errors = result.Errors,
        };
    }

    private static JournalMaintenanceResult Nothing { get; } = new()
    {
        Compressed = 0,
        Deleted = 0,
        BytesFreed = 0,
        Errors = [],
    };

    /// <summary>
    /// The journal directory is ours, created by the installer with a locked-down ACL, so the
    /// usual protected-location and match-count rules cannot meaningfully fire here. Reparse
    /// points are still refused - that check has no override anywhere in the product.
    /// </summary>
    /// <remarks>
    /// The match-count exemption is not stated here. It rides on the synthetic job's
    /// <c>MaxFiles = int.MaxValue</c>, which reaches the guard as a <see cref="GuardScope"/> like
    /// any other job's - so there is one mechanism for "this job may match more than the default"
    /// rather than a second one wired only to this directory.
    /// </remarks>
    private static PathGuard Guard { get; } = new(new GuardOptions());

    private static EffectiveJob JobFor(JournalSettings settings) => new()
    {
        Name = "journal",
        Kind = JobKind.Manage,
        Paths = [],
        Enabled = true,
        Schedule = Schedule.Daily,
        Weekday = 0,
        MonthDay = 0,
        Rotate = settings.Retain,
        Start = 1,

        // Retention is expressed in days, which for one-file-per-day is also the file count -
        // but maxage is what an operator means by "keep 30 days", and it stays correct when a
        // busy day rolls mid-file and produces more than one.
        MaxAge = settings.Retain < 0 ? null : settings.Retain,

        MinAge = null,
        MinSize = null,
        MaxSize = null,
        SizeThreshold = 1 << 20,
        Compress = settings.Compress != CompressType.None,
        CompressType = settings.Compress,
        DelayCompress = false,
        DateExt = true,
        DateFormat = "-yyyy-MM-dd",
        MissingOk = true,
        NotIfEmpty = true,
        OldDir = null,
        CreateOldDir = false,
        LockStrategy = LockStrategy.Rename,

        // Today's file is being written right now. This is the same guarantee that keeps us
        // off the log IIS is currently appending to.
        LiveFiles = 1,

        MaxFiles = int.MaxValue,
        RetryCount = 3,
        RetryIntervalMs = 100,
        PreRotate = [],
        PostRotate = [],

        // The journal's own upkeep runs no hooks and never will: it is the one job whose
        // definition is code rather than configuration, so there is nothing for an operator to
        // hang a script on.
        HookTimeout = TimeSpan.Zero,
        AllowDangerous = [],
    };
}
