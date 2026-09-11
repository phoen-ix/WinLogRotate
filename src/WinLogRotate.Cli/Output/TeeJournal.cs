using WinLogRotate.Contracts;
using WinLogRotate.Core.Journaling;

namespace WinLogRotate.Cli.Output;

/// <summary>
/// Writes every journal entry to the journal, and the ones worth watching to the sink.
/// </summary>
/// <remarks>
/// <para>
/// <c>IOutputSink.Line</c> is discarded under <c>--json</c> on the stated grounds that "the same
/// information is carried as diagnostics and events" - and <c>RunCommand</c> called
/// <c>Output.Event</c> zero times, reporting every per-file line through the channel that is
/// thrown away. So <c>--json-stream</c> was silent about the one verb it exists for, and the GUI
/// tailed an empty file.
/// </para>
/// <para>
/// The fix belongs here rather than in the engine. <c>IJournal</c> is a two-member seam the CLI
/// already constructs, so wrapping it reaches every event the engine writes - run and job
/// brackets, each operation, guard verdicts, NUL-fill findings, hooks - without Core learning
/// what a sink is.
/// </para>
/// <para>
/// The journal and the stream are not the same audience. The journal is the forensic record and
/// keeps both halves of every operation, so a run killed between them leaves evidence of an
/// intention that was never carried out. A watcher wants the last word instead: told "would
/// delete" and then nothing, an operator cannot tell a completed deletion from an abandoned one.
/// </para>
/// </remarks>
internal sealed class TeeJournal(IJournal inner, IOutputSink sink, TimeProvider clock, bool dryRun)
    : IJournal
{
    public string RunId => inner.RunId;

    public void Write(CliEvent entry)
    {
        // Stamped here rather than by the journal. JournalWriter fills Ts and Run because the
        // engine leaves them empty - but a dry run journals to NullJournal, which stamps
        // nothing, so a watcher would get blank timestamps on exactly the run it is watching.
        var stamped = entry with
        {
            Run = entry.Run.Length == 0 ? inner.RunId : entry.Run,
            Ts = entry.Ts.Length == 0 ? clock.GetUtcNow().ToString("O") : entry.Ts,
        };

        inner.Write(stamped);

        if (IsLastWord(stamped))
        {
            sink.Event(stamped);
        }
    }

    /// <summary>
    /// Whether this entry is the final thing that will be said about its operation.
    /// </summary>
    /// <remarks>
    /// The rule itself is <see cref="CliEventLastWord.Settles"/>, shared with the readers of the
    /// persisted journal so the live account and the recorded one cannot disagree about what one
    /// operation is. What stays here is the dry-run arm, because dry-ness is a property of the
    /// run and not of the event: a dry run stops before the apply half, so its plan events are
    /// all there is, and a reader of a journal has no such flag to consult - nor any need of
    /// one, since a dry run journals to NullJournal and persists nothing.
    /// </remarks>
    private bool IsLastWord(CliEvent entry) => dryRun || CliEventLastWord.Settles(entry);

    public void Dispose() => inner.Dispose();
}
