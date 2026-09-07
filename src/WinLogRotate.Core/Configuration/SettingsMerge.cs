namespace WinLogRotate.Core.Configuration;

/// <summary>Resolves the three-level settings chain into a fully-populated job.</summary>
public static class SettingsMerge
{
    /// <summary>
    /// Built-in defaults, then <c>config.toml</c>'s <c>[defaults]</c>, then the job itself -
    /// each level overriding only what it actually sets.
    /// </summary>
    /// <remarks>
    /// Every setting is nullable precisely so this works. If <c>compress</c> were a plain
    /// <c>bool</c>, a job that never mentions it would be indistinguishable from one that sets
    /// it to <c>false</c>, and turning compression off globally would be impossible to
    /// override per job. The nullability is the feature, not ceremony.
    /// </remarks>
    public static EffectiveJob Resolve(JobConfig job, JobSettings? fileDefaults)
    {
        var b = BuiltInDefaults.Values;
        var d = fileDefaults;

        T Pick<T>(Func<JobSettings, T?> get, T fallback) where T : struct =>
            get(job) ?? (d is null ? null : get(d)) ?? get(b) ?? fallback;

        T? PickNullable<T>(Func<JobSettings, T?> get) where T : struct =>
            get(job) ?? (d is null ? null : get(d)) ?? get(b);

        string? PickString(Func<JobSettings, string?> get) =>
            get(job) ?? (d is null ? null : get(d)) ?? get(b);

        IReadOnlyList<string> PickList(Func<JobSettings, IReadOnlyList<string>?> get) =>
            get(job) ?? (d is null ? null : get(d)) ?? get(b) ?? [];

        var compress = Pick(s => s.Compress, true);
        var compressType = Pick(s => s.CompressType, CompressType.Zip);

        return new EffectiveJob
        {
            Name = job.Name,
            Kind = job.Kind,
            Paths = job.Paths,
            Enabled = job.Enabled,
            Schedule = Pick(s => s.Schedule, Schedule.Daily),
            Weekday = Pick(s => s.Weekday, 0),
            MonthDay = Pick(s => s.MonthDay, 0),
            Rotate = Pick(s => s.Rotate, 7),
            Start = Pick(s => s.Start, 1),
            MaxAge = PickNullable(s => s.MaxAge),
            MinAge = PickNullable(s => s.MinAge),
            MinSize = PickNullable(s => s.MinSize),
            MaxSize = PickNullable(s => s.MaxSize),
            SizeThreshold = Pick(s => s.SizeThreshold, 1L << 20),

            // "compress = false" and "compresstype = none" must mean the same thing, or a
            // config can say one and mean the other. Normalise to a single source of truth.
            Compress = compress && compressType != CompressType.None,
            CompressType = compress ? compressType : CompressType.None,

            DelayCompress = Pick(s => s.DelayCompress, false),
            DateExt = Pick(s => s.DateExt, false),
            DateFormat = PickString(s => s.DateFormat) ?? "-yyyyMMdd",
            MissingOk = Pick(s => s.MissingOk, false),
            NotIfEmpty = Pick(s => s.NotIfEmpty, true),
            OldDir = PickString(s => s.OldDir),
            CreateOldDir = Pick(s => s.CreateOldDir, false),
            LockStrategy = Pick(s => s.LockStrategy, LockStrategy.Rename),
            LiveFiles = Pick(s => s.LiveFiles, 1),
            MaxFiles = Pick(s => s.MaxFiles, 1000),
            RetryCount = Pick(s => s.RetryCount, 5),
            RetryIntervalMs = Pick(s => s.RetryIntervalMs, 100),
            PreRotate = PickList(s => s.PreRotate),
            PostRotate = PickList(s => s.PostRotate),
            AllowDangerous = job.AllowDangerous,
            SourceFile = job.SourceFile,
        };
    }
}
