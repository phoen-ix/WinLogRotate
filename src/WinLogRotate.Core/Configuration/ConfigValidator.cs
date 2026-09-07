using WinLogRotate.Contracts;
using WinLogRotate.Core.Safety;

namespace WinLogRotate.Core.Configuration;

/// <summary>
/// Rules that turn a recurring runtime failure into a single config-time error.
/// </summary>
public static class ConfigValidator
{
    public static void Validate(EffectiveJob job, PathGuard guard, DiagnosticBag d)
    {
        var file = job.SourceFile ?? job.Name;

        foreach (var pattern in job.Paths)
        {
            var decision = guard.CheckPattern(pattern);
            if (!decision.IsAllowed)
            {
                d.Error(file, DiagnosticCode.DangerousPathRefused,
                    decision.Message ?? $"'{pattern}' was refused.", remedy: decision.Remedy);
            }
            else if (decision.Overridden)
            {
                d.Warn(file, DiagnosticCode.DangerousPathRefused,
                    decision.Message ?? $"'{pattern}' is permitted only by an explicit override.",
                    remedy: "Review this override periodically; it is listed in Settings.");
            }
        }

        if (job.Rotate < -1)
        {
            d.Error(file, DiagnosticCode.ConfigInvalid,
                $"rotate = {job.Rotate} is not meaningful.",
                remedy: "Use a count, 0 to discard immediately, or -1 to keep everything and prune by maxage alone.");
        }

        // rotate = -1 removes the only count-based brake, so maxage becomes the sole thing
        // standing between this job and a full disk. Saying so at config time is much cheaper
        // than discovering it at 94% capacity.
        if (job is { Rotate: -1, MaxAge: null })
        {
            d.Warn(file, DiagnosticCode.ConfigInvalid,
                "rotate = -1 keeps every archive forever and no maxage is set, so nothing will ever be deleted.",
                remedy: "Set maxage, or give rotate a count.");
        }

        if (job.LiveFiles < 0)
        {
            d.Error(file, DiagnosticCode.ConfigInvalid, $"livefiles = {job.LiveFiles} is negative.");
        }

        // A manage job that does not skip the newest file will compress the one the producer
        // is actively writing - which is the single thing this job kind exists to avoid.
        if (job is { Kind: JobKind.Manage, LiveFiles: 0 })
        {
            d.Warn(file, DiagnosticCode.ConfigInvalid,
                "livefiles = 0 on a manage job means the file the application is currently writing will be compressed and rotated.",
                remedy: "Leave livefiles at 1 unless you are certain the producer has finished with every matched file.");
        }

        // dateext refuses to overwrite an existing dated archive, by design. If the date
        // format is coarser than the schedule, the second rotation of every period therefore
        // fails - forever. Catching it here turns a recurring runtime error into one message.
        if (job is { Kind: JobKind.Rotate, DateExt: true })
        {
            var needed = job.Schedule switch
            {
                Schedule.Hourly => "H",
                Schedule.Daily or Schedule.Weekly => "d",
                Schedule.Monthly => "M",
                Schedule.Yearly => "y",
                _ => null,
            };

            if (needed is not null && !job.DateFormat.Contains(needed, StringComparison.Ordinal))
            {
                d.Error(file, DiagnosticCode.ConfigInvalid,
                    $"dateformat '{job.DateFormat}' is coarser than the {job.Schedule.ToString().ToLowerInvariant()} schedule, so two rotations in one period would produce the same filename.",
                    remedy: $"Include '{needed}' in dateformat, or rotate less often. dateext never overwrites an existing archive, so this would fail on every second rotation.");
            }
        }

        if (job is { DelayCompress: true, Compress: false })
        {
            d.Warn(file, DiagnosticCode.ConfigInvalid,
                "delaycompress does nothing without compress.");
        }

        if (job is { Kind: JobKind.Manage, LockStrategy: not LockStrategy.Rename })
        {
            d.Warn(file, DiagnosticCode.ConfigInvalid,
                "lockstrategy is ignored on a manage job: it never touches the file the application is writing.");
        }

        if (job.Weekday is < 0 or > 7)
        {
            d.Error(file, DiagnosticCode.ConfigInvalid,
                $"weekday = {job.Weekday} is out of range.",
                remedy: "0 is Sunday through 6 Saturday; 7 means every seven days regardless of weekday.");
        }

        if (job.MonthDay is < 0 or > 31)
        {
            d.Error(file, DiagnosticCode.ConfigInvalid, $"monthday = {job.MonthDay} is out of range.");
        }

        if (job.MinSize is not null && job.MaxSize is not null && job.MinSize > job.MaxSize)
        {
            d.Error(file, DiagnosticCode.ConfigInvalid,
                $"minsize ({job.MinSize}) is larger than maxsize ({job.MaxSize}), so this job can never rotate.",
                remedy: "minsize suppresses a due rotation; maxsize forces an early one. Setting minsize above maxsize cancels both.");
        }
    }
}
