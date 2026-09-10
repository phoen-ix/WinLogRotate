using Shouldly;
using WinLogRotate.Contracts;
using WinLogRotate.Core.Compression;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Engine;
using WinLogRotate.Core.Io;
using WinLogRotate.Core.State;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>An inspector that answers as told and counts what it was asked.</summary>
internal sealed class FakeInspector(ProbeVerdict verdict = ProbeVerdict.Unknown) : IWriterInspector
{
    public int Classified { get; private set; }

    public int Sampled { get; private set; }

    public int BlockingError { get; init; }

    public WriterSample Next { get; set; } = new() { Opened = true };

    public ProbeResult Classify(string path)
    {
        Classified++;
        return new ProbeResult
        {
            Path = path,
            Verdict = verdict,
            BlockingError = BlockingError,
            Explanation = "as instructed",
        };
    }

    public WriterSample Sample(string path, long fromOffset, int limit = 4096)
    {
        Sampled++;
        return Next;
    }
}

/// <summary>
/// The two decisions a rotation makes about a file another process is writing.
/// </summary>
/// <remarks>
/// Pure, so all of it runs on the Linux leg. One of these rules can refuse a path permanently and
/// the other decides whether a customer's log is truncated in place, so they deserve tests that do
/// not need a particular operating system to be present.
/// </remarks>
public sealed class LockChoiceTests
{
    private const long TwoMegabytes = 2L << 20;

    private static EffectiveJob Job(LockStrategy strategy) => new()
    {
        Name = "app",
        Kind = JobKind.Rotate,
        Paths = [@"C:\logs\app.log"],
        Enabled = true,
        Schedule = Schedule.Daily,
        Weekday = 0,
        MonthDay = 0,
        Rotate = 4,
        Start = 1,
        MaxAge = null,
        MinAge = null,
        MinSize = null,
        MaxSize = null,
        SizeThreshold = 1 << 20,
        Compress = false,
        CompressType = CompressType.None,
        DelayCompress = false,
        DateExt = false,
        DateFormat = "-yyyyMMdd",
        MissingOk = true,
        NotIfEmpty = false,
        OldDir = null,
        CreateOldDir = false,
        LockStrategy = strategy,
        LiveFiles = 1,
        MaxFiles = 1000,
        RetryCount = 5,
        RetryIntervalMs = 100,
        PreRotate = [],
        PostRotate = [],
        HookTimeout = TimeSpan.FromSeconds(60),
        AllowDangerous = [],
    };

    private static PathState Truncated(long from = TwoMegabytes, long to = 0, int checks = 0) => new()
    {
        Path = @"C:\logs\app.log",
        LastTruncatedFrom = from,
        LastTruncatedTo = to,
        TruncationChecks = checks,
    };

    private static NulFillJudgement Judge(PathState state, WriterSample sample) =>
        LockChoice.Judge("app", @"C:\logs\app.log", state, sample);

    // ---- judging a truncation ----------------------------------------------------------------

    /// <summary>
    /// A NUL run where the truncation left the file confirms, and quarantines.
    /// </summary>
    /// <remarks>
    /// The signature itself. A log that genuinely regrew would carry log text at that offset.
    /// </remarks>
    [Fact]
    public void ANulRunAtTheResumeOffsetConfirms()
    {
        var judged = Judge(Truncated(), new WriterSample
        {
            Opened = true,
            Size = TwoMegabytes,
            BytesRead = 4096,
            NulRun = 4096,
        });

        judged.Verdict.ShouldBe(NulFillVerdict.Confirmed);
        judged.Evidence.ShouldBe(NulFillEvidence.NulSignature);
        judged.ClearBaseline.ShouldBeTrue();
        judged.Diagnostic.ShouldNotBeNull().Code.ShouldBe(DiagnosticCode.NulFillDetected);
        judged.Diagnostic.Severity.ShouldBe(Severity.Error);
    }

    /// <summary>
    /// A log that genuinely regrew to its old size is not quarantined.
    /// </summary>
    /// <remarks>
    /// The most important test in the milestone. <c>NulFillDetector.Judge</c> confirms on the size
    /// ratio alone, and a log producing two megabytes a day is back at two megabytes every morning
    /// for entirely innocent reasons. Accepting the ratio one full interval later would
    /// permanently refuse copytruncate to every steadily-growing log on the machine - and the
    /// verdict cannot be undone.
    /// </remarks>
    [Fact]
    public void ASteadyProducerIsNotQuarantined()
    {
        var judged = Judge(Truncated(), new WriterSample
        {
            Opened = true,
            Size = TwoMegabytes,
            BytesRead = 4096,

            // It grew back to exactly its old size - and there is log text where the truncation
            // left it, which is what a writer that honours truncation produces.
            NulRun = 0,
        });

        judged.Verdict.ShouldBe(NulFillVerdict.Clean);
        judged.Diagnostic.ShouldBeNull();
    }

    /// <summary>A writer that has written nothing yet is not evidence of anything.</summary>
    [Fact]
    public void AFileThatHasNotBeenWrittenToIsDeferred()
    {
        // BytesRead = 0: the file has not passed the offset the truncation left it.
        var judged = Judge(Truncated(), new WriterSample { Opened = true, Size = 0 });

        judged.Verdict.ShouldBeNull("recording Clean here would be a claim made on no evidence");
        judged.ClearBaseline.ShouldBeFalse("the evidence is still pending, not spent");
    }

    [Fact]
    public void AnUnreadableFileIsDeferredRatherThanJudged()
    {
        Judge(Truncated(), WriterSample.Unreadable(Win32Error.SharingViolation))
            .Verdict.ShouldBeNull();
    }

    /// <summary>
    /// A baseline that can never be examined is eventually abandoned.
    /// </summary>
    /// <remarks>
    /// An immortal baseline is not harmless: it is evidence that gets staler every night while
    /// still being compared against.
    /// </remarks>
    [Fact]
    public void ABaselineThatCannotBeExaminedIsAbandoned()
    {
        var judged = Judge(
            Truncated(checks: LockChoice.MaxChecks),
            WriterSample.Unreadable(Win32Error.SharingViolation));

        judged.ClearBaseline.ShouldBeTrue();
        judged.Verdict.ShouldBeNull();
        judged.Diagnostic.ShouldNotBeNull().Severity.ShouldBe(Severity.Info);
    }

    [Fact]
    public void AFileThatHasGoneAwayLeavesNoVerdict()
    {
        var judged = Judge(Truncated(), WriterSample.Unreadable(Win32Error.FileNotFound));

        judged.ClearBaseline.ShouldBeTrue();
        judged.Verdict.ShouldBeNull();
        judged.Diagnostic.ShouldBeNull("a log that was deleted is not a problem to report here");
    }

    /// <summary>
    /// A different file wearing the same name discards the evidence, not the verdict.
    /// </summary>
    /// <remarks>
    /// The baseline belongs to the file that is gone. The verdict does not: the defect belongs to
    /// the writer, and a log deleted and recreated by the same broken logger is still broken.
    /// </remarks>
    [Fact]
    public void ARecreatedFileDiscardsTheBaselineButNotTheVerdict()
    {
        var remembered = Truncated() with { FileIdentity = "AAAA-1111" };

        var judged = Judge(remembered, new WriterSample
        {
            Opened = true,
            Size = TwoMegabytes,
            Identity = "AAAA-2222",
            BytesRead = 4096,
            NulRun = 4096,
        });

        judged.ClearBaseline.ShouldBeTrue();
        judged.Verdict.ShouldBeNull("nothing may be concluded about a file we did not truncate");
    }

    [Fact]
    public void NothingIsJudgedWithoutARecordedTruncation()
    {
        Judge(new PathState { Path = @"C:\logs\app.log" }, new WriterSample { Opened = true })
            .Verdict.ShouldBeNull();
    }

    // ---- choosing a strategy -----------------------------------------------------------------

    [Theory]
    [InlineData(ProbeVerdict.Rename, LockStrategy.Rename)]
    [InlineData(ProbeVerdict.CopyTruncate, LockStrategy.CopyTruncate)]
    [InlineData(ProbeVerdict.Copy, LockStrategy.Copy)]
    public void AutoTakesWhateverTheWriterPermits(ProbeVerdict verdict, LockStrategy expected)
    {
        var probe = new FakeInspector(verdict);

        var decision = LockChoice.Choose(
            Job(LockStrategy.Auto), @"C:\logs\app.log", NulFillVerdict.Unknown, probe);

        decision.Strategy.ShouldBe(expected);
        decision.Probed.ShouldBeTrue();
        probe.Classified.ShouldBe(1);
    }

    /// <summary>
    /// A file nothing can open is not rotated, and says which error stopped it.
    /// </summary>
    /// <remarks>
    /// log4net's default ExclusiveLock looks like this. Reporting it as a rotation failure rather
    /// than silently renaming - which is what auto did - is the whole point.
    /// </remarks>
    [Fact]
    public void AutoRefusesAFileNothingCanOpen()
    {
        var decision = LockChoice.Choose(
            Job(LockStrategy.Auto), @"C:\logs\app.log", NulFillVerdict.Unknown,
            new FakeInspector(ProbeVerdict.None) { BlockingError = Win32Error.SharingViolation });

        decision.Strategy.ShouldBeNull();
        decision.Diagnostic.ShouldNotBeNull().Code.ShouldBe(DiagnosticCode.FileLocked);
        decision.Diagnostic.NativeError.ShouldBe(Win32Error.SharingViolation);
    }

    /// <summary>Falling back to a copy is said every run, because the file keeps growing.</summary>
    [Fact]
    public void AutoDegradingToACopyWarnsThatTheOriginalKeepsGrowing()
    {
        var decision = LockChoice.Choose(
            Job(LockStrategy.Auto), @"C:\logs\app.log", NulFillVerdict.Unknown,
            new FakeInspector(ProbeVerdict.Copy));

        decision.Strategy.ShouldBe(LockStrategy.Copy);
        decision.Diagnostic.ShouldNotBeNull().Severity.ShouldBe(Severity.Warning);
        decision.Diagnostic.Message.ShouldContain("keeps growing");
    }

    /// <summary>
    /// A quarantined path is not offered copytruncate, even when the writer would permit it.
    /// </summary>
    /// <remarks>
    /// The regression that would otherwise fill a disk quietly: the probe answers what the writer
    /// permits, which does not change because we learned the writer misbehaves.
    /// </remarks>
    [Fact]
    public void AQuarantinedPathIsNotOfferedCopyTruncateByAuto()
    {
        var decision = LockChoice.Choose(
            Job(LockStrategy.Auto), @"C:\logs\app.log", NulFillVerdict.Confirmed,
            new FakeInspector(ProbeVerdict.CopyTruncate));

        decision.Strategy.ShouldBe(LockStrategy.Copy);
        decision.Diagnostic.ShouldNotBeNull().Message.ShouldContain("NUL-fill");
    }

    /// <summary>An explicitly configured copytruncate, once refused, is not substituted.</summary>
    /// <remarks>
    /// A rename would leave the writer appending to the archive while the recreated log stays
    /// empty - the log simply stops. A copy would write a second full copy of a NUL-filled file
    /// every night. Both would also be choosing on behalf of somebody who said what they wanted.
    /// </remarks>
    [Fact]
    public void AQuarantinedExplicitCopyTruncateRefusesRatherThanSubstituting()
    {
        var probe = new FakeInspector(ProbeVerdict.Rename);

        var decision = LockChoice.Choose(
            Job(LockStrategy.CopyTruncate), @"C:\logs\app.log", NulFillVerdict.Confirmed, probe);

        decision.Strategy.ShouldBeNull();
        probe.Classified.ShouldBe(0, "an explicit strategy says what it means");
        decision.Diagnostic.ShouldNotBeNull().Severity.ShouldBe(Severity.Error);
        decision.Diagnostic.Code.ShouldBe(DiagnosticCode.StrategyUnavailable);
        decision.Diagnostic.Remedy.ShouldNotBeNull().ShouldContain("MinimalLock");
    }

    /// <summary>The quarantine is about truncation, and touches nothing else.</summary>
    [Theory]
    [InlineData(LockStrategy.Rename)]
    [InlineData(LockStrategy.Copy)]
    public void AQuarantineDoesNotAffectAStrategyThatDoesNotTruncate(LockStrategy strategy)
    {
        LockChoice.Choose(Job(strategy), @"C:\logs\app.log", NulFillVerdict.Confirmed,
                new FakeInspector())
            .Strategy.ShouldBe(strategy);
    }

    /// <summary>
    /// An explicit strategy is never probed.
    /// </summary>
    /// <remarks>
    /// A thousand-file job would otherwise pay three thousand CreateFile calls for a decision
    /// nobody asked it to make.
    /// </remarks>
    [Theory]
    [InlineData(LockStrategy.Rename)]
    [InlineData(LockStrategy.CopyTruncate)]
    [InlineData(LockStrategy.Copy)]
    public void AnExplicitStrategyIsNeverProbed(LockStrategy strategy)
    {
        var probe = new FakeInspector(ProbeVerdict.Copy);

        LockChoice.Choose(Job(strategy), @"C:\logs\app.log", NulFillVerdict.Unknown, probe)
            .Strategy.ShouldBe(strategy);

        probe.Classified.ShouldBe(0);
    }

    /// <summary>Where nothing can be asked, auto falls back to the documented default.</summary>
    /// <remarks>
    /// Rename is what the job would have had without auto, and it fails loudly rather than doing
    /// something else quietly - so falling back to it is safe in a way that guessing is not.
    /// </remarks>
    [Fact]
    public void AutoFallsBackToRenameWhenNothingCanBeAsked()
    {
        var decision = LockChoice.Choose(
            Job(LockStrategy.Auto), @"C:\logs\app.log", NulFillVerdict.Unknown,
            new FakeInspector(ProbeVerdict.Unknown));

        decision.Strategy.ShouldBe(LockStrategy.Rename);
        decision.Probed.ShouldBeFalse("nothing was learned, so nothing should be recorded");
        decision.Diagnostic.ShouldNotBeNull().Severity.ShouldBe(Severity.Warning);
    }

    /// <summary>The explanation names the probe, so a dry run explains the choice.</summary>
    [Fact]
    public void TheExplanationNamesWhatWasProbed()
    {
        LockChoice.Choose(
                Job(LockStrategy.Auto), @"C:\logs\app.log", NulFillVerdict.Unknown,
                new FakeInspector(ProbeVerdict.CopyTruncate))
            .Explanation.ShouldContain("probed CopyTruncate");
    }
}
