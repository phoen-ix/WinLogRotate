namespace WinLogRotate.Contracts;

/// <summary>
/// Which record is the last word about an operation. The only place that decides.
/// </summary>
/// <remarks>
/// <para>
/// The journal writes two records for every destructive operation - <c>plan</c>, then
/// <c>apply</c> - so a run killed between them leaves a readable account of an intention that
/// was never carried out. That pairing is explained in four places and was, until this lived
/// here, acted on in exactly one: a private method serving the live stream. Both readers of the
/// persisted journal counted the records rather than the operations, and both told an operator
/// twice the truth.
/// </para>
/// <para>
/// There are two questions here, not one, and conflating them is the trap. A watcher asks
/// <see cref="Settles"/>: does this record end the matter, with no lookahead available because
/// the run is still going? A reader of a finished journal asks <see cref="Collapse"/>: which
/// record is the last word about each operation - and there, the <i>absence</i> of a settling
/// record is itself the answer.
/// </para>
/// </remarks>
public static class CliEventLastWord
{
    /// <summary>
    /// Whether this record ends the matter by itself.
    /// </summary>
    /// <remarks>
    /// The plan half of an operation that is about to be applied does not: the apply half
    /// follows and says whether it worked. Everything else does - a skip is decided at plan time
    /// and never applied, a guard verdict is reached at plan time and is final, and the run and
    /// job brackets are statements in their own right.
    /// </remarks>
    public static bool Settles(CliEvent e) => e.Phase != Phase.Plan || e.Result is not null;

    /// <summary>
    /// One record per operation, in the order they were written.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A fold rather than a filter, and that is the whole of the design. <see cref="Settles"/>
    /// is false for a plan half with no result - which is correct for a watcher, because an
    /// apply half is coming, and catastrophic for a reader, because on a finished journal that
    /// record is precisely the evidence of a run that died holding a file open. Filtering by the
    /// predicate would delete the most important line in the file from the default view of the
    /// verb that exists to find it.
    /// </para>
    /// <para>
    /// So: every record that settles, plus every record whose operation nothing settled. Nothing
    /// is dropped, which is what makes collapsing safe to do by default.
    /// </para>
    /// <para>
    /// Idempotent, so a caller that cannot know whether its input was already collapsed - the
    /// GUI, talking to a CLI it did not ship with - can simply apply it.
    /// </para>
    /// </remarks>
    /// <param name="entries">
    /// The whole sequence. Demanded as a list rather than an <c>IEnumerable</c> because this
    /// rule genuinely needs to see every record before it can answer for any of them, and a
    /// signature that hides a buffer is a signature that lies about its cost.
    /// </param>
    public static IReadOnlyList<CliEvent> Collapse(IReadOnlyList<CliEvent> entries)
    {
        var settled = new HashSet<(string, string, string?, string?, string?, string?)>();

        foreach (var e in entries)
        {
            if (Settles(e))
            {
                settled.Add(Identity(e));
            }
        }

        var kept = new List<CliEvent>(entries.Count);

        foreach (var e in entries)
        {
            if (Settles(e) || !settled.Contains(Identity(e)))
            {
                kept.Add(e);
            }
        }

        return kept;
    }

    /// <summary>
    /// What makes two records halves of one operation.
    /// </summary>
    /// <remarks>
    /// <c>Run</c> is in the key, so the same path deleted on two nights is two operations rather
    /// than one. <c>Reason</c> is in it because <c>PlanExecutor</c> and <c>HookRunner</c> both
    /// write the same reason on both halves, so it is stable across a pair - and it is what
    /// separates a prerotate hook from a postrotate hook that run the same command line.
    /// </remarks>
    private static (string, string, string?, string?, string?, string?) Identity(CliEvent e) =>
        (e.Run, e.Operation, e.Job, e.Src, e.Dst, e.Reason);
}
