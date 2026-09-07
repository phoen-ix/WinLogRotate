using System.Text.Json;
using WinLogRotate.Core.Safety;

namespace WinLogRotate.Core.State;

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

    /// <summary>Loads state, or starts fresh if there is none.</summary>
    /// <param name="corrupt">Set when an existing file could not be read and was replaced.</param>
    public static StateStore Load(string path, out string? corrupt)
    {
        corrupt = null;

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
                corrupt = "the state file was empty";
                return new StateStore(path, new StateDocument());
            }

            if (document.Version > 1)
            {
                // Refuse rather than guess. Misreading state means rotating on the wrong
                // schedule, which is worse than starting over from a known baseline.
                throw new InvalidOperationException(
                    $"The state file at {path} declares version {document.Version}, but this build understands 1. " +
                    "Upgrade WinLogRotate, or move the file aside to start from a fresh baseline.");
            }

            return new StateStore(path, document);
        }
        catch (JsonException e)
        {
            // A truncated state file (power loss mid-write, though the atomic save makes that
            // unlikely) must not stop the tool from running. Starting from a fresh baseline
            // delays each log by one interval, which is a far better outcome than refusing to
            // rotate anything at all.
            corrupt = e.Message;
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

    /// <summary>Forgets entries whose files no longer exist, so the file cannot grow forever.</summary>
    public int Prune(Func<string, bool> stillExists)
    {
        var gone = _document.Paths
            .Where(kv => !stillExists(kv.Value.Path))
            .Select(kv => kv.Key)
            .ToArray();

        foreach (var key in gone)
        {
            _document.Paths.Remove(key);
        }

        return gone.Length;
    }

    /// <summary>
    /// Writes state atomically: a temporary sibling, flushed to disk, then moved over the
    /// target. A crash leaves either the previous state or the new one - never a half-written
    /// file, which would cost every log its rotation clock.
    /// </summary>
    public void Save(TimeProvider clock)
    {
        _document = _document with { Written = clock.GetUtcNow() };

        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = _path + ".tmp";
        var json = JsonSerializer.Serialize(_document, StateJsonContext.Default.StateDocument);

        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(json);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, _path, overwrite: true);
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
