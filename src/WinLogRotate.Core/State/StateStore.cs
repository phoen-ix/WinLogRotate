using System.Text.Json;
using WinLogRotate.Core.Safety;

namespace WinLogRotate.Core.State;

/// <summary>Why a state file could not be used as it was.</summary>
public enum StateProblemKind
{
    /// <summary>The file would not parse: a torn write, or something that is not this product's JSON.</summary>
    Corrupt,

    /// <summary>The file exists and could not be read: held open without sharing, or an ACL that denies this account.</summary>
    Unreadable,

    /// <summary>The file declares a version this build does not understand.</summary>
    Unsupported,
}

/// <summary>What was wrong with a state file, for the caller to report or refuse on.</summary>
public sealed record StateProblem(StateProblemKind Kind, string Message);

/// <summary>
/// The rotation clock, remembered between runs.
/// </summary>
/// <remarks>
/// <para>
/// This file is deliberately independent of which host runs rotations. Switching between a
/// Scheduled Task, a Windows Service and manual invocation must not reset anything - get that
/// wrong and every log silently re-rotates the first time an operator changes their mind about
/// scheduling.
/// </para>
/// <para>
/// Paths are keyed canonically, because <c>C:\Logs\App.log</c> and <c>c:\logs\app.log</c> are
/// one file and must not become two entries with independent clocks.
/// </para>
/// </remarks>
public sealed class StateStore
{
    private readonly string _path;
    private StateDocument _document;

    private StateStore(string path, StateDocument document)
    {
        _path = path;
        _document = document;
    }

    public IReadOnlyDictionary<string, PathState> Paths => _document.Paths;

    /// <summary>Where this store reads and writes, so a failure to write can name it.</summary>
    public string Path => _path;

    /// <summary>Loads state, or starts fresh if there is none.</summary>
    /// <param name="problem">
    /// Set when the file was there and could not be used as it was. The store returned beside a
    /// problem is a fresh baseline; whether to run on it is the caller's decision, and for
    /// anything but <see cref="StateProblemKind.Corrupt"/> the answer is no - a baseline saved
    /// over a file that could have been read tomorrow replaces everything it held.
    /// </param>
    public static StateStore Load(string path, out StateProblem? problem)
    {
        problem = null;

        if (!File.Exists(path))
        {
            return new StateStore(path, new StateDocument());
        }

        try
        {
            var json = File.ReadAllText(path);
            var document = JsonSerializer.Deserialize(json, StateJsonContext.Default.StateDocument);

            if (document is null)
            {
                problem = new StateProblem(StateProblemKind.Corrupt, "the state file was empty");
                return new StateStore(path, new StateDocument());
            }

            if (document.Version > 1)
            {
                // Refused rather than guessed at, and reported rather than thrown. Misreading
                // state means rotating on the wrong schedule; starting over throws every verdict
                // in the file away just as surely. This used to be an InvalidOperationException
                // nothing caught, so a state file from a newer build ended every run at exit 4
                // with "a defect in the product" until somebody noticed.
                problem = new StateProblem(
                    StateProblemKind.Unsupported,
                    $"the state file at {path} declares version {document.Version}, but this build understands 1");
                return new StateStore(path, new StateDocument());
            }

            return new StateStore(path, document);
        }
        catch (JsonException e)
        {
            // A truncated state file (power loss mid-write, though the atomic save makes that
            // unlikely) must not stop the tool from running. Starting from a fresh baseline
            // delays each log by one interval, which is a far better outcome than refusing to
            // rotate anything at all.
            problem = new StateProblem(StateProblemKind.Corrupt, e.Message);
            return new StateStore(path, new StateDocument());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The file is there and this account could not read it - a backup agent holding it
            // open, or an ACL. Unguarded, this reached CommandContext.Guarded as LR1006.
            problem = new StateProblem(StateProblemKind.Unreadable, e.Message);
            return new StateStore(path, new StateDocument());
        }
    }

    public PathState? Get(string path) =>
        _document.Paths.GetValueOrDefault(WinPath.CanonicalKey(path));

    public void Set(string path, PathState state) =>
        _document.Paths[WinPath.CanonicalKey(path)] = state with { Path = path };

    /// <summary>Updates one entry, creating it if this is the first sighting.</summary>
    public PathState Update(string path, Func<PathState, PathState> change)
    {
        var key = WinPath.CanonicalKey(path);
        var existing = _document.Paths.GetValueOrDefault(key) ?? new PathState { Path = path };
        var updated = change(existing) with { Path = path };
        _document.Paths[key] = updated;
        return updated;
    }

    /// <summary>
    /// Records the first sighting of a log without rotating it.
    /// </summary>
    /// <remarks>
    /// logrotate's behaviour, replicated deliberately: a log encountered for the first time has
    /// "now" recorded as its baseline and is not rotated until a full interval has passed.
    /// Anyone who has ever deleted a state file and wondered why nothing rotated that night has
    /// met this rule. <c>--catchup</c> opts out; the GUI offers the same choice when a job is
    /// added to an already-enormous log.
    /// </remarks>
    public bool RecordFirstSighting(string path, DateTimeOffset now)
    {
        var key = WinPath.CanonicalKey(path);
        if (_document.Paths.ContainsKey(key))
        {
            return false;
        }

        _document.Paths[key] = new PathState { Path = path, FirstSeen = now, LastRotated = now };
        return true;
    }

    /// <summary>How long a path goes unseen before its clock is forgotten.</summary>
    /// <remarks>
    /// Longer than the notification side's thirty days, because the cost of being wrong is
    /// different: forgetting a notification repeats a message, forgetting a rotation clock
    /// re-baselines a log. A quarterly job, or a machine switched off for a month, must survive.
    /// </remarks>
    public const int ForgetAfterDays = 90;

    /// <summary>
    /// Forgets paths no run has seen for a while, so the file cannot grow forever.
    /// </summary>
    /// <remarks>
    /// <para>
    /// By age, and deliberately not by absence. This took a <c>Func&lt;string, bool&gt;
    /// stillExists</c> and had no production caller at all, which was the only reason it had never
    /// done any damage: rename is the default strategy and a rotation emits the rename and the
    /// recreate as two separate operations, so "the file is absent" is a state a run produces
    /// about itself. <c>NotifyStateStore.Prune</c> already refuses absence as a criterion for a
    /// weaker reason than this one.
    /// </para>
    /// <para>
    /// <b>A confirmed NUL-fill is never forgotten, at any age.</b> That verdict means copytruncate
    /// destroyed this log once already, its enum calls it permanent, and <c>LockChoice.Judge</c>
    /// deliberately preserves it even when the file has gone - "the defect belongs to the writer
    /// rather than to the inode". Ageing it out would silently re-enable, on a ninety-day timer,
    /// the one operation known to corrupt this path. That set is therefore unbounded, and the
    /// remedy for it is fixing the writer rather than quietly forgetting.
    /// </para>
    /// <para>
    /// A row carrying no timestamp at all is kept rather than treated as infinitely old, so a
    /// state file written before <c>LastSeen</c> existed is not emptied by its first prune.
    /// </para>
    /// </remarks>
    public int Prune(DateTimeOffset now, TimeSpan keepFor)
    {
        var stale = _document.Paths
            .Where(kv => kv.Value.NulFill != NulFillVerdict.Confirmed)
            .Where(kv => (kv.Value.LastSeen ?? kv.Value.LastRotated ?? kv.Value.FirstSeen)
                is { } when && now - when > keepFor)
            .Select(kv => kv.Key)
            .ToArray();

        foreach (var key in stale)
        {
            _document.Paths.Remove(key);
        }

        return stale.Length;
    }

    /// <summary>
    /// Writes state atomically: a temporary sibling, flushed to disk, then moved over the
    /// target. A crash leaves either the previous state or the new one - never a half-written
    /// file, which would cost every log its rotation clock.
    /// </summary>
    public void Save(TimeProvider clock)
    {
        _document = _document with { Written = clock.GetUtcNow() };

        AtomicJson.Write(
            _path, JsonSerializer.Serialize(_document, StateJsonContext.Default.StateDocument));
    }

    /// <summary>
    /// A stable fingerprint of the rotation clocks, ignoring bookkeeping like the write
    /// timestamp. Used by the test that proves switching run hosts does not disturb state.
    /// </summary>
    public string ClockFingerprint() =>
        string.Join('\n', _document.Paths
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={kv.Value.LastRotated:O}"));
}
