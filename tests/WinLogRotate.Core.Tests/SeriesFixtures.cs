using WinLogRotate.Core.Engine;
using WinLogRotate.Core.Globbing;
using WinLogRotate.Core.Safety;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// A file system that is a dictionary, so discovery and planning are testable without one.
/// </summary>
/// <remarks>
/// Shared rather than private to one test class, because the interesting question about a plan is
/// what it does to a <b>directory</b>, and the directory has to be the same one discovery walked.
/// Handing a planner an archive list by hand proves the planner is fragile; it cannot prove the
/// product produces that list - which is the whole of the duplicate-index defect.
/// </remarks>
internal sealed class FakeFiles : IArchiveSource
{
    /// <summary>The date every file gets unless one is named. Arbitrary, and old.</summary>
    private static readonly DateTimeOffset Epoch = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly Dictionary<string, MatchedFile> _files = new(StringComparer.OrdinalIgnoreCase);

    public FakeFiles(params string[] paths)
    {
        foreach (var path in paths)
        {
            Add(path);
        }
    }

    /// <summary>Every path asked about, in order, so the cost of looking can be asserted.</summary>
    public List<string> Probed { get; } = [];

    public IReadOnlyList<MatchedFile> All => [.. _files.Values];

    /// <summary>Adds a file, dated and sized where that matters to the test.</summary>
    public FakeFiles Add(string path, DateTimeOffset? lastWrite = null, long bytes = 10)
    {
        _files[path] = new MatchedFile
        {
            Path = path,
            Length = bytes,
            LastWriteUtc = lastWrite ?? Epoch,
        };

        return this;
    }

    public MatchedFile? Find(string path)
    {
        Probed.Add(path);
        return _files.GetValueOrDefault(path);
    }

    public IReadOnlyList<MatchedFile> Glob(string pattern) =>
        [.. _files.Values.Where(f => Globbing.Glob.IsMatch(f.Path, pattern))];
}

/// <summary>One file in the directory a plan left behind, and where it came from.</summary>
internal sealed record OutcomeFile
{
    public required string Path { get; init; }

    /// <summary>
    /// The path this file was seeded under, or <c>null</c> if the plan produced it.
    /// </summary>
    /// <remarks>
    /// The load-bearing field, and the reason this models identity rather than existence. Every
    /// rename-strategy plan recreates the live log, so a directory keyed only on paths holds two
    /// files called <c>app.log</c> - the seeded one now at <c>app.log.1</c> and the fresh one -
    /// and no assertion can name either. More importantly, "which rule condemned this file" is
    /// only answerable against the file's own identity: the shift re-creates a path the moment
    /// after it vacates it, so a deletion of the right <i>name</i> can still be a deletion of the
    /// wrong <i>file</i>.
    /// </remarks>
    public string? Origin { get; init; }

    public DateTimeOffset LastWriteUtc { get; init; }
    public long Length { get; init; }
}

/// <summary>The directory a plan leaves behind, and what it did to get there.</summary>
internal sealed record Outcome
{
    public required IReadOnlyList<OutcomeFile> Files { get; init; }

    /// <summary>Operations whose source was not there by the time the plan reached them.</summary>
    /// <remarks>
    /// Recorded rather than thrown, because the planner is allowed to be wrong here and the test
    /// is what says so. On a real run each of these is one failed operation, one journal line
    /// reading <c>failed to rename</c>, and a non-zero exit.
    /// </remarks>
    public required IReadOnlyList<string> Missing { get; init; }

    /// <summary>Destinations that already held a file when the plan wrote over them.</summary>
    /// <remarks>
    /// Also recorded rather than thrown: numbered rotation replaces silently on purpose, which
    /// <c>PlanDateExt</c>'s own comment states as the difference between the two paths. A test
    /// that cares asserts on it.
    /// </remarks>
    public required IReadOnlyList<string> Clobbered { get; init; }

    /// <summary>The file names left, ordered, for a whole-directory assertion.</summary>
    public IReadOnlyList<string> Names =>
        [.. Files.Select(f => WinPath.FileName(f.Path)).Order(StringComparer.Ordinal)];

    public OutcomeFile? At(string path) =>
        Files.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>Where a seeded file ended up, by its original name. Empty if it was destroyed.</summary>
    public IReadOnlyList<string> Became(string seededName) =>
    [
        .. Files
            .Where(f => f.Origin is { } o
                && string.Equals(WinPath.FileName(o), seededName, StringComparison.OrdinalIgnoreCase))
            .Select(f => WinPath.FileName(f.Path))
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>Whether the file seeded under this name is gone.</summary>
    public bool Lost(string seededName) => Became(seededName).Count == 0;

    /// <summary>
    /// Runs a plan over a seeded directory, in the order the executor would.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="PlanExecutor"/> walks <c>plan.Operations</c> with a plain <c>foreach</c>, so the
    /// list order <i>is</i> the execution order. That is what makes a plan judgeable without a
    /// file system - and it is also the thing every defect in this area turned on, because an
    /// operation queued earlier changes the directory the later ones were reasoned about.
    /// </para>
    /// <para>
    /// Which actions consume a source and which produce one is written down rather than inferred,
    /// because one assertion in this file rests on it: <see cref="PlannedAction.Create"/> names
    /// the live log as its source and runs immediately <i>after</i> the rename that moved the live
    /// log away. Treating it as a consumer reports every ordinary rotation as broken.
    /// </para>
    /// </remarks>
    public static Outcome Of(JobPlan plan, FakeFiles seed)
    {
        var files = new Dictionary<string, OutcomeFile>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        var clobbered = new List<string>();

        foreach (var file in seed.All)
        {
            files[file.Path] = new OutcomeFile
            {
                Path = file.Path,
                Origin = file.Path,
                LastWriteUtc = file.LastWriteUtc,
                Length = file.Length,
            };
        }

        void Put(string path, OutcomeFile file)
        {
            if (files.ContainsKey(path))
            {
                clobbered.Add(path);
            }

            files[path] = file with { Path = path };
        }

        foreach (var op in plan.Operations)
        {
            if (op.Action is PlannedAction.Skip or PlannedAction.CreateDirectory)
            {
                continue;
            }

            if (op.Action == PlannedAction.Create)
            {
                // A producer, and deliberately without an origin: the log the writer will find is
                // a new empty file, not the one that was archived a moment ago.
                files[op.Destination ?? op.Source] = new OutcomeFile
                {
                    Path = op.Destination ?? op.Source,
                    Origin = null,
                    LastWriteUtc = default,
                    Length = 0,
                };

                continue;
            }

            if (!files.TryGetValue(op.Source, out var source))
            {
                missing.Add(op.ToString());
                continue;
            }

            switch (op.Action)
            {
                case PlannedAction.Delete:
                    files.Remove(op.Source);
                    break;

                // Compressor writes the archive and only then deletes the original, and stamps
                // the archive with the original's own modification time - so the compressed file
                // is the same file under another name, which is exactly what Origin carries.
                case PlannedAction.Compress:
                case PlannedAction.Rename:
                    files.Remove(op.Source);
                    Put(op.Destination!, source);
                    break;

                // The original stays. Under copytruncate it stays at zero bytes, which is the
                // whole point of the strategy: the writer's handle and offset are undisturbed.
                case PlannedAction.Copy:
                case PlannedAction.CopyTruncate:
                    Put(op.Destination!, source);
                    files[op.Source] = op.Action == PlannedAction.CopyTruncate
                        ? source with { Length = 0 }
                        : source;
                    break;

                default:
                    throw new InvalidOperationException(
                        $"the directory model has nothing to say about {op.Action}");
            }
        }

        return new Outcome
        {
            Files = [.. files.Values],
            Missing = missing,
            Clobbered = clobbered,
        };
    }
}
