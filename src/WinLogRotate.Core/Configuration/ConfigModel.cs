namespace WinLogRotate.Core.Configuration;

/// <summary>What a job does.</summary>
public enum JobKind
{
    /// <summary>WinLogRotate performs the rotation: rename or copytruncate, then compress and
    /// retain. The classic logrotate behaviour.</summary>
    Rotate,

    /// <summary>
    /// The producing application already rotates its own logs; we only compress and retain
    /// what it has finished with, and never touch the file it is writing.
    /// <para>
    /// This is the mode most Windows installations actually need. IIS, http.sys, NSSM, WinSW,
    /// Apache rotatelogs, Serilog, NLog, log4net and Tomcat all roll their own logs and then
    /// never delete or compress them. It also needs none of the locked-file machinery, which
    /// makes it both the most useful mode and the safest.
    /// </para>
    /// </summary>
    Manage,
}

/// <summary>When a rotate job is due. Mutually exclusive - last one wins, as in logrotate.</summary>
public enum Schedule
{
    Hourly,
    Daily,
    Weekly,
    Monthly,
    Yearly,

    /// <summary>Rotate on size alone, ignoring the calendar entirely.</summary>
    Size,
}

/// <summary>How rotated files are compressed.</summary>
public enum CompressType
{
    None,

    /// <summary>Default. Double-clickable in Explorer on every Windows version, which matters
    /// for the audience actually running this. Costs zcat/zgrep compatibility, and Splunk and
    /// Elastic file inputs auto-decompress .gz but generally not .zip - so the producer presets
    /// choose gzip where the consuming ecosystem expects it.</summary>
    Zip,

    /// <summary>What logrotate produces. Streamable, and what log shippers expect.</summary>
    Gzip,
}

/// <summary>How a rotate job deals with a file another process holds open.</summary>
public enum LockStrategy
{
    /// <summary>
    /// Rename the live file. The clean, atomic option - and the one that fails outright when
    /// the writer did not permit FILE_SHARE_DELETE, which most Windows loggers do not. The
    /// default, because failing loudly is better than silently doing something else.
    /// </summary>
    Rename,

    /// <summary>Copy the contents aside, then truncate the original in place. Works when the
    /// writer permits write sharing. Carries a small data-loss window, and a real hazard with
    /// writers that cache their own file offset - see the NUL-fill detector.</summary>
    CopyTruncate,

    /// <summary>Copy and leave the original completely alone. Snapshot semantics; the live
    /// file keeps growing.</summary>
    Copy,

    /// <summary>Probe the file and pick the best strategy it actually supports, recording the
    /// choice on every run. Opt-in: the default never changes strategy behind your back.</summary>
    Auto,
}

/// <summary>
/// Settings shared by both job kinds. Every property is nullable so "not set here" can be
/// distinguished from "set to the same value as the default" - which is what makes a
/// three-level merge (built-in defaults, then config.toml, then the job) behave predictably.
/// </summary>
public record JobSettings
{
    public Schedule? Schedule { get; init; }

    /// <summary>0 = Sunday .. 6 = Saturday; 7 means "every 7 days regardless of weekday".</summary>
    public int? Weekday { get; init; }

    /// <summary>Day of month for a monthly schedule; 0 means "the first run of a new month".</summary>
    public int? MonthDay { get; init; }

    /// <summary>Number of past generations to keep. 0 discards; -1 keeps them all and leaves
    /// pruning entirely to <see cref="MaxAge"/>.</summary>
    public int? Rotate { get; init; }

    /// <summary>Base index for numbered rotation: 1 gives app.log.1, 0 gives app.log.0.</summary>
    public int? Start { get; init; }

    /// <summary>Delete rotated files older than this many days.</summary>
    public int? MaxAge { get; init; }

    /// <summary>Refuse to rotate a live log younger than this many days.</summary>
    public int? MinAge { get; init; }

    /// <summary>Rotate on schedule only once the log reaches this size.</summary>
    public long? MinSize { get; init; }

    /// <summary>Rotate ahead of schedule once the log exceeds this size.</summary>
    public long? MaxSize { get; init; }

    /// <summary>Size threshold for <see cref="Schedule.Size"/>.</summary>
    public long? SizeThreshold { get; init; }

    public bool? Compress { get; init; }
    public CompressType? CompressType { get; init; }

    /// <summary>Postpone compressing the newest generation by one cycle, so a writer that is
    /// still holding the renamed file is not compressed out from under it.</summary>
    public bool? DelayCompress { get; init; }

    /// <summary>Name archives by date rather than by an incrementing number.</summary>
    public bool? DateExt { get; init; }

    /// <summary>strftime-style, and it must sort lexicographically - year, then month, then
    /// day - or retention cannot order the archives.</summary>
    public string? DateFormat { get; init; }

    /// <summary>Skip a missing log silently instead of reporting it.</summary>
    public bool? MissingOk { get; init; }

    /// <summary>Do not rotate a zero-length log.</summary>
    public bool? NotIfEmpty { get; init; }

    /// <summary>Directory to move archives into. Relative paths resolve against the log's own
    /// directory.</summary>
    public string? OldDir { get; init; }

    public bool? CreateOldDir { get; init; }

    public LockStrategy? LockStrategy { get; init; }

    /// <summary>Manage jobs only: how many of the newest files to leave alone, because the
    /// producer is still writing them.</summary>
    public int? LiveFiles { get; init; }

    /// <summary>Refuse a pattern resolving to more files than this.</summary>
    public int? MaxFiles { get; init; }

    public int? RetryCount { get; init; }
    public int? RetryIntervalMs { get; init; }

    public IReadOnlyList<string>? PreRotate { get; init; }
    public IReadOnlyList<string>? PostRotate { get; init; }
}

/// <summary>One configured job, as written in a conf.d file.</summary>
public sealed record JobConfig : JobSettings
{
    public required string Name { get; init; }
    public required IReadOnlyList<string> Paths { get; init; }
    public JobKind Kind { get; init; } = JobKind.Rotate;
    public bool Enabled { get; init; } = true;

    /// <summary>The file this job was read from, for diagnostics and for writing back.</summary>
    public string? SourceFile { get; init; }

    /// <summary>Patterns the operator explicitly unlocked for this job.</summary>
    public IReadOnlyList<string> AllowDangerous { get; init; } = [];
}

/// <summary>
/// The compiled-in defaults, applied beneath config.toml and the job itself.
/// </summary>
/// <remarks>
/// These deliberately differ from logrotate's. Upstream's built-in defaults are
/// <c>size 1M</c> + <c>rotate 0</c> + <c>ifempty</c>, which means a job with no directives
/// deletes your logs at one megabyte and keeps nothing. That is defensible for a config
/// language nobody writes bare, and indefensible behind a GUI where a half-filled form is a
/// normal intermediate state. The divergence is documented in
/// <c>docs/logrotate-compatibility.md</c> and restated as a comment in every generated config.
/// </remarks>
public static class BuiltInDefaults
{
    public static JobSettings Values { get; } = new()
    {
        Schedule = Configuration.Schedule.Daily,
        Rotate = 7,
        Start = 1,
        Compress = true,
        CompressType = Configuration.CompressType.Zip,
        DelayCompress = false,
        DateExt = false,
        DateFormat = "-yyyyMMdd",
        MissingOk = false,
        NotIfEmpty = true,
        CreateOldDir = false,
        LockStrategy = Configuration.LockStrategy.Rename,
        LiveFiles = 1,
        MaxFiles = 1000,
        RetryCount = 5,
        RetryIntervalMs = 100,
        Weekday = 0,
        MonthDay = 0,
        SizeThreshold = 1L << 20,
    };
}

/// <summary>A job with every setting resolved - no nulls, nothing left to look up.</summary>
public sealed record EffectiveJob
{
    public required string Name { get; init; }
    public required JobKind Kind { get; init; }
    public required IReadOnlyList<string> Paths { get; init; }
    public required bool Enabled { get; init; }
    public required Schedule Schedule { get; init; }
    public required int Weekday { get; init; }
    public required int MonthDay { get; init; }
    public required int Rotate { get; init; }
    public required int Start { get; init; }
    public required int? MaxAge { get; init; }
    public required int? MinAge { get; init; }
    public required long? MinSize { get; init; }
    public required long? MaxSize { get; init; }
    public required long SizeThreshold { get; init; }
    public required bool Compress { get; init; }
    public required CompressType CompressType { get; init; }
    public required bool DelayCompress { get; init; }
    public required bool DateExt { get; init; }
    public required string DateFormat { get; init; }
    public required bool MissingOk { get; init; }
    public required bool NotIfEmpty { get; init; }
    public required string? OldDir { get; init; }
    public required bool CreateOldDir { get; init; }
    public required LockStrategy LockStrategy { get; init; }
    public required int LiveFiles { get; init; }
    public required int MaxFiles { get; init; }
    public required int RetryCount { get; init; }
    public required int RetryIntervalMs { get; init; }
    public required IReadOnlyList<string> PreRotate { get; init; }
    public required IReadOnlyList<string> PostRotate { get; init; }
    public required IReadOnlyList<string> AllowDangerous { get; init; }
    public string? SourceFile { get; init; }
}
