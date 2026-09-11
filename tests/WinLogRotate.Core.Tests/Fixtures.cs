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
        MaxAge = null,
        MinAge = null,
        MinSize = null,
        MaxSize = null,
        SizeThreshold = 1 << 20,
        Compress = true,
        CompressType = CompressType.Zip,
        DelayCompress = false,
        DateExt = false,
        DateFormat = "-yyyyMMdd",
        MissingOk = true,
        NotIfEmpty = false,
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
}
