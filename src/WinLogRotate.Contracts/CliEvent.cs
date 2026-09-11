namespace WinLogRotate.Contracts;

/// <summary>
/// One line of the NDJSON stream a long-running verb writes to stdout under
/// <c>--json-stream</c>. The GUI renders these live; the journal persists the same shape.
/// <para>
/// Every destructive operation appears twice - once with <see cref="Phase"/> "plan" and once
/// with "apply" - so a crash between them leaves a readable "planned, never applied" record.
/// A dry run emits only the plan half, which is what makes <c>--dry-run</c> trustworthy:
/// it is the same code path, stopped one step earlier.
/// </para>
/// </summary>
public sealed record CliEvent
{
    /// <summary>ISO-8601 with offset.</summary>
    public required string Ts { get; init; }

    /// <summary>Groups every event of one invocation. Minted per run so the GUI can group
    /// without timestamp heuristics.</summary>
    public required string Run { get; init; }

    /// <summary>See <see cref="Op"/> for the vocabulary.</summary>
    public required string Operation { get; init; }

    /// <summary>"plan" or "apply".</summary>
    public required string Phase { get; init; }

    /// <summary>"ok", "skipped", "failed", or absent for progress events.</summary>
    public string? Result { get; init; }

    public string? Job { get; init; }
    public string? Src { get; init; }
    public string? Dst { get; init; }
    public long? BytesBefore { get; init; }
    public long? BytesAfter { get; init; }

    /// <summary>Which locked-file strategy was used, when one was.</summary>
    public string? Strategy { get; init; }

    /// <summary>Why this happened - the retention rule that condemned a file, the reason a
    /// job was skipped. Populated for every delete.</summary>
    public string? Reason { get; init; }

    public string? Error { get; init; }
    public long? Ms { get; init; }
}

/// <summary>
/// The <see cref="CliEvent.Operation"/> vocabulary. A closed set, queried by
/// <c>winlogrotate journal</c>.
/// </summary>
/// <remarks>
/// <para>
/// It used to say the GUI switches on these as well. It does not, and never has: the GUI runs
/// the CLI with <c>--json-stream</c> and tails the file as raw lines without deserializing a
/// single event.
/// </para>
/// <para>
/// Every member here must be written by something. Eight of twenty were not - a journal that
/// answers a query for <c>guard.override</c> with nothing, not because no override happened but
/// because no line was ever written, is worse than one that does not offer the query at all.
/// <c>EveryJournalOpIsEmittedBySomethingUnderSrc</c> keeps that from recurring.
/// </para>
/// </remarks>
public static class Op
{
    public const string RunStart = "run.start";
    public const string RunEnd = "run.end";
    public const string JobStart = "job.start";
    public const string JobEnd = "job.end";

    /// <summary>What the engine intends to do to one file.</summary>
    public const string Plan = "plan";

    // Destructive operations
    public const string Rename = "rename";
    public const string CopyTruncate = "copytruncate";
    public const string Copy = "copy";
    public const string Compress = "compress";
    public const string Delete = "delete";
    public const string Create = "create";
    /// <summary>A job's olddir being made, where createolddir asked for one.</summary>
    /// <remarks>
    /// Replaces "moveolddir", which no journal on disk can contain because no planner ever emitted
    /// it - so the wire contract is not being broken here, it was never made.
    /// </remarks>
    public const string CreateDir = "createdir";

    // Non-destructive
    public const string Hook = "hook";

    /// <summary>A stored credential was added, replaced or removed. The NAME only - never a
    /// value, and never anything derived from one. An operator asking "who changed that
    /// password?" has nowhere else to look.</summary>
    public const string Secret = "secret";

    /// <summary>A copytruncate NUL-fill verdict was reached for a path.</summary>
    public const string NulFill = "nulfill";

    /// <summary>A guardrail refused something.</summary>
    public const string GuardRefuse = "guard.refuse";

    /// <summary>An operator override of a guardrail was honoured. Always recorded with the
    /// reason, so an override can never be quietly forgotten.</summary>
    public const string GuardOverride = "guard.override";
}

/// <summary>Values for <see cref="CliEvent.Phase"/>.</summary>
public static class Phase
{
    public const string Plan = "plan";
    public const string Apply = "apply";
}

/// <summary>Values for <see cref="CliEvent.Result"/>.</summary>
public static class OpResult
{
    public const string Ok = "ok";
    public const string Skipped = "skipped";
    public const string Failed = "failed";
}
