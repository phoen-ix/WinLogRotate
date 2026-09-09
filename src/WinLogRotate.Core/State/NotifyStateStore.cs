using System.Text.Json;
using WinLogRotate.Core.Notify;

namespace WinLogRotate.Core.State;

/// <summary>
/// What was last reported, and which channels are currently suppressed.
/// </summary>
/// <remarks>
/// <para>
/// A sibling of <c>state.json</c> rather than a section inside it, and the reason is that the two
/// must fail in <em>opposite</em> directions. <see cref="StateStore.Load"/> throws on a version
/// it does not understand, deliberately: misreading a rotation clock means rotating on the wrong
/// schedule, and every log losing an interval is worse than starting from a known baseline. This
/// file must do the reverse and carry on, because the worst consequence of losing it is one
/// redundant message. Sharing a document would force one policy on both.
/// </para>
/// <para>
/// What is shared is the <em>mechanism</em>: <see cref="AtomicJson"/>, so a crash leaves either
/// the old contents or the new ones.
/// </para>
/// </remarks>
public sealed class NotifyStateStore
{
    private readonly string _path;
    private NotifyStateDocument _document;

    private NotifyStateStore(string path, NotifyStateDocument document, string? warning)
    {
        _path = path;
        _document = document;
        Warning = warning;
    }

    /// <summary>Why the previous contents were discarded, when they were.</summary>
    public string? Warning { get; }

    /// <summary>
    /// True when the stored fingerprints were produced by a different algorithm.
    /// </summary>
    /// <remarks>
    /// The planner adopts them silently instead of comparing, so that changing how a failure is
    /// identified is a release note rather than a night on which every installation reports
    /// every job as newly broken and then newly recovered.
    /// </remarks>
    public bool AlgorithmChanged { get; private init; }

    public IReadOnlyDictionary<string, JobNotifyState> Jobs => _document.Jobs;

    public IReadOnlyDictionary<string, ChannelNotifyState> Channels => _document.Channels;

    /// <summary>
    /// Reads the file, or starts fresh. Never throws, and never refuses.
    /// </summary>
    public static NotifyStateStore Load(string path)
    {
        if (!File.Exists(path))
        {
            return new NotifyStateStore(path, Fresh(), null);
        }

        try
        {
            var json = File.ReadAllText(path);
            var document = JsonSerializer.Deserialize(json, NotifyStateJsonContext.Default.NotifyStateDocument);

            if (document is null)
            {
                return new NotifyStateStore(path, Fresh(), "the notification state file was empty");
            }

            if (document.Version > 1)
            {
                // Carried on, unlike StateStore. A newer WinLogRotate wrote this and was then
                // rolled back; the cost of ignoring it is one redundant notification, and the
                // cost of refusing to run would be a rotation that does not happen.
                return new NotifyStateStore(path, Fresh(),
                    $"the notification state file declares version {document.Version}, "
                    + "which this build does not understand");
            }

            // Case-insensitive because job names are, and because the dictionary comes back from
            // System.Text.Json with the default ordinal comparer whatever it was written with -
            // the same trap that made stored secret names case-sensitive after a restart.
            document = document with
            {
                Jobs = new Dictionary<string, JobNotifyState>(document.Jobs, StringComparer.OrdinalIgnoreCase),
                Channels = new Dictionary<string, ChannelNotifyState>(document.Channels, StringComparer.OrdinalIgnoreCase),
            };

            return new NotifyStateStore(path, document, null)
            {
                AlgorithmChanged = !string.Equals(
                    document.Algorithm, NotifyFingerprint.Algorithm, StringComparison.Ordinal),
            };
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return new NotifyStateStore(path, Fresh(),
                $"the notification state file could not be read ({e.Message})");
        }
    }

    private static NotifyStateDocument Fresh() => new()
    {
        Jobs = new Dictionary<string, JobNotifyState>(StringComparer.OrdinalIgnoreCase),
        Channels = new Dictionary<string, ChannelNotifyState>(StringComparer.OrdinalIgnoreCase),
    };

    /// <summary>Records what a job was last told about.</summary>
    public void SetJob(string job, JobNotifyState state) => _document.Jobs[job] = state;

    /// <summary>Records a channel's breaker.</summary>
    public void SetChannel(string key, ChannelNotifyState state) => _document.Channels[key] = state;

    public JobNotifyState JobOrDefault(string job) =>
        _document.Jobs.TryGetValue(job, out var found) ? found : new JobNotifyState();

    public ChannelNotifyState ChannelOrDefault(string key) =>
        _document.Channels.TryGetValue(key, out var found) ? found : new ChannelNotifyState();

    /// <summary>
    /// Forgets jobs that have not been seen for a while.
    /// </summary>
    /// <remarks>
    /// By age rather than by absence from the current configuration. A job filtered out by
    /// <c>--job</c>, or in a file that failed to parse this once, is absent from the run but has
    /// not been deleted - and forgetting it would send a spurious "new failure" the next time it
    /// appeared.
    /// </remarks>
    public void Prune(DateTimeOffset now, TimeSpan keepFor)
    {
        var stale = _document.Jobs
            .Where(kv => kv.Value.LastSeen is { } seen && now - seen > keepFor)
            .Select(kv => kv.Key)
            .ToArray();

        foreach (var job in stale)
        {
            _document.Jobs.Remove(job);
        }

        // Channels too, from milestone 10 onwards. Nothing wrote them before delivery existed, so
        // it never mattered; now a target that is renamed, retired or typo'd once leaves a row
        // behind for ever, and notify status would grow a permanent list of destinations that no
        // longer exist. Keyed on the last attempt rather than on the configuration, so a channel
        // dropped for a fortnight and put back keeps its breaker history.
        var forgotten = _document.Channels
            .Where(kv => kv.Value.LastAttempt is { } attempt && now - attempt > keepFor)
            .Select(kv => kv.Key)
            .ToArray();

        foreach (var channel in forgotten)
        {
            _document.Channels.Remove(channel);
        }
    }

    public void Save(TimeProvider clock)
    {
        _document = _document with
        {
            Written = clock.GetUtcNow(),

            // Stamped on write, so a file written by this build always claims this build's
            // algorithm even when it was loaded from an older one.
            Algorithm = NotifyFingerprint.Algorithm,
        };

        AtomicJson.Write(
            _path, JsonSerializer.Serialize(_document, NotifyStateJsonContext.Default.NotifyStateDocument));
    }
}
