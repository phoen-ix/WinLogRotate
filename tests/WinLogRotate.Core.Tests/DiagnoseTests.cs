using System.ComponentModel;
using Shouldly;
using WinLogRotate.Contracts;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Engine;
using WinLogRotate.Core.Io;
using WinLogRotate.Core.Journaling;
using WinLogRotate.Core.Safety;
using WinLogRotate.Core.State;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// The engine used to hand the CLI bare strings, which were all labelled
/// <see cref="DiagnosticCode.RotationFailed"/> at <see cref="Severity.Error"/>. These tests pin
/// the classification, because the distinctions only matter if they survive a refactor: a
/// notifier that groups failures by code cannot tell "we refused to delete from System32" from
/// "the file was locked" if both arrive as the same code.
/// </summary>
public sealed class DiagnoseTests
{
    private static PathGuard Guard() => new(new GuardOptions { MaxMatches = 500 });

    [Fact]
    public void ARefusedProtectedLocationIsASecurityDiagnostic_NotARotationFailure()
    {
        var decision = Guard().CheckPattern(@"C:\Windows\System32\*.log", GuardScope.None);
        decision.IsAllowed.ShouldBeFalse();

        var d = Diagnose.Refusal(decision, job: "Bad job");

        d.Code.ShouldBe(DiagnosticCode.DangerousPathRefused);
        d.Job.ShouldBe("Bad job");
        d.Path.ShouldBe(decision.Subject);
    }

    [Fact]
    public void TheRuntimeAndTheValidatorAgreeOnSeverityForTheSameCondition()
    {
        // ConfigValidator reports a refused pattern as an Error. The runtime guard reports the
        // identical condition, and a severity that depends on which code path happened to
        // notice it would make any threshold behave differently for the same broken config.
        Diagnose.Refusal(Guard().CheckPattern(@"C:\Windows\System32\*.log", GuardScope.None))
            .Severity.ShouldBe(Severity.Error);
    }

    [Fact]
    public void TheGuardsOwnWordingAndRemedyAreCarriedThrough()
    {
        // The guard already writes the best available explanation and the exact fix. Re-wording
        // it here would mean two places to keep correct, and the copy would lose.
        var decision = Guard().CheckMatchCount(@"C:\logs\*", 1842, GuardScope.None);

        var d = Diagnose.Refusal(decision);

        d.Message.ShouldBe(decision.Message);
        d.Remedy.ShouldBe(decision.Remedy);
        d.Remedy.ShouldNotBeNull();
    }

    [Fact]
    public void TooManyMatchesIsAMisconfiguration_NotAnAttack()
    {
        var d = Diagnose.Refusal(Guard().CheckMatchCount(@"C:\logs\*", 40_000, GuardScope.None));

        d.Severity.ShouldBe(Severity.Error);
        d.Code.ShouldBe(DiagnosticCode.DangerousPathRefused);
    }

    [Fact]
    public void OnlyAReparsePointEscapeIsCritical()
    {
        // Any local user can create a junction with no privilege at all. One inside a directory
        // we walk as SYSTEM is somebody trying something, which is what Critical is for -
        // and what it stops meaning if a typo can also produce it.
        var escape = new GuardDecision
        {
            Verdict = GuardVerdict.ReparsePoint,
            Subject = @"C:\app\logs",
            Message = "resolves outside the job's root",
        };

        Diagnose.Refusal(escape).Severity.ShouldBe(Severity.Critical);
        Diagnose.Refusal(escape).Code.ShouldBe(DiagnosticCode.ReparsePointRefused);
    }

    [Fact]
    public void AMalformedPathIsAConfigurationMistake_NotASecurityEvent()
    {
        // Putting a typo in the 9xxx band would train operators to skim the one band that must
        // never be skimmed.
        var d = Diagnose.Refusal(Guard().CheckPattern(@"C:logs\*.log", GuardScope.None));

        d.Code.ShouldBe(DiagnosticCode.ConfigInvalid);
        d.Severity.ShouldBe(Severity.Error);
    }

    [Fact]
    public void ASharingViolationKeepsItsOwnCodeAndCarriesTheWin32Number()
    {
        var e = new IOException("in use", unchecked((int)0x80070020));

        var d = Diagnose.Failure(PlannedAction.Rename, @"C:\logs\app.log", e, "IIS");

        // A share violation means a lockstrategy question, not a permissions question - so it
        // is the one failure that must stay distinguishable from every other rotation error.
        d.Code.ShouldBe(DiagnosticCode.FileLocked);
        d.NativeError.ShouldBe(Win32Error.SharingViolation);
        d.Job.ShouldBe("IIS");
        d.Remedy.ShouldNotBeNull();
        d.Remedy.ShouldContain("probe");
    }

    [Fact]
    public void AccessDeniedIsARotationFailureButStillCarriesItsErrorClass()
    {
        var d = Diagnose.Failure(
            PlannedAction.Delete, @"C:\logs\app.1.log", new UnauthorizedAccessException(), "IIS");

        d.Code.ShouldBe(DiagnosticCode.RotationFailed);
        d.NativeError.ShouldBe(Win32Error.AccessDenied);
    }

    [Fact]
    public void AnExceptionWithNoWin32CodeLeavesTheErrorClassNull()
    {
        // Null rather than zero: zero is a real Win32 value (ERROR_SUCCESS), and a grouping key
        // that cannot distinguish "no code" from "succeeded" is a grouping key with a bug in it.
        var d = Diagnose.Failure(PlannedAction.Compress, @"C:\logs\a.log", new IOException("disk"));

        d.NativeError.ShouldBeNull();
        d.Message.ShouldContain("disk");
    }

    [Fact]
    public void TheErrorClassIsANumber_SoRewordingAMessageCannotRegroupFailures()
    {
        // The point of NativeError. Anything that fingerprints on prose reports every ongoing
        // failure as brand new the first time somebody improves an error message.
        var a = Diagnose.Failure(PlannedAction.Rename, @"C:\logs\a.log", Locked());
        var b = Diagnose.Failure(PlannedAction.Delete, @"D:\other\b.log", Locked());

        a.Message.ShouldNotBe(b.Message);
        a.NativeError.ShouldBe(b.NativeError);
        a.Code.ShouldBe(b.Code);

        static Exception Locked() => new Win32Exception(Win32Error.SharingViolation).ToIo();
    }

    [Fact]
    public void AClrIoExceptionHasNoWin32CodeAtAll()
    {
        // COR_E_IO is 0x80131620. Masking it with 0xFFFF yields 5664, which used to be rendered
        // to operators as "Win32 error 5664" and compared against ERROR_SHARING_VIOLATION.
        var clr = new IOException("the disk is full");
        clr.HResult.ShouldBe(unchecked((int)0x80131620));

        Win32Error.TryFromHResult(clr.HResult, out var code).ShouldBeFalse();
        code.ShouldBe(0);
        RetryPolicy.ErrorCode(clr).ShouldBe(0);
    }

    [Fact]
    public void AWin32IoExceptionStillYieldsItsCode()
    {
        var win32 = new IOException("in use", unchecked((int)0x80070020));

        Win32Error.TryFromHResult(win32.HResult, out var code).ShouldBeTrue();
        code.ShouldBe(Win32Error.SharingViolation);
        RetryPolicy.IsTransient(win32).ShouldBeTrue();
    }

    [Fact]
    public void AClrExceptionWhoseLowWordLooksLikeAShareViolationIsNotRetried()
    {
        // The failure mode the facility check exists to prevent: retrying, with backoff,
        // something that was never a share violation and will never stop failing.
        // Facility 0x13 (the CLR's own), low word 32.
        var impostor = new IOException("not Win32", unchecked((int)0x80130020));
        (impostor.HResult & 0xFFFF).ShouldBe(Win32Error.SharingViolation);

        RetryPolicy.IsTransient(impostor).ShouldBeFalse();
    }
}

/// <summary>
/// RunReport carries the same failures twice: as the flat strings the --json envelope has
/// always exposed, and as classified diagnostics. They are appended together, and a caller
/// that reads one and not the other must never see a different set of failures.
/// </summary>
public sealed class RunReportDiagnosticsTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-rr-");

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// Patterns are Windows-shaped on purpose. PathGuard refuses anything that is not an
    /// absolute Windows path, so a Linux temp directory would be rejected as LR1003 before the
    /// case under test was ever reached - and the test would pass for the wrong reason.
    /// </summary>
    private RunReport RunWith(string pattern, bool missingOk = false)
    {
        var job = new EffectiveJob
        {
            Name = "bad",
            Kind = JobKind.Manage,
            Paths = [pattern],
            Enabled = true,
            Schedule = Schedule.Daily,
            Weekday = 0,
            MonthDay = 0,
            Rotate = 3,
            Start = 1,
            MaxAge = null,
            MinAge = null,
            MinSize = null,
            MaxSize = null,
            SizeThreshold = 1 << 20,
            Compress = true,
            CompressType = CompressType.Zip,
            DelayCompress = false,
            DateExt = false,
            DateFormat = "-yyyyMMdd",
            MissingOk = missingOk,
            NotIfEmpty = true,
            OldDir = null,
            CreateOldDir = false,
            LockStrategy = LockStrategy.Rename,
            LiveFiles = 1,
            MaxFiles = 1000,
            RetryCount = 1,
            RetryIntervalMs = 1,
            PreRotate = [],
            PostRotate = [],
            HookTimeout = TimeSpan.FromSeconds(60),
            AllowDangerous = [],
        };

        var config = new LoadedConfig
        {
            Jobs = [job],
            Diagnostics = [],
            Paths = InstallPaths.Resolve(_dir.FullName),
            Quarantined = [],
        };

        var runner = new RotationRunner(
            new NullJournal(),
            new PathGuard(new GuardOptions()),
            StateStore.Load(Path.Combine(_dir.FullName, "state.json"), out _),
            TimeProvider.System);

        return runner.Run(config, new RunOptions { DryRun = true });
    }

    [Fact]
    public void EveryErrorStringHasAMatchingDiagnostic()
    {
        var report = RunWith(@"C:\Windows\System32\*.log");

        report.Errors.Count.ShouldBe(report.Diagnostics.Count);
        report.Diagnostics.Select(d => d.Message).ShouldBe(report.Errors);
    }

    [Fact]
    public void ARefusedPathIsReportedAsTheSecurityDecisionItWas()
    {
        // The regression this whole change exists to prevent: the guard deliberately refused to
        // touch System32, and that used to reach the operator as "rotation failed".
        var d = RunWith(@"C:\Windows\System32\*.log").Diagnostics.ShouldHaveSingleItem();

        d.Code.ShouldBe(DiagnosticCode.DangerousPathRefused);
        d.Job.ShouldBe("bad");
    }

    [Fact]
    public void ARefusedPathIsCountedOnce_NotAlsoAsAMissingFile()
    {
        // One cause, one failure. Reporting the refusal and then "matched no files" describes
        // the consequence as a second problem and doubles the count a monitor sees.
        var report = RunWith(@"C:\Windows\System32\*.log");

        report.Failed.ShouldBe(1);
        report.Diagnostics.Select(d => d.Code).ShouldNotContain(DiagnosticCode.FileMissing);
    }

    [Fact]
    public void AMissingLogIsAttributedToItsJob()
    {
        var report = RunWith(@"C:\logs\definitely-not-here\*.log");

        var d = report.Diagnostics.ShouldHaveSingleItem();
        d.Code.ShouldBe(DiagnosticCode.FileMissing);
        d.Job.ShouldBe("bad");
        d.Remedy.ShouldNotBeNull();
    }

    [Fact]
    public void AJobThatMatchesNothingWithMissingOkReportsNothingAtAll()
    {
        var report = RunWith(@"C:\logs\definitely-not-here\*.log", missingOk: true);

        report.Failed.ShouldBe(0);
        report.Diagnostics.ShouldBeEmpty();
        report.Errors.ShouldBeEmpty();
        report.ExitCode.ShouldBe(ExitCode.Ok);
    }
}

file static class ExceptionExtensions
{
    /// <summary>An IOException carrying a real Win32 code, the way the BCL produces one.</summary>
    public static IOException ToIo(this Win32Exception e) =>
        new(e.Message, unchecked((int)(0x80070000 | (uint)e.NativeErrorCode)));
}
