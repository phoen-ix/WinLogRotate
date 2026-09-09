using System.Globalization;
using System.Text;
using WinLogRotate.Contracts;

namespace WinLogRotate.Core.Notify;

/// <summary>
/// Turns a decision into words, and fits them into whatever the destination allows.
/// </summary>
/// <remarks>
/// Pure, and deterministic: the same plan renders to the same bytes every time, on every machine.
/// The machine name arrives as data rather than being read from the environment, so a test can
/// assert on the whole string.
/// </remarks>
public static class MessageComposer
{
    /// <summary>What each destination will accept. Anything longer is silently discarded there.</summary>
    /// <remarks>
    /// Only Pushover and the Event Log are applied automatically, because only those two are
    /// identified by their scheme. A webhook could be pointed at anything, so Discord and ntfy are
    /// reachable through a provider's <c>max_message</c> key - these constants are what the
    /// documentation quotes for it.
    /// </remarks>
    public const int PushoverLimit = 1024;
    public const int DiscordLimit = 2000;
    public const int NtfyLimit = 4096;
    public const int EventLogLimit = 31_839;

    /// <summary>For destinations that impose no limit worth enforcing: SMTP, a plain webhook.</summary>
    /// <remarks>
    /// Silently truncating an operator's diagnostic is a real cost, so it is paid only where the
    /// destination would otherwise discard the whole message.
    /// </remarks>
    public const int NoLimit = int.MaxValue;

    public static string Subject(NotifyReason reason, string job, IReadOnlyList<DigestLine> lines, RunSummary run)
    {
        var what = reason switch
        {
            NotifyReason.NewFailure => "FAILED",
            NotifyReason.Changed => "CHANGED",
            NotifyReason.Reminder => "STILL FAILING",
            NotifyReason.Recovered => "RECOVERED",
            _ => "RESOLVED",
        };

        var scope = job == State.NotifyStateDocument.RunScope ? "configuration" : job;
        var files = lines.Sum(l => l.Count);

        var detail = reason is NotifyReason.Recovered or NotifyReason.Resolved || files == 0
            ? string.Empty
            : $" - {files} {(files == 1 ? "problem" : "problems")}";

        return $"[WinLogRotate] {what} on {run.Machine} - {scope}{detail}";
    }

    /// <summary>
    /// The whole message, then trimmed to fit.
    /// </summary>
    /// <remarks>
    /// Composed in full and truncated afterwards rather than being built to a budget, so every
    /// destination renders the same message and only the tail differs. Building differently per
    /// destination is how two channels end up disagreeing about what happened.
    /// </remarks>
    /// <param name="now">
    /// Supplied rather than read, so the same plan renders to the same bytes in a test. Only the
    /// trailing "go and look" command uses it.
    /// </param>
    public static string Render(PlannedNotification message, RunSummary run, int limit,
        DateTimeOffset now, IReadOnlyList<string>? redact = null)
    {
        var head = Head(message, run);
        var tail = $"winlogrotate journal --since {now:yyyy-MM-dd}";

        var body = new List<string>();
        foreach (var line in message.Lines)
        {
            body.Add(Redaction.MaskText(Format(line), redact));
        }

        // A recovery is triggered by the threshold but described without it, so the reader is
        // not told the machine is clean when three warnings remain.
        if (message.Reason is NotifyReason.Recovered or NotifyReason.Resolved)
        {
            var remaining = message.Context.Where(c => !message.Lines.Contains(c)).ToArray();
            if (remaining.Length > 0)
            {
                body.Add(string.Empty);
                body.Add($"{remaining.Sum(r => r.Count)} lesser finding(s) remain:");
                body.AddRange(remaining.Select(r => Redaction.MaskText(Format(r), redact)));
            }
        }

        return Fit(head, body, tail, limit);
    }

    private static string Head(PlannedNotification message, RunSummary run)
    {
        var builder = new StringBuilder();
        builder.Append(message.Reason switch
        {
            NotifyReason.NewFailure => "A job started failing.",
            NotifyReason.Changed => "A failing job is now failing differently.",
            NotifyReason.Reminder => "A job is still failing.",
            NotifyReason.Recovered => "A job has recovered.",
            _ => "A job's findings are below the current threshold.",
        });

        builder.Append("\r\n\r\n");
        builder.Append("machine: ").Append(run.Machine).Append("\r\n");
        builder.Append("run:     ").Append(run.RunId).Append("\r\n");

        if (message.FailingSince is { } since)
        {
            builder.Append("failing since: ")
                   .Append(since.ToString("u", CultureInfo.InvariantCulture)).Append("\r\n");
        }

        return builder.ToString();
    }

    private static string Format(DigestLine line)
    {
        var severity = line.Severity.ToString().ToLowerInvariant();
        var count = line.Count > 1
            ? $" (x{line.Count.ToString(CultureInfo.InvariantCulture)})"
            : string.Empty;
        var where = line.Where.Length > 0 ? $"  {line.Where}" : string.Empty;

        return $"{severity,-8} {line.Code}  {line.Text}{count}{where}";
    }

    /// <summary>
    /// Fits head, body and tail into <paramref name="limit"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Whole lines only, and the tail always survives. A message truncated mid-line reads as
    /// corruption, and one that loses its tail loses the instruction for finding the rest.
    /// </para>
    /// <para>
    /// The head is never dropped: a message that says only "... 7 more" is worse than useless.
    /// If even the head does not fit, it is hard-cut - the destination would reject the whole
    /// thing otherwise, and a cut head still names the machine.
    /// </para>
    /// </remarks>
    private static string Fit(string head, IReadOnlyList<string> body, string tail, int limit)
    {
        var full = new StringBuilder(head);
        foreach (var line in body)
        {
            full.Append(line).Append("\r\n");
        }

        full.Append("\r\n").Append(tail);

        if (full.Length <= limit)
        {
            return full.ToString();
        }

        for (var keep = body.Count - 1; keep >= 0; keep--)
        {
            var dropped = body.Count - keep;
            var candidate = new StringBuilder(head);
            for (var i = 0; i < keep; i++)
            {
                candidate.Append(body[i]).Append("\r\n");
            }

            candidate.Append("... +").Append(dropped.ToString(CultureInfo.InvariantCulture))
                     .Append(" more\r\n\r\n").Append(tail);

            if (candidate.Length <= limit)
            {
                return candidate.ToString();
            }
        }

        var minimum = head + "\r\n" + tail;
        return minimum.Length <= limit ? minimum : minimum[..limit];
    }
}
