using WinLogRotate.Core.Configuration;

namespace WinLogRotate.Core.Tests;

/// <summary>Fully-populated domain objects, for tests that care about shape.</summary>
/// <remarks>
/// Every member set on purpose: a snapshot of the wire taken from a half-built object
/// records the fields that happened to be populated, and would go quietly green the day
/// somebody stopped emitting one of the others.
/// </remarks>
internal static class Fixtures
{
    public static EffectiveJob Job() => new()
    {
        Name = "app",
        Kind = JobKind.Manage,
        Paths = [@"C:\logs\app.log"],
        Enabled = true,
        Schedule = Schedule.Daily,
        Weekday = 0,
        MonthDay = 0,
        Rotate = 4,
        Start = 1,
        // Not null, unlike the five that follow, because these are what an editor and a
        // retention report actually read - and a fixture that leaves a field null leaves that
        // field out of the pinned envelope entirely, because the sink omits nulls. The shape
        // this product publishes then goes unpinned for exactly the fields most likely to be
        // added to or removed from.
        MaxAge = 90,
        MinAge = 2,
        MinSize = 4096,
        MaxSize = 104857600,
        SizeThreshold = 1 << 20,
        Compress = true,
        CompressType = CompressType.Zip,
        DelayCompress = false,
        DateExt = false,
        DateFormat = "-yyyyMMdd",
        MissingOk = true,
        NotIfEmpty = false,
        OldDir = @"C:\archive",
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

        // The editor's only route from a job back to the file it came from. Absent from the
        // pinned envelope for as long as this was null, so config show could have stopped
        // publishing it without anything noticing.
        SourceFile = @"C:\ProgramData\WinLogRotate\conf.d\app.toml",
    };
}
