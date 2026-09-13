using WinLogRotate.Core.Safety;

namespace WinLogRotate.Core.Configuration;

/// <summary>One key an editor asked to change.</summary>
/// <param name="Key">A <c>[job]</c> key, as <see cref="JobSchema"/> spells it.</param>
/// <param name="Value">The text to parse, or null to remove the key so it inherits again.</param>
public readonly record struct JobEdit(string Key, string? Value);

/// <summary>What became of one key.</summary>
public enum JobChangeKind
{
    /// <summary>The key now says something it did not say before.</summary>
    Set,

    /// <summary>The key is gone, so the job inherits that setting again.</summary>
    Unset,

    /// <summary>It already said that, or it was already absent. The file was not touched.</summary>
    Unchanged,
}

/// <summary>One key's before and after, in the file's own spelling.</summary>
public sealed record JobChange
{
    public required string Key { get; init; }
    public required JobChangeKind Kind { get; init; }

    /// <summary>The TOML source the key had, or null if it had none.</summary>
    public string? Before { get; init; }

    /// <summary>The TOML source it has now, or null if it was removed.</summary>
    public string? After { get; init; }
}

/// <summary>A key that could not be applied at all, named so the caller can say which.</summary>
public sealed record JobEditProblem
{
    public required string Key { get; init; }
    public required string Message { get; init; }
    public string? Remedy { get; init; }
}

/// <summary>
/// An edit applied to a document in memory and judged, but not written.
/// </summary>
/// <remarks>
/// This is what makes <c>--dry-run</c> honest. A form that validates through one path and saves
/// through another reports a green tick and then fails; here the two differ by whether the caller
/// goes on to call <c>Save</c>, and by nothing else.
/// </remarks>
public sealed record JobProposal
{
    /// <summary>The edited document. Not saved, and not to be saved if anything below refuses.</summary>
    public required TomlFile File { get; init; }

    public required IReadOnlyList<JobChange> Changes { get; init; }

    /// <summary>Keys that could not be applied. Non-empty means nothing was applied at all.</summary>
    public required IReadOnlyList<JobEditProblem> Problems { get; init; }

    /// <summary>
    /// What a run would make of the edited file - null when <see cref="Problems"/> is non-empty,
    /// because a document that was never fully edited must not be judged as though it were.
    /// </summary>
    public JobVerdict? Verdict { get; init; }

    /// <summary>Whether saving would change any byte of the file.</summary>
    public bool Changed => Changes.Any(c => c.Kind != JobChangeKind.Unchanged);

    /// <summary>Whether this proposal may be written.</summary>
    public bool Writable =>
        Problems.Count == 0 && Verdict is { Outcome: JobOutcome.Ready, HasErrors: false };
}

/// <summary>
/// A job file, edited key by key and never round-tripped through a model.
/// </summary>
/// <remarks>
/// <para>
/// The whole design of the job verbs is here. An edit holds a <see cref="TomlFile"/> - Tomlyn's
/// syntax tree, comments and blank lines and all - applies one set or one removal per key the
/// caller <b>named</b>, and saves. Keys nobody named are not read, not rewritten, and not
/// reordered. <see cref="Skeleton"/> means <c>job add</c> is the same path over an empty file
/// rather than a second one.
/// </para>
/// <para>
/// Deliberately not the obvious design, which is to bind the file to a <see cref="JobConfig"/>,
/// change a field, and write it back. That loses every comment, and worse: the only job object
/// this product publishes is <see cref="EffectiveJob"/>, with <c>[defaults]</c> already merged in,
/// so writing one back would sever the job from <c>[defaults]</c> on its first save - silently,
/// and permanently. Here no merged value is ever in scope on the write side, so that cannot
/// happen rather than merely not happening.
/// </para>
/// <para>
/// The corollary is the rule the <c>[defaults]</c> decision needs: a cleared field means
/// <i>inherit again</i>, never <i>set to zero</i>. That is <see cref="JobChangeKind.Unset"/>, and
/// it is why an edit carries a null value rather than an empty string.
/// </para>
/// </remarks>
public static class JobDocument
{
    /// <summary>The table every job key lives in.</summary>
    public static readonly string[] Table = ["job"];

    /// <summary>Where each key sits in the schema, so an applied edit can be put back in order.</summary>
    private static readonly Dictionary<string, int> Order = JobSchema.Keys
        .Select((k, i) => (k.Key, i))
        .ToDictionary(p => p.Key, p => p.i, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// An empty job file: the schema line, and the table.
    /// </summary>
    /// <remarks>
    /// A constant, because <see cref="TomlEditor"/> deliberately refuses to create tables - its
    /// own doc comment argues that deciding where a table belongs in somebody's file is where an
    /// editor becomes a formatter. A new file has no such question to answer.
    /// </remarks>
    public static TomlFile Skeleton(string path) =>
        TomlFile.Parse("schema = 1\n\n[job]\n", path);

    /// <summary>
    /// Applies the named keys and judges the result, without writing anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Keys are applied in <see cref="JobSchema"/> order rather than in the order the caller
    /// listed them, so a file this product creates reads in the same six groups as
    /// <c>docs/configuration.md</c>. An existing file keeps its own order, because an existing
    /// key is replaced where it already is.
    /// </para>
    /// <para>
    /// A key the caller named twice is one edit. For a list key that means the values accumulate
    /// into one array - <c>--paths A --paths B</c> - because <see cref="JobSchema.TryParse"/>
    /// deliberately does not split on a separator: a Windows path may contain any of them.
    /// </para>
    /// </remarks>
    public static JobProposal Apply(
        TomlFile file,
        string path,
        IReadOnlyList<JobEdit> edits,
        JobSettings? defaults,
        PathGuard guard,
        IReadOnlyDictionary<string, string> namesInUse)
    {
        var problems = new List<JobEditProblem>();
        var planned = new List<(JobKey Row, string? Value, IReadOnlyList<string> Items)>();

        foreach (var group in edits.GroupBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (JobSchema.Find(group.Key) is not { } row)
            {
                problems.Add(new JobEditProblem
                {
                    Key = group.Key,
                    Message = $"'{group.Key}' is not a [job] key.",
                    Remedy = JobSchema.Nearest(group.Key) is { } near
                        ? $"Did you mean '{near}'?"
                        : "Run 'winlogrotate job show' on an existing job, or see docs/configuration.md.",
                });
                continue;
            }

            // A removal wins over a set of the same key. Nothing in the CLI produces both, and
            // saying so here means a caller that does gets the safer of the two rather than
            // whichever came last.
            if (group.Any(e => e.Value is null))
            {
                planned.Add((row, null, []));
                continue;
            }

            var items = group.Select(e => e.Value!).ToArray();

            foreach (var text in items)
            {
                if (!JobSchema.TryParse(row.Key, text, out _, out var problem))
                {
                    problems.Add(new JobEditProblem
                    {
                        Key = row.Key,
                        Message = $"'{text}' is {problem}",
                        Remedy = $"For example: {row.Key} = {row.Sample}",
                    });
                }
            }

            planned.Add((row, items[^1], items));
        }

        if (problems.Count > 0)
        {
            // Nothing applied, so nothing judged. Reporting a validation verdict for a document
            // that is missing the keys that failed to parse would be a verdict about a job
            // nobody proposed.
            return new JobProposal { File = file, Changes = [], Problems = problems };
        }

        var changes = new List<JobChange>();

        foreach (var (row, value, items) in planned.OrderBy(p => Order[p.Row.Key]))
        {
            TomlEditor.TryRead(file, Table, row.Key, out var before, out _);

            if (value is null)
            {
                var removed = TomlEditor.TryRemove(file, Table, row.Key, out var error, out var detail);

                if (!removed && error is not TomlEditError.NoSuchKey)
                {
                    problems.Add(new JobEditProblem { Key = row.Key, Message = detail ?? "The key could not be removed." });
                    continue;
                }

                changes.Add(new JobChange
                {
                    Key = row.Key,
                    Kind = removed ? JobChangeKind.Unset : JobChangeKind.Unchanged,
                    Before = before,
                });
                continue;
            }

            // Re-parsed rather than carried from the loop above, because a list key's value is
            // every item at once and the loop above checked them one at a time.
            var written = row.Kind == JobKeyKind.TextList
                ? TomlValue.List(items)
                : Parsed(row, value);

            var after = written.ToString();

            if (string.Equals(before, after, StringComparison.Ordinal))
            {
                changes.Add(new JobChange
                {
                    Key = row.Key,
                    Kind = JobChangeKind.Unchanged,
                    Before = before,
                    After = after,
                });
                continue;
            }

            if (!TomlEditor.TrySet(file, Table, row.Key, written, out _, out var why))
            {
                problems.Add(new JobEditProblem { Key = row.Key, Message = why ?? "The key could not be written." });
                continue;
            }

            changes.Add(new JobChange
            {
                Key = row.Key,
                Kind = JobChangeKind.Set,
                Before = before,
                After = after,
            });
        }

        return problems.Count > 0
            ? new JobProposal { File = file, Changes = changes, Problems = problems }
            : new JobProposal
            {
                File = file,
                Changes = changes,
                Problems = [],
                Verdict = ConfigLoader.Judge(file, path, defaults, guard, namesInUse),
            };
    }

    private static TomlValue Parsed(JobKey row, string text)
    {
        JobSchema.TryParse(row.Key, text, out var value, out _);
        return value;
    }
}
