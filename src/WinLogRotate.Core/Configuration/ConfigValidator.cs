using WinLogRotate.Contracts;
using WinLogRotate.Core.Hooks;
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

        // Entries are judged before any pattern consults them, so a job can never be rescued by
        // an override too broad to honour. A refused entry is an Error, and an Error makes the
        // job Invalid - so a job with one good entry and one careless one does not run until the
        // careless one is fixed, and the careless one is named. That is the safe reading: the
        // point of allowdangerous is that somebody looked, and an entry the guard will not honour
        // is evidence that they did not. The good entries are still collected below, so every
        // pattern is judged against the overrides that would apply and reported in one pass
        // rather than one refusal per run. (This comment used to say the refused entry was
        // dropped while the rest kept working. It never was; the Error saw to that.)
        var honoured = new List<string>();
        foreach (var entry in job.AllowDangerous)
        {
            var judged = guard.CheckOverrideEntry(entry);
            if (judged.IsAllowed)
            {
                honoured.Add(entry);
                continue;
            }

            d.Error(file, DiagnosticCode.DangerousPathRefused,
                judged.Message ?? $"'{entry}' is not an acceptable allowdangerous entry.",
                remedy: judged.Remedy);
        }

        var scope = job.GuardScope with { AllowDangerous = honoured };

        foreach (var pattern in job.Paths)
        {
            var decision = guard.CheckPattern(pattern, scope);
            if (!decision.IsAllowed)
            {
                d.Error(file, DiagnosticCode.DangerousPathRefused,
                    decision.Message ?? $"'{pattern}' was refused.", remedy: decision.Remedy);
            }
            else if (decision.Overridden)
            {
                d.Warn(file, DiagnosticCode.DangerousPathRefused,
                    decision.Message ?? $"'{pattern}' is permitted only by an explicit override.",
                    // Not "it is listed in Settings": nothing in the GUI has ever mentioned
                    // AllowDangerous, and a remedy pointing somewhere that does not exist is
                    // worse than none - especially now that this warning is reachable at all.
                    remedy: "Review this override periodically; 'winlogrotate config check' "
                          + "reports every one in force, with the file and line that set it.");
            }
        }

        // Only when it is absolute, and deliberately without asking whether it exists. A relative
        // olddir resolves against each matched log's own directory, so it cannot be known until
        // plan time; and whether a directory exists is a fact about one machine at one moment,
        // while config check is often run on another - the same argument CheckHooks makes below
        // about the hook gate. OldDirGate asks the real question immediately before acting.
        if (job.OldDir is { Length: > 0 } oldDir
            && WinPath.IsAbsolute(WinPath.Normalize(oldDir))
            && guard.CheckPath(WinPath.Normalize(oldDir), job.GuardScope) is { IsAllowed: false } where)
        {
            d.Error(file, DiagnosticCode.DangerousPathRefused,
                where.Message ?? $"olddir '{oldDir}' was refused.", remedy: where.Remedy);
        }

        // olddir is read by ArchiveNaming, which ManageJobPlanner never calls.
        if (job is { Kind: JobKind.Manage, OldDir.Length: > 0 })
        {
            d.Warn(file, DiagnosticCode.ConfigInvalid,
                "olddir is ignored on a manage job: it never moves a log, so there is nothing to "
                + "put anywhere else.");
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

        if (job.DateExt)
        {
            CheckDateFormat(job.DateFormat, file, d);
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

        // RetryPolicy refuses fewer than one attempt with an ArgumentOutOfRangeException, and
        // Thread.Sleep treats -1 as "for ever". Neither is an IOException, so neither was caught
        // anywhere: retrycount = 0 ended every run at exit 4 on its first file operation, and
        // retryinterval = -1 hung the first retry until the scheduled task's limit killed it.
        // Both are values somebody plausibly writes to mean "do not retry".
        if (job.RetryCount < 1)
        {
            d.Error(file, DiagnosticCode.ConfigInvalid,
                $"retrycount = {job.RetryCount} is not a number of attempts.",
                remedy: "It counts attempts, not retries: 1 tries once and never retries. The default is 5.");
        }

        if (job.RetryIntervalMs < 0)
        {
            d.Error(file, DiagnosticCode.ConfigInvalid,
                $"retryinterval = {job.RetryIntervalMs} is negative.",
                remedy: "Milliseconds between attempts; 0 retries at once. The default is 100.");
        }

        if (job.MaxFiles < 1)
        {
            d.Error(file, DiagnosticCode.ConfigInvalid,
                $"maxfiles = {job.MaxFiles} would refuse every pattern, because every pattern matches at least one file when it matches at all.",
                remedy: "Set the largest number of files one pattern may legitimately match. The default is 1000.");
        }

        if (job.Start < 0)
        {
            d.Error(file, DiagnosticCode.ConfigInvalid,
                $"start = {job.Start} is negative, and app.log.-1 is not a name this product can find again.",
                remedy: "The index the first archive gets: 1 gives app.log.1, 0 gives app.log.0.");
        }

        CheckHooks(job, HookStage.PreRotate, job.PreRotate, file, d);
        CheckHooks(job, HookStage.PostRotate, job.PostRotate, file, d);

        // A manage job never moves a live log - the application rotates, and this only compresses
        // and retains what it left behind - and hooks run only when a live log actually moves. So
        // a hook here would never fire, ever, and would look exactly like one that does.
        if (job.Kind == JobKind.Manage && (job.PreRotate.Count > 0 || job.PostRotate.Count > 0))
        {
            d.Warn(file, DiagnosticCode.HookRefused,
                "a manage job never rotates anything itself, so its hooks would never run.",
                remedy: "Hooks belong on a kind = \"rotate\" job. If the application's own rotation "
                      + "needs to trigger something, it has to do that itself - this job only "
                      + "compresses and retains what it already wrote.");
        }

        if (job.HookTimeout <= TimeSpan.Zero && (job.PreRotate.Count > 0 || job.PostRotate.Count > 0))
        {
            d.Warn(file, DiagnosticCode.ConfigInvalid,
                "hook_timeout is not positive, so no hook could ever run.",
                remedy: "Give it a duration, e.g. hook_timeout = \"60s\".");
        }
    }

    /// <summary>
    /// Refuses a <c>dateformat</c> the engine could not format, or could not find again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing checked the format at all. One .NET rejects - a stray quote, a trailing
    /// <c>%</c> - threw <c>FormatException</c> out of <c>ArchiveNaming.FirstRotation</c> at plan
    /// time, where no catch filter names it, and ended the whole run at exit 4. One .NET accepts
    /// but this product cannot rediscover was worse, because it was silent: archives are found
    /// again by turning the format into a glob of digit classes and parsing the stamp back, and
    /// both steps assume every specifier renders as digits and every literal is one of the three
    /// separators. <c>-%Y%m%d</c> - what every logrotate user writes first, and what this key's
    /// own doc comment called "strftime-style" - rendered as <c>-Y09m13d</c>-shaped names that
    /// retention never counted or deleted again.
    /// </para>
    /// <para>
    /// The rule is therefore the discovery rule, stated: runs of <c>y M d H m s</c>, separated by
    /// <c>-</c>, <c>_</c> or <c>.</c>, at least two characters long so .NET reads a custom pattern
    /// rather than a standard one, and never a month or weekday spelled as a name.
    /// </para>
    /// </remarks>
    private static void CheckDateFormat(string format, string file, DiagnosticBag d)
    {
        if (DateFormatProblem(format) is { } problem)
        {
            d.Error(file, DiagnosticCode.ConfigInvalid, problem.Message, remedy: problem.Remedy);
        }
    }

    /// <summary>
    /// Why a <c>dateformat</c> could not be found again, in the validator's own words, or null
    /// when it can. Pure, so a form can say it before the file is written.
    /// </summary>
    public static (string Message, string Remedy)? DateFormatProblem(string format)
    {
        if (format.Contains('%'))
        {
            return (
                $"dateformat '{format}' is written in strftime's grammar, which this product does not use.",
                "Write .NET specifiers instead: %Y is yyyy, %m is MM, %d is dd, and %H %M %S "
                + "are HH mm ss - so -%Y%m%d is -yyyyMMdd.");
        }

        if (format.Length < 2)
        {
            return (
                $"dateformat '{format}' is too short: .NET reads a single character as one of its standard "
                + "patterns, which contain separators no file name can carry.",
                "Use a pattern of at least two characters, such as -yyyyMMdd.");
        }

        var stray = format.FirstOrDefault(c => c is not ('y' or 'M' or 'd' or 'H' or 'm' or 's' or '-' or '_' or '.'));
        if (stray != default)
        {
            return (
                $"dateformat '{format}' contains '{stray}', and an archive named with it could not be found again.",
                "Archives are rediscovered by their name, which works for runs of y, M, d, H, m and s "
                + "separated by -, _ or . and for nothing else. Anything else stops them being counted or deleted.");
        }

        if (LongestRun(format, 'M') > 2 || LongestRun(format, 'd') > 2)
        {
            return (
                $"dateformat '{format}' spells a month or a weekday as a name, and a name is not a number an archive can be found by.",
                "MM and dd are the numeric forms; MMM, MMMM, ddd and dddd are names.");
        }

        return null;
    }

    private static int LongestRun(string text, char c)
    {
        var longest = 0;
        var run = 0;

        foreach (var ch in text)
        {
            run = ch == c ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        return longest;
    }

    /// <summary>
    /// Puts a job's hook strings through the same parser that will run them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// So a typo is caught by <c>config check</c> rather than at 03:00. Notification targets have
    /// been validated this way since they existed - <c>ConfigLoader</c> parses every
    /// <c>[notify].to</c> entry and reports what it cannot understand - while the identical string
    /// in <c>postrotate</c> produced no diagnostic anywhere. <c>HookParser</c>'s own doc comment
    /// claims "one parser for hooks and for notification targets alike, so postrotate and [notify]
    /// cannot drift apart in what they accept"; this is the half that makes it true.
    /// </para>
    /// <para>
    /// The gate is deliberately open here. Whether the configuration directory is safe to execute
    /// from is a fact about the machine at the moment of a run, and <c>config check</c> is often
    /// run on a different machine entirely - refusing every hook because the reviewer's laptop has
    /// a loose conf.d would report a security finding about the wrong computer. The runtime asks
    /// the real question, immediately before dispatching.
    /// </para>
    /// <para>
    /// Warnings, not errors, even though the runtime refusal is an error. A config-time error sets
    /// <c>LoadedConfig.HasErrors</c>, and <c>run</c> then attempts nothing at all - so one
    /// mistyped hook in one file would stop every healthy job on the machine from rotating. The
    /// runtime refuses the hook that is actually wrong and leaves the rest alone, which is where
    /// that severity belongs.
    /// </para>
    /// </remarks>
    private static void CheckHooks(
        EffectiveJob job, HookStage stage, IReadOnlyList<string> hooks, string file, DiagnosticBag d)
    {
        if (hooks.Count == 0)
        {
            return;
        }

        // The same decision the runtime makes, so config check cannot pass something run would
        // refuse - or refuse something it would happily run.
        //
        // That was not true while HookPlan probed the disk. Check and run are separate processes
        // at separate times: a file appearing between 17:00 and 03:00 was enough to make them
        // disagree, and the file that made them disagree was the one an attacker would plant.
        // CommandLine.TrySplit takes no probe now, so the agreement is a property of the text.
        var planned = HookPlan.For(job.Name, stage, hooks, HookGate.Open);

        foreach (var refusal in planned.Refusals)
        {
            d.Warn(file, refusal.Code, refusal.Message, remedy: refusal.Remedy);
        }

        // Accepted, and still worth a sentence: an event: target without Global\ is a hook that
        // will run and report "no event named X exists" every night, about an event that does
        // exist on somebody's desktop. Under ConfigInvalid at Warning, beside the hook_timeout
        // check, rather than HookRefused - it is not refused, and docs/hooks.md says LR9003
        // means "the hook will not be run".
        foreach (var hook in planned.Hooks)
        {
            if (HookPlan.SessionLocalEventCaution(hook.Action) is { } caution)
            {
                d.Warn(file, DiagnosticCode.ConfigInvalid, $"the {hook.StageName} hook {caution}",
                    remedy: $"Write event:{HookPlan.GlobalPrefix}{hook.Action.Target.Trim()} here and "
                          + "create the event under that name in the waiting program.");
            }
        }
    }
}
