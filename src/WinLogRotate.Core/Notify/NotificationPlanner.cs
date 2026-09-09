using WinLogRotate.Contracts;
using WinLogRotate.Core.State;

namespace WinLogRotate.Core.Notify;

/// <summary>
/// Decides what, if anything, is worth telling somebody about a run.
/// </summary>
/// <remarks>
/// <para>
/// Split from delivery exactly as <see cref="Journaling.JournalMaintenance.PlanFor"/> is split
/// from its <c>Run</c>: the decision is a pure function of its arguments, so the threshold, the
/// state machine, the aggregation and the wording are all testable on the Linux CI leg, and only
/// delivery needs a network or a Windows box.
/// </para>
/// <para>
/// It is also what makes <c>--dry-run</c> honest. The plan is produced identically whether or not
/// anything will be sent, so "would notify ..." describes the message that would really have gone
/// out, rather than a separate description that drifts from it.
/// </para>
/// </remarks>
public static class NotificationPlanner
{
    public static NotificationPlan PlanFor(
        RunSummary run,
        IReadOnlyList<CliDiagnostic> diagnostics,
        NotifySettings settings,
        NotifyStateStore state,
        DateTimeOffset now)
    {
        if (!settings.Enabled)
        {
            return NotificationPlan.Nothing("notifications are disabled");
        }

        if (settings.On == NotifyOn.Never)
        {
            return NotificationPlan.Nothing("on = \"never\"");
        }

        // Every group found, regardless of the threshold. The threshold decides what is worth
        // waking somebody for; the body of a recovery still has to be able to say what remains.
        var all = Aggregate(diagnostics);

        var jobs = new HashSet<string>(run.ObservedJobs, StringComparer.OrdinalIgnoreCase);
        foreach (var line in all)
        {
            jobs.Add(line.Job);
        }

        var messages = new List<PlannedNotification>();
        var suppressed = new List<string>();
        var baseline = new List<(string, JobNotifyState)>();

        foreach (var job in jobs.OrderBy(j => j, StringComparer.Ordinal))
        {
            var lines = all.Where(l => Same(l.Job, job)).ToArray();
            var above = lines.Where(l => l.Severity >= settings.Threshold).ToArray();

            var prior = state.JobOrDefault(job);
            var failing = above.Length > 0;
            var fingerprint = failing ? Fingerprint(above) : string.Empty;

            DateTimeOffset? failingSince = failing ? prior.FailingSince ?? now : null;

            var next = new JobNotifyState
            {
                Outcome = failing ? NotifyOutcome.Failing : NotifyOutcome.Healthy,
                Fingerprint = fingerprint,
                NotifiedAt = now,
                NotifiedThreshold = settings.Threshold.ToString(),
                FailingSince = failingSince,
                LastSeen = now,
            };

            var decision = Decide(prior, failing, fingerprint, settings, state.AlgorithmChanged, now);

            switch (decision.Kind)
            {
                case Decision.Send:
                    messages.Add(Compose(job, decision.Reason, above, lines, fingerprint, failingSince, next, run));
                    break;

                case Decision.Baseline:
                    // Recorded, not sent. Installing this on a machine that is already broken
                    // must not page anybody about problems that predate the install.
                    baseline.Add((job, next with { NotifiedAt = null, NotifiedThreshold = null }));
                    suppressed.Add($"{Describe(job)}: first run, recorded as a baseline");
                    break;

                default:
                    // Still worth keeping LastSeen fresh so pruning does not forget a job that
                    // is simply healthy.
                    baseline.Add((job, prior with { LastSeen = now }));
                    if (decision.Why is { } why)
                    {
                        suppressed.Add($"{Describe(job)}: {why}");
                    }

                    break;
            }
        }

        return new NotificationPlan
        {
            Messages = messages,
            Suppressed = suppressed,
            Baseline = baseline,
        };
    }

    private enum Decision { Send, Silent, Baseline }

    private readonly record struct Verdict(Decision Kind, NotifyReason Reason, string? Why);

    /// <summary>
    /// The state machine, one job at a time.
    /// </summary>
    /// <remarks>
    /// Per job rather than per run, because every Windows estate has exactly one permanently
    /// broken job - the log4net file opened with ExclusiveLock that nobody will ever fix. With a
    /// run-level bit that job pins the run at Failing for ever, so Failing-to-Healthy never
    /// happens again and recovery notification silently leaves the product.
    /// </remarks>
    private static Verdict Decide(
        JobNotifyState prior, bool failing, string fingerprint,
        NotifySettings settings, bool algorithmChanged, DateTimeOffset now)
    {
        if (settings.On == NotifyOn.Every)
        {
            return failing
                ? new Verdict(Decision.Send, NotifyReason.NewFailure, null)
                : new Verdict(Decision.Silent, default, null);
        }

        switch (prior.Outcome)
        {
            case NotifyOutcome.Unknown:
                // Never reported on. A failure is recorded rather than sent, so a fresh install
                // is quiet; a healthy job is simply recorded.
                return new Verdict(Decision.Baseline, default, null);

            case NotifyOutcome.Healthy when failing:
                return new Verdict(Decision.Send, NotifyReason.NewFailure, null);

            case NotifyOutcome.Healthy:
                return new Verdict(Decision.Silent, default, null);

            case NotifyOutcome.Failing when !failing:
                // A raised threshold is not the same news as a fixed problem, and saying
                // "recovered" when the failure is still there would be a lie about the world.
                return string.Equals(prior.NotifiedThreshold, settings.Threshold.ToString(), StringComparison.Ordinal)
                    ? new Verdict(Decision.Send, NotifyReason.Recovered, null)
                    : new Verdict(Decision.Send, NotifyReason.Resolved, null);

            case NotifyOutcome.Failing when algorithmChanged:
                // Adopt the new fingerprint without comment. Otherwise the first release that
                // changes how a failure is identified reports every job as newly broken on the
                // night of the upgrade.
                return new Verdict(Decision.Baseline, default, "the fingerprint algorithm changed");

            case NotifyOutcome.Failing when !string.Equals(prior.Fingerprint, fingerprint, StringComparison.Ordinal):
                // A different code, errno or directory is a different incident even though the
                // job never went green. Missing it is how "the file is locked" masks "and now
                // the disk is full".
                return new Verdict(Decision.Send, NotifyReason.Changed, null);

            default:
                return Remind(prior, settings, now);
        }
    }

    /// <summary>
    /// Still the same failure. Says so again once it has been quiet long enough.
    /// </summary>
    /// <remarks>
    /// Without this, "only on change" means a permanent failure produces exactly one message
    /// ever - and once somebody deletes that mail the system is silently broken for good.
    /// <para>
    /// A threshold on elapsed time, not a loop advancing the clock by RemindAfter: after thirty
    /// days at seven-day intervals this sends ONE reminder, not four at once.
    /// </para>
    /// </remarks>
    private static Verdict Remind(JobNotifyState prior, NotifySettings settings, DateTimeOffset now)
    {
        if (settings.RemindAfter <= TimeSpan.Zero)
        {
            // Not "remind every run" - that already has a spelling, on = "every", and a mistyped
            // remind_after = "0s" must not silently become per-run spam.
            return new Verdict(Decision.Silent, default, "still failing; reminders are off");
        }

        // Counted from when somebody was last told, or - for a job recorded as a baseline,
        // where nobody ever was - from when it started failing. Treating a null NotifiedAt as
        // "overdue" would make the run straight after a first-run baseline send the reminder the
        // baseline exists to suppress, which is the whole point undone one run later.
        var since = prior.NotifiedAt ?? prior.FailingSince;
        if (since is not { } last)
        {
            return new Verdict(Decision.Silent, default, "still failing; nothing to count from");
        }

        if (now - last >= settings.RemindAfter)
        {
            return new Verdict(Decision.Send, NotifyReason.Reminder, null);
        }

        var due = last + settings.RemindAfter;
        return new Verdict(Decision.Silent, default,
            $"still failing, unchanged since {last:yyyy-MM-dd}; next reminder {due:yyyy-MM-dd}");
    }

    /// <summary>
    /// Folds diagnostics into counted groups.
    /// </summary>
    /// <remarks>
    /// Ordering is explicit rather than left to GroupBy, so two machines given the same input
    /// produce the same message. Severity first because that is what a reader scans for, then
    /// count, then the stable code, then the job and the location - a total order with no ties
    /// that could be broken differently on a different runtime.
    /// </remarks>
    private static DigestLine[] Aggregate(IReadOnlyList<CliDiagnostic> diagnostics)
    {
        var groups = new Dictionary<(string Job, string Code, int Native, string Where), List<CliDiagnostic>>();

        foreach (var d in diagnostics)
        {
            var job = d.Job ?? NotifyStateDocument.RunScope;

            // A run-scoped finding carries the config file's path, so fingerprinting on its
            // directory would make "the configuration is broken" a different problem after
            // somebody moves the config root - or on any --config-dir run.
            var where = job == NotifyStateDocument.RunScope
                ? string.Empty
                : NotifyFingerprint.Canonical(DirectoryOf(d.Path));

            var key = (job, d.Code, d.NativeError ?? 0, where);

            if (!groups.TryGetValue(key, out var list))
            {
                groups[key] = list = [];
            }

            list.Add(d);
        }

        return [.. groups
            .Select(g => new DigestLine
            {
                Severity = g.Value.Max(d => d.Severity),
                Code = g.Key.Code,
                Job = g.Key.Job,
                Text = g.Value[0].Message,
                Where = Render(g.Key.Where, g.Value),
                DirectoryKey = g.Key.Where,
                Count = g.Value.Count,
                NativeError = g.Key.Native,
            })
            .OrderByDescending(l => l.Severity)
            .ThenByDescending(l => l.Count)
            .ThenBy(l => l.Code, StringComparer.Ordinal)
            .ThenBy(l => l.Job, StringComparer.Ordinal)
            .ThenBy(l => l.Where, StringComparer.Ordinal)];
    }

    /// <summary>The single path when there is one, the directory and a wildcard when there are more.</summary>
    private static string Render(string directory, List<CliDiagnostic> members) =>
        members.Count == 1 && members[0].Path is { Length: > 0 } only
            ? only
            : directory.Length == 0 ? string.Empty : directory + "\\*";

    private static string? DirectoryOf(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        // Windows separators handled explicitly: Path.GetDirectoryName does not treat '\' as a
        // separator on Linux, and ~70% of this suite runs there.
        var cut = path.LastIndexOfAny(['\\', '/']);
        return cut <= 0 ? path : path[..cut];
    }

    private static string Fingerprint(IEnumerable<DigestLine> lines) =>
        NotifyFingerprint.For(lines.Select(l => new FingerprintPart(
            l.Code, l.NativeError, l.Severity.ToString(), l.DirectoryKey)));

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string Describe(string job) =>
        job == NotifyStateDocument.RunScope ? "the run" : $"[{job}]";

    private static PlannedNotification Compose(
        string job, NotifyReason reason, DigestLine[] above, DigestLine[] all,
        string fingerprint, DateTimeOffset? failingSince, JobNotifyState next, RunSummary run) =>
        new()
        {
            Reason = reason,
            Job = job,
            Severity = above.Length > 0 ? above.Max(l => l.Severity) : Severity.Info,
            Subject = MessageComposer.Subject(reason, job, above, run),
            Lines = above,
            Context = all,
            Fingerprint = fingerprint,
            FailingSince = failingSince,
            NextState = next,
        };
}

/// <summary>
/// The facts about a run that a message needs, without dragging the whole report in.
/// </summary>
/// <remarks>
/// A parameter rather than a static read, so the composer stays pure: the machine name appears in
/// every subject line, and reading it from the environment would make output depend on where the
/// test ran.
/// </remarks>
public sealed record RunSummary
{
    public required string RunId { get; init; }
    public required string Machine { get; init; }
    public required int JobsRun { get; init; }
    public required int Completed { get; init; }
    public required long BytesFreed { get; init; }

    /// <summary>
    /// Jobs this run actually looked at.
    /// </summary>
    /// <remarks>
    /// Needed so that a job which did not run - disabled, filtered out by --job, or removed from
    /// the configuration - is left alone rather than read as healthy. Reading absence as health
    /// mails everybody "recovered" the moment somebody disables a job.
    /// </remarks>
    public required IReadOnlyList<string> ObservedJobs { get; init; }
}
