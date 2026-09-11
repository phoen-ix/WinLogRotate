using WinLogRotate.Contracts;

namespace WinLogRotate.Core.Journaling;

/// <summary>
/// The append-only record of everything WinLogRotate compressed, moved or deleted.
/// <para>
/// This is the answer to "what happened to my logs", and it is why
/// nothing reads Task Scheduler's Last Run Result: the task we register
/// deliberately exits 0 when another run holds the lock, so 0x0 does not prove work happened.
/// The journal does.
/// </para>
/// </summary>
public interface IJournal : IDisposable
{
    /// <summary>Identifier shared by every event of this invocation.</summary>
    string RunId { get; }

    void Write(CliEvent entry);
}

/// <summary>A journal that records nothing. For dry runs of the journal's own tests, and for
/// callers that have not opened one.</summary>
public sealed class NullJournal : IJournal
{
    public string RunId => "00000000000000000000000000";

    public void Write(CliEvent entry) { }

    public void Dispose() { }
}
