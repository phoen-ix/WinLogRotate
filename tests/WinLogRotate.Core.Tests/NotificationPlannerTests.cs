using Shouldly;
using WinLogRotate.Contracts;
using WinLogRotate.Core.Notify;
using WinLogRotate.Core.State;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// What is worth telling somebody, and when. All pure, all on Linux.
/// </summary>
public sealed class NotificationPlannerTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-plan-");
    private readonly DateTimeOffset _now = new(2026, 9, 9, 3, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    private NotifyStateStore State() =>
        NotifyStateStore.Load(Path.Combine(_dir.FullName, "notify.json"));

    private static RunSummary Run(params string[] jobs) => new()
    {
        RunId = "01JQTEST",
        Machine = "WEB01",
        JobsRun = jobs.Length,
        Completed = 0,
        BytesFreed = 0,
        ObservedJobs = jobs,
    };

    private static CliDiagnostic Bad(
        string job = "iis", string code = DiagnosticCode.FileLocked,
        Severity severity = Severity.Error, string path = @"C:\logs\a.log", int native = 32) => new()
        {
            Severity = severity,
            Code = code,
            Message = "another process has it open",
            Path = path,
            Job = job,
            NativeError = native,
        };

    private static NotifySettings Settings(
        NotifyOn on = NotifyOn.Change, Severity threshold = Severity.Warning, int remindDays = 7) => new()
        {
            On = on,
            Threshold = threshold,
            RemindAfter = TimeSpan.FromDays(remindDays),
        };

    private NotificationPlan Plan(
        IReadOnlyList<CliDiagnostic> diagnostics, NotifyStateStore state,
        NotifySettings? settings = null, DateTimeOffset? at = null, params string[] jobs) =>
        NotificationPlanner.PlanFor(
            Run(jobs.Length == 0 ? ["iis"] : jobs), diagnostics,
            settings ?? Settings(), state, at ?? _now);

    private NotificationPlan PlanMuting(
        IReadOnlyList<CliDiagnostic> diagnostics, NotifyStateStore state,
        string[] muted, DateTimeOffset? at = null, string[]? jobs = null) =>
        NotificationPlanner.PlanFor(
            Run(jobs ?? ["iis"]) with { MutedJobs = muted }, diagnostics,
            Settings(), state, at ?? _now);

    // ---- the run scope ----------------------------------------------------------------------

    /// <summary>
    /// A clean run records the run scope healthy, even though there is nothing to report.
    /// </summary>
    /// <remarks>
    /// Every other job earns its place by being observed. Nothing observes the run scope, so
    /// before this it entered the loop only when it already had a finding - which meant its
    /// recorded outcome stayed Unknown for ever on a healthy machine, and the two tests below
    /// were both broken.
    /// </remarks>
    [Fact]
    public void ACleanRunRecordsTheRunScopeAsHealthy()
    {
        var state = State();

        Plan([], state).Baseline.ShouldContain(b => b.Job == NotifyStateDocument.RunScope);
    }

    /// <summary>
    /// The FIRST configuration or security finding on a machine is reported that night.
    /// </summary>
    /// <remarks>
    /// It used to be recorded as a first-run baseline and not sent, surfacing only when
    /// remind_after elapsed - seven days late by default, for the band that carries "your
    /// configuration directory is writable by every local user".
    /// </remarks>
    [Fact]
    public void TheFirstRunScopedFindingIsSentRatherThanBaselined()
    {
        var state = State();

        // One healthy run, as any working machine has before something breaks.
        foreach (var (job, next) in Plan([], state).Baseline)
        {
            state.SetJob(job, next);
        }

        var insecure = Bad(job: null!, code: DiagnosticCode.ConfigDirectoryInsecure,
            severity: Severity.Critical, path: @"C:\ProgramData\WinLogRotate\conf.d", native: 0);

        var message = Plan([insecure], state, at: _now.AddDays(1))
            .Messages.ShouldHaveSingleItem();

        message.Job.ShouldBe(NotifyStateDocument.RunScope);
        message.Reason.ShouldBe(NotifyReason.NewFailure);
    }

    /// <summary>
    /// And its recovery is reported too, which needs the scope present when it has no findings.
    /// </summary>
    /// <remarks>
    /// This is the sharper half of the same bug: with the finding gone there are no run-scoped
    /// diagnostics at all, so nothing put the scope back into the set to notice it had gone
    /// green. "I fixed the ACL and never got the all-clear" is indistinguishable from "the
    /// alerting is broken".
    /// </remarks>
    [Fact]
    public void ARunScopedFindingThatIsFixedReportsItsRecovery()
    {
        var state = State();
        state.SetJob(NotifyStateDocument.RunScope, Failing(_now.AddDays(-2), "whatever"));

        var message = Plan([], state).Messages.ShouldHaveSingleItem();

        message.Job.ShouldBe(NotifyStateDocument.RunScope);
        message.Reason.ShouldBe(NotifyReason.Recovered);
    }

    /// <summary>A machine already broken on its first run is still baselined and still silent.</summary>
    [Fact]
    public void AMachineBrokenOnTheVeryFirstRunIsStillQuiet()
    {
        // The property lives in Decide, not in the job set, and always evaluating the run scope
        // must not have moved it.
        var insecure = Bad(job: null!, code: DiagnosticCode.ConfigDirectoryInsecure,
            severity: Severity.Critical, path: @"C:\conf.d", native: 0);

        Plan([insecure], State()).Messages.ShouldBeEmpty();
    }

    // ---- the truth table -------------------------------------------------------------------

    [Fact]
    public void AFirstRunRecordsABaselineAndSendsNothing()
    {
        // Installing this on a machine that is already broken must not page anybody about
        // problems that predate the install. That is how a source gets muted in week one.
        var plan = Plan([Bad()], State());

        plan.IsEmpty.ShouldBeTrue();
        plan.Baseline.ShouldContain(b => b.Job == "iis");
        plan.Suppressed.ShouldContain(s => s.Contains("baseline", StringComparison.Ordinal));
    }

    [Fact]
    public void AHealthyJobThatStartsFailingIsReported()
    {
        var state = State();
        state.SetJob("iis", new JobNotifyState { Outcome = NotifyOutcome.Healthy, NotifiedAt = _now.AddDays(-1) });

        var message = Plan([Bad()], state).Messages.ShouldHaveSingleItem();

        message.Reason.ShouldBe(NotifyReason.NewFailure);
        message.Job.ShouldBe("iis");
    }

    [Fact]
    public void AHealthyJobThatStaysHealthyIsSilent()
    {
        var state = State();
        state.SetJob("iis", new JobNotifyState { Outcome = NotifyOutcome.Healthy, NotifiedAt = _now.AddDays(-1) });

        Plan([], state).IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void AFailingJobThatRecoversIsReported()
    {
        var state = State();
        state.SetJob("iis", Failing(_now.AddDays(-1), "abc"));

        Plan([], state).Messages.ShouldHaveSingleItem().Reason.ShouldBe(NotifyReason.Recovered);
    }

    [Fact]
    public void TheSameFailureAgainIsSilent()
    {
        // What on = "change" buys. Without it a permanently locked file mails every night until
        // somebody writes a rule for it - and then the next, different failure is filed unread.
        var state = State();
        var first = Plan([Bad()], BaselinedState()).Messages.ShouldHaveSingleItem();
        state.SetJob("iis", first.NextState);

        var again = Plan([Bad()], state, at: _now.AddDays(1));

        again.IsEmpty.ShouldBeTrue();
        again.Suppressed.ShouldContain(s => s.Contains("still failing", StringComparison.Ordinal));
    }

    [Fact]
    public void ADifferentFailureWhileStillFailingIsReported()
    {
        // How "the file is locked" stops masking "and now the disk is full".
        var state = State();
        var first = Plan([Bad()], BaselinedState()).Messages.ShouldHaveSingleItem();
        state.SetJob("iis", first.NextState);

        var different = Bad(code: DiagnosticCode.RotationFailed, native: 112);
        Plan([different], state, at: _now.AddDays(1))
            .Messages.ShouldHaveSingleItem().Reason.ShouldBe(NotifyReason.Changed);
    }

    [Fact]
    public void AJobThatDidNotRunIsLeftAlone()
    {
        // "Absent" is not "healthy". Reading it as healthy mails everyone "recovered" the moment
        // somebody disables a job.
        var state = State();
        state.SetJob("iis", Failing(_now.AddDays(-1), "abc"));

        var plan = Plan([], state, jobs: ["other"]);

        plan.Messages.ShouldNotContain(m => m.Job == "iis");
    }

    [Fact]
    public void RaisingTheThresholdResolvesRatherThanRecovers()
    {
        // Saying "recovered" when the failure is still there would be a lie about the world;
        // leaving the outstanding message unclosed trains the reader to ignore the next one.
        var state = State();
        state.SetJob("iis", Failing(_now.AddDays(-1), "abc") with { NotifiedThreshold = "Warning" });

        var message = Plan([Bad(severity: Severity.Warning)], state, Settings(threshold: Severity.Error))
            .Messages.ShouldHaveSingleItem();

        message.Reason.ShouldBe(NotifyReason.Resolved);
    }

    [Fact]
    public void AJobBaselinedWhileFailingReportsRecoveredNotResolved()
    {
        // A baseline records the outcome but tells nobody, so there is no recorded threshold.
        // Reading that absence as "the threshold changed" made every such job report RESOLVED
        // when it recovered - telling the operator they had raised a threshold they never
        // touched. Only visible by baselining, failing, then fixing it.
        var state = State();
        foreach (var (job, next) in Plan([Bad()], state).Baseline)
        {
            state.SetJob(job, next);
        }

        var message = Plan([], state, at: _now.AddDays(1)).Messages.ShouldHaveSingleItem();

        message.Reason.ShouldBe(NotifyReason.Recovered);
    }

    // ---- the reminder ------------------------------------------------------------------------

    [Fact]
    public void AContinuingFailureRemindsAfterTheInterval()
    {
        // Without a reminder, "only on change" means one message ever - and once somebody
        // deletes that mail the system is silently broken for good.
        var state = State();
        state.SetJob("iis", Failing(_now.AddDays(-8), FingerprintOf(Bad())));

        Plan([Bad()], state).Messages.ShouldHaveSingleItem().Reason.ShouldBe(NotifyReason.Reminder);
    }

    [Fact]
    public void ItDoesNotRemindEarly()
    {
        var state = State();
        state.SetJob("iis", Failing(_now.AddDays(-6), FingerprintOf(Bad())));

        var plan = Plan([Bad()], state);

        plan.IsEmpty.ShouldBeTrue();
        plan.Suppressed.ShouldContain(s => s.Contains("next reminder", StringComparison.Ordinal));
    }

    [Fact]
    public void ThirtyDaysOfDowntimeProducesOneReminderNotFour()
    {
        // A threshold on elapsed time, not a loop advancing the clock - which is the
        // implementation people actually write, and it produces a four-message storm at the
        // worst possible moment.
        var state = State();
        state.SetJob("iis", Failing(_now.AddDays(-30), FingerprintOf(Bad())));

        Plan([Bad()], state).Messages.Count.ShouldBe(1);
    }

    [Fact]
    public void RemindAfterZeroMeansNeverNotEveryRun()
    {
        // "Every run" already has a spelling. A mistyped remind_after = "0s" must not silently
        // become per-run spam.
        var state = State();
        state.SetJob("iis", Failing(_now.AddDays(-99), FingerprintOf(Bad())));

        Plan([Bad()], state, Settings(remindDays: 0)).IsEmpty.ShouldBeTrue();
    }

    // ---- aggregation ---------------------------------------------------------------------------

    [Fact]
    public void FourHundredFailingFilesBecomeOneLine()
    {
        // The unit of notification is the run, not the file. A message with 412 lines is a
        // message nobody reads twice.
        var many = Enumerable.Range(0, 412)
            .Select(i => Bad(path: $@"C:\logs\app-{i}.log"))
            .ToArray();

        var message = Plan(many, BaselinedState()).Messages.ShouldHaveSingleItem();

        var line = message.Lines.ShouldHaveSingleItem();
        line.Count.ShouldBe(412);
        line.Where.ShouldBe(@"C:\LOGS\*");
    }

    [Fact]
    public void ASingleFailureNamesItsFile()
    {
        Plan([Bad()], BaselinedState()).Messages.ShouldHaveSingleItem()
            .Lines.ShouldHaveSingleItem().Where.ShouldBe(@"C:\logs\a.log");
    }

    [Fact]
    public void OutputIsIdenticalWhateverOrderTheDiagnosticsArriveIn()
    {
        // Two machines given the same input must produce the same message, so nothing may depend
        // on GroupBy or dictionary enumeration order.
        CliDiagnostic[] items =
        [
            Bad(code: DiagnosticCode.FileLocked, path: @"C:\a\1.log"),
            Bad(code: DiagnosticCode.RotationFailed, path: @"C:\b\2.log", native: 5),
            Bad(code: DiagnosticCode.FileMissing, severity: Severity.Warning, path: @"C:\c\3.log", native: 2),
        ];

        var forward = Plan(items, BaselinedState()).Messages.ShouldHaveSingleItem();
        var reversed = Plan([.. items.Reverse()], BaselinedState()).Messages.ShouldHaveSingleItem();

        reversed.Lines.Select(l => l.Code).ShouldBe(forward.Lines.Select(l => l.Code));
        reversed.Fingerprint.ShouldBe(forward.Fingerprint);
    }

    [Fact]
    public void ADiagnosticWithNoJobBelongsToTheRun()
    {
        // Config errors, an insecure conf.d, an unreadable secret store - the ones an operator
        // most wants mailed, and none of them belong to a job.
        var runScoped = new CliDiagnostic
        {
            Severity = Severity.Critical,
            Code = DiagnosticCode.ConfigDirectoryInsecure,
            Message = "conf.d is writable",
        };

        var plan = Plan([runScoped], BaselinedRunScope());

        plan.Messages.ShouldHaveSingleItem().Job.ShouldBe(NotifyStateDocument.RunScope);
    }

    // ---- the threshold ---------------------------------------------------------------------------

    [Fact]
    public void NothingAtOrAboveTheThresholdSendsNothing()
    {
        Plan([Bad(severity: Severity.Info)], BaselinedState(), Settings(threshold: Severity.Warning))
            .IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void ARecoveryStillDescribesWhatRemains()
    {
        // Triggered by the threshold, described without it - so the reader is not told the
        // machine is clean when three warnings remain.
        var state = State();
        state.SetJob("iis", Failing(_now.AddDays(-1), "abc"));

        var message = Plan([Bad(severity: Severity.Info)], state, Settings(threshold: Severity.Warning))
            .Messages.ShouldHaveSingleItem();

        message.Reason.ShouldBe(NotifyReason.Recovered);
        message.Lines.ShouldBeEmpty();
        message.Context.ShouldNotBeEmpty();
    }

    // ---- on = every ---------------------------------------------------------------------------

    [Fact]
    public void OnEverySendsWithoutCaringAboutChange()
    {
        var state = State();
        state.SetJob("iis", Failing(_now.AddDays(-1), FingerprintOf(Bad())));

        Plan([Bad()], state, Settings(on: NotifyOn.Every)).Messages.ShouldHaveSingleItem();
    }

    [Fact]
    public void OnNeverSendsNothingAtAll()
    {
        Plan([Bad()], BaselinedState(), Settings(on: NotifyOn.Never)).IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void DisabledSendsNothingAtAll()
    {
        NotificationPlanner.PlanFor(
            Run("iis"), [Bad()], new NotifySettings { Enabled = false }, State(), _now)
            .IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void ABaselinedFailureDoesNotRemindOnTheVeryNextRun()
    {
        // The baseline records that a job is failing but tells nobody, so there is no "last
        // notified" time. Reading that absence as "overdue" made the run straight after a first
        // run send the reminder the baseline exists to suppress - the whole point undone one run
        // later, and only visible by running it twice.
        var state = State();
        foreach (var (job, next) in Plan([Bad()], state).Baseline)
        {
            state.SetJob(job, next);
        }

        var second = Plan([Bad()], state, at: _now.AddMinutes(5));

        second.IsEmpty.ShouldBeTrue();
        second.Suppressed.ShouldContain(s => s.Contains("next reminder", StringComparison.Ordinal));
    }

    [Fact]
    public void ABaselinedFailureStillRemindsEventually()
    {
        // Silent is not the same as forgotten. The clock runs from when it started failing.
        var state = State();
        foreach (var (job, next) in Plan([Bad()], state).Baseline)
        {
            state.SetJob(job, next);
        }

        Plan([Bad()], state, at: _now.AddDays(8))
            .Messages.ShouldHaveSingleItem().Reason.ShouldBe(NotifyReason.Reminder);
    }

    [Fact]
    public void ARunScopedFindingDoesNotFingerprintOnWhereTheConfigLives()
    {
        // Config diagnostics carry the config file's path. Fingerprinting its directory would
        // make "the configuration is broken" a different problem after somebody moves the
        // config root, or on any --config-dir run - so every such run reports it as new.
        CliDiagnostic At(string path) => new()
        {
            Severity = Severity.Error,
            Code = DiagnosticCode.ConfigInvalid,
            Message = "no [job] table",
            Path = path,
        };

        var here = Plan([At(@"C:\ProgramData\WinLogRotate\conf.d\a.toml")], BaselinedRunScope());
        var there = Plan([At(@"D:\elsewhere\conf.d\a.toml")], BaselinedRunScope());

        there.Messages.ShouldHaveSingleItem().Fingerprint
            .ShouldBe(here.Messages.ShouldHaveSingleItem().Fingerprint);
    }

    [Fact]
    public void TwoSpellingsOfOneDirectoryAreOneProblem()
    {
        // Delegating to WinPath.CanonicalKey rather than hand-rolling it: a local version missed
        // repeated separators, so C:\logs\\app and C:\logs\app fingerprinted differently.
        var a = Plan([Bad(path: @"C:\logs\app\1.log"), Bad(path: @"C:\logs\app\2.log")], BaselinedState());
        var b = Plan([Bad(path: @"C:\logs\\app\1.log"), Bad(path: @"C:/logs/app/2.log")], BaselinedState());

        b.Messages.ShouldHaveSingleItem().Fingerprint
            .ShouldBe(a.Messages.ShouldHaveSingleItem().Fingerprint);
    }

    [Fact]
    public void RewordingAMessageDoesNotMakeItANewProblem()
    {
        // Constraint six. A prose-derived fingerprint reports every ongoing failure as newly
        // broken the night of an upgrade, and then newly recovered - two pages per incident, for
        // a wording change.
        var original = Bad();
        var reworded = original with { Message = "the file is held open by another process" };

        Plan([reworded], BaselinedState()).Messages.ShouldHaveSingleItem().Fingerprint
            .ShouldBe(Plan([original], BaselinedState()).Messages.ShouldHaveSingleItem().Fingerprint);
    }

    [Fact]
    public void OneMoreFailingFileIsNotANewProblem()
    {
        // 412 locked files today and 413 tomorrow is one incident. Including the count in the
        // fingerprint turns on = "change" into on = "every" wearing a disguise.
        var today = Enumerable.Range(0, 412).Select(i => Bad(path: $@"C:\logs\a{i}.log")).ToArray();
        var tomorrow = Enumerable.Range(0, 413).Select(i => Bad(path: $@"C:\logs\a{i}.log")).ToArray();

        Plan(tomorrow, BaselinedState()).Messages.ShouldHaveSingleItem().Fingerprint
            .ShouldBe(Plan(today, BaselinedState()).Messages.ShouldHaveSingleItem().Fingerprint);
    }

    [Fact]
    public void TomorrowsIisLogIsNotANewProblem()
    {
        // u_ex260909.log becomes u_ex260910.log at midnight. Fingerprinting the example filename
        // would produce a guaranteed daily false change on the most common Windows log job.
        Plan([Bad(path: @"C:\inetpub\logs\W3SVC1\u_ex260910.log")], BaselinedState())
            .Messages.ShouldHaveSingleItem().Fingerprint
            .ShouldBe(Plan([Bad(path: @"C:\inetpub\logs\W3SVC1\u_ex260909.log")], BaselinedState())
                .Messages.ShouldHaveSingleItem().Fingerprint);
    }

    // ---- the opt-out ---------------------------------------------------------------------------

    [Fact]
    public void MutingAFailingJobDoesNotMailARecovery()
    {
        // THE trap. Filtering only the diagnostics leaves the job in ObservedJobs with nothing
        // above the threshold, which reads as healthy - so muting a broken job mails "RECOVERED"
        // about it. That is the same lie ObservedJobs exists to prevent, arriving by the other
        // door, and it is one .Where() away.
        var state = State();
        state.SetJob("iis", Failing(_now.AddDays(-1), "abc"));

        var plan = PlanMuting([Bad()], state, muted: ["iis"]);

        plan.Messages.ShouldNotContain(m => m.Reason == NotifyReason.Recovered);
        plan.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void MutingActuallySilencesTheJob()
    {
        // The other half-measure: filtering only ObservedJobs lets the diagnostics loop add the
        // job straight back, and the opt-out silently does nothing at all.
        var state = State();
        state.SetJob("iis", new JobNotifyState { Outcome = NotifyOutcome.Healthy, NotifiedAt = _now.AddDays(-1) });

        PlanMuting([Bad()], state, muted: ["iis"]).IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void MutingCannotSilenceARunScopedFinding()
    {
        // "*" is deliberately in the muted set. A per-job opt-out must never be able to turn off
        // "your configuration directory is writable by anyone" - and after Aggregate replaces a
        // null job with the run scope, a job named "*" would be indistinguishable from one.
        var insecure = new CliDiagnostic
        {
            Severity = Severity.Critical,
            Code = DiagnosticCode.ConfigDirectoryInsecure,
            Message = "conf.d is writable",
        };

        var plan = PlanMuting([insecure, Bad()], BaselinedRunScope(), muted: ["iis", "*"]);

        var message = plan.Messages.ShouldHaveSingleItem();
        message.Job.ShouldBe(NotifyStateDocument.RunScope);
        message.Lines.ShouldContain(l => l.Code == DiagnosticCode.ConfigDirectoryInsecure);
    }

    [Fact]
    public void MutingIsCaseInsensitive()
    {
        // Job names are compared that way everywhere else. A caller passing an ordinal set would
        // produce a partial mute that nothing detects, so the planner re-wraps it.
        var state = State();
        state.SetJob("iis", Failing(_now.AddDays(-1), "abc"));

        PlanMuting([Bad(job: "iis")], state, muted: ["IIS"]).IsEmpty.ShouldBeTrue();
    }

    [Theory]
    [InlineData(NotifyOutcome.Unknown, true)]
    [InlineData(NotifyOutcome.Unknown, false)]
    [InlineData(NotifyOutcome.Healthy, true)]
    [InlineData(NotifyOutcome.Healthy, false)]
    [InlineData(NotifyOutcome.Failing, true)]
    [InlineData(NotifyOutcome.Failing, false)]
    public void AMutedJobIsNeverReportedFromAnyPriorState(NotifyOutcome prior, bool failingNow)
    {
        // Proves no send path survives the guard, from every cell of the table.
        var state = State();
        if (prior != NotifyOutcome.Unknown)
        {
            state.SetJob("iis", prior == NotifyOutcome.Failing
                ? Failing(_now.AddDays(-40), "abc")
                : new JobNotifyState { Outcome = prior, NotifiedAt = _now.AddDays(-40) });
        }

        var plan = PlanMuting(failingNow ? [Bad()] : [], state, muted: ["iis"]);

        plan.Messages.ShouldNotContain(m => m.Job == "iis");
    }

    [Fact]
    public void MutingLeavesTheReportedStateExactlyAsItWas()
    {
        // Muting is a configuration edit, not a delivery event, so it must not move the side of
        // state that records what somebody was told. Only LastSeen may change.
        var state = State();
        var before = Failing(_now.AddDays(-5), "abc");
        state.SetJob("iis", before);

        // Named rather than "the single item": the run scope is always recorded too, and this
        // test is about what muting does to the job.
        var recorded = PlanMuting([Bad()], state, muted: ["iis"], at: _now)
            .Baseline.Single(b => b.Job == "iis");

        recorded.State.Outcome.ShouldBe(before.Outcome);
        recorded.State.Fingerprint.ShouldBe(before.Fingerprint);
        recorded.State.NotifiedAt.ShouldBe(before.NotifiedAt);
        recorded.State.FailingSince.ShouldBe(before.FailingSince);
        recorded.State.LastSeen.ShouldBe(_now);
    }

    [Fact]
    public void MutingAJobWithNoHistoryWritesNothing()
    {
        // Otherwise a machine in opt-in mode with 200 jobs grows 200 useless entries per run.
        // The run scope is exempt and always recorded - that is what makes the first genuine
        // configuration finding a new failure rather than a baseline.
        PlanMuting([Bad()], State(), muted: ["iis"]).Baseline
            .ShouldNotContain(b => b.Job == "iis");
    }

    [Fact]
    public void UnmutingAJobThatIsStillBrokenSaysSoAtOnce()
    {
        // The day somebody turns it back on, and the reason state is frozen rather than cleared.
        // Cleared, this run would baseline and stay silent for another week.
        var state = State();
        state.SetJob("iis", Failing(_now.AddDays(-30), FingerprintOf(Bad())));

        var message = Plan([Bad()], state).Messages.ShouldHaveSingleItem();

        message.Reason.ShouldBe(NotifyReason.Reminder);
        message.FailingSince.ShouldBe(_now.AddDays(-30));
    }

    [Fact]
    public void UnmutingAJobThatWasFixedClosesTheOutstandingFailure()
    {
        var state = State();
        state.SetJob("iis", Failing(_now.AddDays(-30), "abc"));

        Plan([], state).Messages.ShouldHaveSingleItem().Reason.ShouldBe(NotifyReason.Recovered);
    }

    [Fact]
    public void AMutedJobSaysWhyInTheSuppressionTrace()
    {
        // The trace is the answer to "why did I not get an email?", which is most of what makes
        // a notification feature trustworthy.
        var state = State();
        state.SetJob("iis", Failing(_now.AddDays(-1), "abc"));

        PlanMuting([Bad()], state, muted: ["iis"])
            .Suppressed.ShouldContain(x => x.Contains("notify = false", StringComparison.Ordinal));
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static JobNotifyState Failing(DateTimeOffset when, string fingerprint) => new()
    {
        Outcome = NotifyOutcome.Failing,
        Fingerprint = fingerprint,
        NotifiedAt = when,
        NotifiedThreshold = nameof(Severity.Warning),
        FailingSince = when,
        LastSeen = when,
    };

    /// <summary>A store in which "iis" has already been reported healthy, so the next failure sends.</summary>
    private NotifyStateStore BaselinedState()
    {
        var state = State();
        state.SetJob("iis", new JobNotifyState
        {
            Outcome = NotifyOutcome.Healthy,
            NotifiedAt = _now.AddDays(-1),
            NotifiedThreshold = nameof(Severity.Warning),
        });
        return state;
    }

    private NotifyStateStore BaselinedRunScope()
    {
        var state = State();
        state.SetJob(NotifyStateDocument.RunScope, new JobNotifyState
        {
            Outcome = NotifyOutcome.Healthy,
            NotifiedAt = _now.AddDays(-1),
            NotifiedThreshold = nameof(Severity.Warning),
        });
        return state;
    }

    private string FingerprintOf(CliDiagnostic d) =>
        Plan([d], BaselinedState()).Messages.Single().Fingerprint;

    // ---- a job that could not be loaded -------------------------------------------------------

    private NotificationPlan PlanUnloadable(
        NotifyStateStore state, bool muted = false, string job = "iis") =>
        NotificationPlanner.PlanFor(
            new RunSummary
            {
                RunId = "01JQTEST",
                Machine = "WEB01",
                JobsRun = 0,
                Completed = 0,
                BytesFreed = 0,

                // Never observed: it did not reach the runner. It joins the considered set only
                // because it carries a diagnostic.
                ObservedJobs = [],
                UnloadableJobs = [job],
                MutedJobs = muted ? [job] : [],
            },
            [Bad(job, DiagnosticCode.DangerousPathRefused)],
            Settings(), state, _now);

    /// <summary>
    /// A job that could not be loaded is reported on the first run, not baselined.
    /// </summary>
    /// <remarks>
    /// Baselining exists so installing this on an already-broken machine does not page anybody
    /// about problems predating the install. A job whose configuration will not validate is a
    /// change somebody made minutes ago, so that reasoning does not reach it - and before
    /// milestone 16 the same condition stopped every rotation on the machine, which nobody could
    /// miss. Left to baseline, the first night is silent and nothing is said until RemindAfter
    /// elapses: seven days, with the log not rotating throughout.
    /// </remarks>
    [Fact]
    public void AJobThatCouldNotBeLoadedIsReportedOnTheFirstRun()
    {
        var plan = PlanUnloadable(State());

        var message = plan.Messages.ShouldHaveSingleItem();
        message.Job.ShouldBe("iis");
        message.Reason.ShouldBe(NotifyReason.NewFailure);
    }

    /// <summary>An ordinary first sighting is still baselined.</summary>
    /// <remarks>
    /// The exemption has to be narrow, or it becomes the wall of alerts baselining exists to
    /// prevent. A job that loaded fine and merely failed is not a configuration change.
    /// </remarks>
    [Fact]
    public void AnOrdinaryFirstFailureIsStillBaselined() =>
        Plan([Bad()], State()).Messages
            .ShouldBeEmpty("a job that loaded and then failed is the case baselining is for");

    /// <summary>
    /// notify = false is honoured for a job that could not be loaded.
    /// </summary>
    /// <remarks>
    /// The muted set was built from config.Jobs, and a skipped job is by definition not in it -
    /// so the operator's mute stopped applying at precisely the moment the job broke. It is why
    /// LoadedConfig.SkippedJobs carries the whole job rather than its name.
    /// </remarks>
    [Fact]
    public void AMutedJobThatCouldNotBeLoadedIsStillMuted() =>
        PlanUnloadable(State(), muted: true).Messages.ShouldBeEmpty();
}
