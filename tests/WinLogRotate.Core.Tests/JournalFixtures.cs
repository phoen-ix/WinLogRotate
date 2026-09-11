using WinLogRotate.Core.Compression;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Engine;
using WinLogRotate.Core.Io;
using WinLogRotate.Core.Journaling;
using WinLogRotate.Core.Safety;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// A journal directory written by the product, for the tests that read one back.
/// </summary>
/// <remarks>
/// <para>
/// Shared because the verb and the GUI's history page must be asserted against the same records,
/// and because a hand-written journal would only ever contain the pairing its author believed in
/// - which is the thing under test in both.
/// </para>
/// <para>
/// A genuine apply-phase failure is reachable without Win32 by pointing the executor's
/// last-moment link check at a protected root: the guard refuses, and the refusal is emitted as
/// the apply half before any I/O is attempted. An abandoned plan half is a dry run, which stops
/// exactly where a killed process does.
/// </para>
/// </remarks>
internal static class JournalFixtures
{
    private sealed class Redirect : ILinkResolver
    {
        public LinkTarget Resolve(string path) => LinkTarget.At(@"C:\Windows\System32");
    }

    private static EffectiveJob Job() => new()
    {
        Name = "iis",
        Kind = JobKind.Manage,
        Paths = ["x"],
        Enabled = true,
        Schedule = Schedule.Daily,
        Weekday = 0,
        MonthDay = 0,
        Rotate = 3,
        Start = 1,
        MaxAge = null,
        MinAge = null,
        MinSize = null,
        MaxSize = null,
        SizeThreshold = 1 << 20,
        Compress = false,
        CompressType = CompressType.None,
        DelayCompress = false,
        DateExt = false,
        DateFormat = "-yyyyMMdd",
        MissingOk = true,
        NotIfEmpty = true,
        OldDir = null,
        CreateOldDir = false,
        LockStrategy = LockStrategy.Rename,
        LiveFiles = 1,
        MaxFiles = 1000,
        RetryCount = 5,
        RetryIntervalMs = 100,
        PreRotate = [],
        PostRotate = [],
        HookTimeout = TimeSpan.FromSeconds(60),
        AllowDangerous = [],
    };

    /// <summary>The path the <c>journal</c> verb reads for a given configuration root.</summary>
    public static string DirectoryUnder(string root) =>
        InstallPaths.Resolve(root).JournalDirectory;

    /// <summary>
    /// Writes one night's worth of record: a delete that happened, a file deliberately left
    /// alone, and a delete from an earlier run that was never applied.
    /// </summary>
    /// <returns>The journal directory.</returns>
    public static string Write(string root, TimeProvider clock)
    {
        var directory = DirectoryUnder(root);

        // The abandoned run first, so the collapse has to keep a record that appears before the
        // pair rather than after it.
        Session(directory, clock, "01ABANDONEDRUN000000000000", dryRun: true,
        [
            Op(PlannedAction.Delete, @"C:\logs\app.log.9", "rotate = 3 keeps 3 archive(s); this is number 9"),
        ]);

        Session(directory, clock, "01COMPLETEDRUN000000000000", dryRun: false,
        [
            Op(PlannedAction.Delete, @"C:\logs\app.log.4", "rotate = 3 keeps 3 archive(s); this is number 4"),
            Op(PlannedAction.Skip, @"C:\logs\app.log", "newest file - the application is still writing it"),
        ]);

        return directory;
    }

    private static PlannedOp Op(PlannedAction action, string source, string reason) => new()
    {
        Action = action,
        Source = source,
        Reason = reason,
        Bytes = 1024,
    };

    private static void Session(
        string directory, TimeProvider clock, string runId, bool dryRun, PlannedOp[] operations)
    {
        using var journal = JournalWriter.Open(directory, clock, runId);

        new PlanExecutor(
                journal,
                new PathGuard(new GuardOptions { ProtectedRoots = [@"C:\Windows"] }),
                clock,
                new Redirect())
            .Execute(
                new JobPlan { JobName = "iis", Operations = operations, MatchedFiles = operations.Length },
                Job(),
                dryRun);
    }
}
