using System.Text;
using WinLogRotate.Contracts;

namespace WinLogRotate.Gui.Cli;

/// <summary>
/// Follows the NDJSON file an elevated child writes, and hands back sentences.
/// </summary>
/// <remarks>
/// <para>
/// An elevated "runas" child cannot have its pipes redirected, so the GUI passes
/// <c>--json-stream --output</c> and reads the file while it is still being appended to. That
/// makes this the one place in the product that has to reason about a partially written file,
/// and it had three defects in thirty lines - none of them findable where it used to live,
/// because a test project that references the GUI inherits <c>Microsoft.WindowsDesktop.App</c>
/// and cannot run on the Linux leg at all.
/// </para>
/// <para>
/// It reads bytes and decodes only up to the last newline. That is what fixes all three at once:
/// a line still being written is not emitted, a UTF-8 sequence split across a poll is left
/// whole for the next one, and a read that throws mid-write has emitted nothing, so resuming
/// from the same offset repeats nothing.
/// </para>
/// </remarks>
public static class EventTail
{
    /// <summary>How long to wait between polls. Short enough to feel live, cheap enough to ignore.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Reports each event as a sentence until the child exits, then once more.
    /// </summary>
    /// <param name="hasExited">
    /// Asked rather than given a <c>Process</c>, so the loop can be driven by a test on a
    /// platform where the thing being followed is a file and nothing else.
    /// </param>
    public static async Task FollowAsync(
        string path,
        Action<string>? onLine,
        Func<bool> hasExited,
        CancellationToken cancellationToken = default,
        TimeSpan? pollInterval = null)
    {
        if (onLine is null)
        {
            return;
        }

        // Rendered here, once, rather than by each page. The file is NDJSON, and what the
        // operator was shown was NDJSON: one object per line, in the pane of a button labelled
        // "Rotate now". CliEventText is the same rendering the CLI's own text output uses, so
        // the two cannot drift, and it answers null for the envelope that shares this file -
        // which is a result, not a line of progress.
        void Render(string line)
        {
            if (CliEventText.Describe(line) is { } sentence)
            {
                onLine(sentence);
            }
        }

        var offset = 0L;

        while (!hasExited() && !cancellationToken.IsCancellationRequested)
        {
            offset = ReadFrom(path, offset, Render);
            await Task.Delay(pollInterval ?? PollInterval, cancellationToken).ConfigureAwait(false);
        }

        // One last pass: the child may have written its final lines between our last poll and
        // its exit, and those are usually the ones that say what happened.
        ReadFrom(path, offset, Render);
    }

    /// <summary>
    /// Reports every complete line after <paramref name="offset"/> and returns where to resume.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The returned offset is a byte count into the file, never a character count, because that
    /// is what a seek takes and what the next poll has to be able to trust.
    /// </para>
    /// <para>
    /// Nothing is reported until the whole read has succeeded. That is what makes resuming from
    /// the unchanged offset after an <see cref="IOException"/> correct: the old code passed each
    /// line to the caller as it read it and then, on a throw, returned the offset it started
    /// from - re-delivering every line it had just handed over.
    /// </para>
    /// </remarks>
    public static long ReadFrom(string path, long offset, Action<string> onLine)
    {
        byte[] bytes;

        try
        {
            // Shares Delete as well as ReadWrite: the child is appending to this file and the
            // GUI sweeps the directory afterwards. Denying either would make the sweep fail.
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            if (stream.Length <= offset)
            {
                return offset;
            }

            stream.Seek(offset, SeekOrigin.Begin);
            bytes = new byte[stream.Length - offset];
            var read = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);

            if (read < bytes.Length)
            {
                Array.Resize(ref bytes, read);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The child is mid-write, or has not created the file yet. Try again on the next
            // poll, from exactly where this one started.
            return offset;
        }

        // The last complete line is the last one a reader may trust. Everything after it is
        // still being written - and reading to end-of-file and calling that a line is how
        // {"ts":"2026-09 reached an operator's screen, with the remainder arriving later as a
        // second line that parses as nothing at all.
        var end = Array.LastIndexOf(bytes, (byte)'\n');
        if (end < 0)
        {
            return offset;
        }

        // A newline is never part of a multi-byte UTF-8 sequence, so a prefix ending at one is
        // always safe to decode. A sequence split by the poll boundary therefore sits after
        // this point and is decoded whole by a later pass - where the previous code built a
        // fresh StreamReader per poll, decoded the fragment to U+FFFD, and advanced past it, so
        // a path with a non-ASCII character in it was permanently corrupted rather than delayed.
        var start = offset == 0 && bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            ? 3
            : 0;

        foreach (var line in Encoding.UTF8.GetString(bytes, start, end + 1 - start).Split('\n'))
        {
            var text = line.TrimEnd('\r');
            if (text.Length > 0)
            {
                onLine(text);
            }
        }

        return offset + end + 1;
    }
}
