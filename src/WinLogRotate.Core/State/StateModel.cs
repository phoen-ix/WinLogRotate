using System.Text.Json.Serialization;

namespace WinLogRotate.Core.State;

/// <summary>What a lock probe concluded about a path.</summary>
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
public enum ProbeIdentity
{
    Unknown,

    /// <summary>LocalSystem, or an administrator running elevated.</summary>
    Elevated,

    /// <summary>An ordinary user token.</summary>
    Standard,
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

    public ProbeVerdict Probe { get; init; } = ProbeVerdict.Unknown;
    public ProbeIdentity ProbedAs { get; init; } = ProbeIdentity.Unknown;
    public DateTimeOffset? ProbedAt { get; init; }

    public NulFillVerdict NulFill { get; init; } = NulFillVerdict.Unknown;

    /// <summary>Size before the last truncation, kept so the detector has something to
    /// compare against on the following run.</summary>
    public long? LastTruncatedFrom { get; init; }
}

/// <summary>The on-disk state document.</summary>
public sealed record StateDocument
{
    /// <summary>Bumped only on a breaking change. A file from the future is refused rather
    /// than misread, because misreading it would mean rotating on the wrong schedule.</summary>
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
