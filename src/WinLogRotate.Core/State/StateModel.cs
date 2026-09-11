using System.Text.Json.Serialization;

namespace WinLogRotate.Core.State;

/// <summary>What a lock probe concluded about a path.</summary>
/// <remarks>
/// Written as a name rather than a number, for the reason <c>NotifyOutcome</c> gives: this file
/// lands in support bundles, and a bare 2 tells a reader nothing. It also removes a live hazard -
/// stored as ordinals, appending a member anywhere but the end silently reinterprets every state
/// file already on disk, and this particular enum decides whether a path is quarantined.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<ProbeVerdict>))]
public enum ProbeVerdict
{
    Unknown,

    /// <summary>The file can be renamed: the writer permitted FILE_SHARE_DELETE.</summary>
    Rename,

    /// <summary>Rename is denied but the contents can be copied and the file truncated.</summary>
    CopyTruncate,

    /// <summary>Only readable. The original cannot be emptied, so it keeps growing.</summary>
    Copy,

    /// <summary>Not even readable while the writer holds it.</summary>
    None,
}

/// <summary>
/// Who the probe ran as. A verdict reached by SYSTEM says nothing about what a desktop user
/// can do to the same file, and vice versa - so a cached verdict is only reused for the same
/// class of identity. Without this the bug is silent and intermittent, which is the worst kind.
/// </summary>
/// <remarks>
/// Written as a name rather than a number, for the reason <c>NotifyOutcome</c> gives: this file
/// lands in support bundles, and a bare 2 tells a reader nothing. It also removes a live hazard -
/// stored as ordinals, appending a member anywhere but the end silently reinterprets every state
/// file already on disk, and this particular enum decides whether a path is quarantined.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<ProbeIdentity>))]
public enum ProbeIdentity
{
    Unknown,

    /// <summary>LocalSystem, or an administrator running elevated.</summary>
    Elevated,

    /// <summary>An ordinary user token.</summary>
    Standard,
}

/// <summary>Which rule produced a verdict.</summary>
/// <remarks>
/// The caller needs this, not just the verdict. A NUL run at the resume offset is the signature
/// itself and means the same thing whenever it is observed; the size ratio is an inference whose
/// force depends entirely on how long ago the truncation was. Without knowing which fired, a
/// caller cannot tell one from the other - and quarantining on the ratio one full interval later
/// would permanently refuse copytruncate to every steadily-growing log on the machine.
/// </remarks>
public enum NulFillEvidence
{
    None,

    /// <summary>The file returned to roughly its pre-truncation size. An inference.</summary>
    ReGrowth,

    /// <summary>A NUL run at the offset the truncation left. The signature itself.</summary>
    NulSignature,
}

/// <summary>
/// What the copytruncate NUL-fill detector concluded.
/// </summary>
/// <remarks>
/// A writer that caches its own file offset - log4net, many C++ std::ofstream implementations -
/// does not seek back to zero after we truncate. Its next write lands at the old offset and
/// NTFS zero-fills the gap, producing a multi-gigabyte file of NUL bytes plus one line of log,
/// and repeating that every rotation forever. Once observed, the verdict is permanent for that
/// path: we must never try copytruncate on it again.
/// </remarks>
/// <remarks>
/// Written as a name rather than a number, for the reason <c>NotifyOutcome</c> gives: this file
/// lands in support bundles, and a bare 2 tells a reader nothing. It also removes a live hazard -
/// stored as ordinals, appending a member anywhere but the end silently reinterprets every state
/// file already on disk, and this particular enum decides whether a path is quarantined.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<NulFillVerdict>))]
public enum NulFillVerdict
{
    Unknown,

    /// <summary>Truncation behaved: the writer resumed at offset zero.</summary>
    Clean,

    /// <summary>The file re-grew to roughly its previous size. copytruncate is quarantined
    /// for this path.</summary>
    Confirmed,
}

/// <summary>Everything remembered about one log file between runs.</summary>
public sealed record PathState
{
    /// <summary>The path as last seen, in its original casing - for display only.</summary>
    public required string Path { get; init; }

    /// <summary>When this log was last rotated. Absent means it has never been rotated, which
    /// is how first-run baselining is recognised.</summary>
    public DateTimeOffset? LastRotated { get; init; }

    /// <summary>When this path was first seen. The first run records this and rotates nothing,
    /// matching logrotate.</summary>
    public DateTimeOffset? FirstSeen { get; init; }

    /// <summary>
    /// When a run last matched this path, whether or not it rotated it.
    /// </summary>
    /// <remarks>
    /// What <c>Prune</c> ages off, and it has to be this rather than <see cref="LastRotated"/>.
    /// <c>RecordFirstSighting</c> sets LastRotated on the first sighting but it only advances on
    /// an actual rotation, so a log held back every night by notifempty or minsize would look
    /// untouched for ever - and that is precisely a path whose NUL-fill quarantine is worth
    /// keeping. Nullable and additive, so a state file written before this existed simply lacks
    /// it; see <see cref="StateDocument.Version"/>.
    /// </remarks>
    public DateTimeOffset? LastSeen { get; init; }

    public ProbeVerdict Probe { get; init; } = ProbeVerdict.Unknown;
    public ProbeIdentity ProbedAs { get; init; } = ProbeIdentity.Unknown;
    public DateTimeOffset? ProbedAt { get; init; }

    public NulFillVerdict NulFill { get; init; } = NulFillVerdict.Unknown;

    /// <summary>Size before the last truncation, kept so the detector has something to
    /// compare against on the following run.</summary>
    public long? LastTruncatedFrom { get; init; }

    /// <summary>
    /// Where the last truncation left the file, and therefore where a NUL gap would begin.
    /// </summary>
    /// <remarks>
    /// Not zero. Bytes the writer appends while the copy is running are preserved by writing them
    /// back at the head, so a truncated file usually <i>starts</i> with log text - and a detector
    /// sampling from byte zero finds that tail instead of the NUL run it is looking for.
    /// </remarks>
    public long? LastTruncatedTo { get; init; }

    public DateTimeOffset? LastTruncatedAt { get; init; }

    /// <summary>
    /// Volume serial and file index of the handle that was truncated.
    /// </summary>
    /// <remarks>
    /// Deliberately not the creation time. NTFS file tunneling reuses the creation timestamp of a
    /// file recreated under the same name within fifteen seconds - which is exactly the
    /// rotate-then-recreate window, so creation time would report "same file" precisely when it is
    /// most certainly a different one.
    /// </remarks>
    public string? FileIdentity { get; init; }

    /// <summary>
    /// How many runs have failed to reach a verdict on the pending truncation.
    /// </summary>
    /// <remarks>
    /// Capped, so a file that can never be read does not leave a baseline sitting for ever. An
    /// immortal baseline is not harmless: it is evidence that gets staler every night while still
    /// being compared against.
    /// </remarks>
    public int TruncationChecks { get; init; }

    public DateTimeOffset? NulFillAt { get; init; }

    /// <summary>Which rule reached the verdict. See <c>NulFillEvidence</c>.</summary>
    public NulFillEvidence NulFillEvidence { get; init; }

    public long? NulFillSizeBefore { get; init; }
    public long? NulFillSizeAfter { get; init; }

    /// <summary>The Win32 error that blocked the better strategy at the last probe.</summary>
    public int ProbeError { get; init; }
}

/// <summary>The on-disk state document.</summary>
public sealed record StateDocument
{
    /// <summary>
    /// Bumped only on a breaking change. A file from the future is refused rather than misread,
    /// because misreading it would mean rotating on the wrong schedule.
    /// </summary>
    /// <remarks>
    /// Adding an optional property is not a breaking change and must not bump this. The document
    /// is written with <c>WhenWritingNull</c>, so an older file simply lacks the new field and an
    /// older build ignores it - whereas a bump would make that older build refuse the file
    /// outright and re-baseline every log on the machine, which is a real cost for an additive
    /// field. <c>PathState.LastSeen</c> was added exactly this way.
    /// </remarks>
    public int Version { get; init; } = 1;

    public DateTimeOffset? Written { get; init; }

    /// <summary>Keyed by <see cref="Safety.WinPath.CanonicalKey"/>.</summary>
    public Dictionary<string, PathState> Paths { get; init; } = [];
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(StateDocument))]
internal sealed partial class StateJsonContext : JsonSerializerContext;
