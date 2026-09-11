using Shouldly;
using WinLogRotate.Contracts;
using WinLogRotate.Core.Compression;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Engine;
using WinLogRotate.Core.Io;
using WinLogRotate.Core.Safety;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// Where a job's archives are allowed to land, and who is allowed to make the place.
/// </summary>
/// <remarks>
/// Pure, so the whole rule holds on the Linux leg: the gate is handed the guard's verdict and
/// whether the directory exists rather than going and looking.
/// </remarks>
public sealed class OldDirGateTests
{
    private static PathGuard Guard() =>
        new(new GuardOptions
        {
            ProtectedRoots = [@"C:\Windows", @"C:\Program Files"],
            Overrides = OverrideGate.Open,
        });

    private static EffectiveJob Job(string? oldDir, bool createOldDir) => new()
    {
        Name = "archive-job",
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
        OldDir = oldDir,
        CreateOldDir = createOldDir,
        LockStrategy = LockStrategy.Rename,
        LiveFiles = 1,
        MaxFiles = 1000,
        RetryCount = 5,
        RetryIntervalMs = 100,
        PreRotate = [],
        PostRotate = [],
        HookTimeout = TimeSpan.FromSeconds(60),
        AllowDangerous = [],
    };

    private static OldDirDecision Check(EffectiveJob job, string directory, bool exists) =>
        OldDirGate.Check(job, directory, exists, Guard().CheckPath(directory, job.GuardScope));

    /// <summary>An existing directory needs nothing done and says nothing.</summary>
    /// <remarks>Otherwise every run emits a mkdir for a directory that has been there for months.</remarks>
    [Fact]
    public void AnExistingOldDirProducesNoOperationAndNoDiagnostic()
    {
        var decision = Check(Job(@"D:\archive", createOldDir: false), @"D:\archive", exists: true);

        decision.Usable.ShouldBeTrue();
        decision.Create.ShouldBeNull();
        decision.Diagnostic.ShouldBeNull();
    }

    /// <summary>
    /// A missing olddir without createolddir refuses, and names the directory.
    /// </summary>
    /// <remarks>
    /// logrotate's rule. What happened instead was that MoveFileEx returned ERROR_PATH_NOT_FOUND,
    /// Win32Error rendered it as "the file no longer exists", and Diagnose attributed that to the
    /// live log - which existed. The Create op that follows a rename put the log back, so the job
    /// failed once per rotation for ever, with a message naming the wrong file.
    /// </remarks>
    [Fact]
    public void AMissingOldDirWithoutCreateOldDirIsRefusedAndNamesTheDirectory()
    {
        var decision = Check(Job(@"D:\archive", createOldDir: false), @"D:\archive", exists: false);

        decision.Usable.ShouldBeFalse();
        decision.Create.ShouldBeNull();

        var d = decision.Diagnostic.ShouldNotBeNull();
        d.Severity.ShouldBe(Severity.Error);
        d.Path.ShouldBe(@"D:\archive", "the destination is the subject, not the log");
        d.Remedy.ShouldNotBeNull().ShouldContain("createolddir");
    }

    /// <summary>createolddir asks for one operation, not a side effect inside a file primitive.</summary>
    [Fact]
    public void AMissingOldDirWithCreateOldDirProducesOneCreateOperation()
    {
        var decision = Check(Job(@"D:\archive", createOldDir: true), @"D:\archive", exists: false);

        decision.Usable.ShouldBeTrue();
        decision.Diagnostic.ShouldBeNull();

        var op = decision.Create.ShouldNotBeNull();
        op.Action.ShouldBe(PlannedAction.CreateDirectory);
        op.Source.ShouldBe(@"D:\archive");
    }

    /// <summary>
    /// The guard is asked before existence, so createolddir cannot make a protected directory.
    /// </summary>
    /// <remarks>
    /// The rule ordering is the security property. Asked the other way round, createolddir = true
    /// would have WinLogRotate create - and then write into - somewhere it is about to refuse.
    /// </remarks>
    [Fact]
    public void AnOldDirInsideAProtectedRootIsRefusedEvenWhenCreateOldDirIsSet()
    {
        var decision = Check(
            Job(@"C:\Windows\System32\archive", createOldDir: true),
            @"C:\Windows\System32\archive", exists: false);

        decision.Usable.ShouldBeFalse();
        decision.Create.ShouldBeNull("createolddir must never make a directory somewhere refused");
        decision.Diagnostic.ShouldNotBeNull().Code.ShouldBe(DiagnosticCode.DangerousPathRefused);
    }

    /// <summary>A missing directory is not a missing file.</summary>
    /// <remarks>
    /// One arm answered for both codes, so an operator chasing a rotation failure was told the log
    /// had vanished when the truth was that its archive directory had never been made.
    /// </remarks>
    [Fact]
    public void DescribeTellsAMissingFileFromAMissingDirectory()
    {
        Win32Error.Describe(Win32Error.FileNotFound).ShouldBe("the file no longer exists");
        Win32Error.Describe(Win32Error.PathNotFound).ShouldContain("directory");
        Win32Error.Describe(Win32Error.PathNotFound)
            .ShouldNotBe(Win32Error.Describe(Win32Error.FileNotFound));
    }

    /// <summary>A failure that was writing somewhere says where.</summary>
    [Fact]
    public void AFailureNamesWhereItWasWriting()
    {
        var d = Diagnose.Failure(
            PlannedAction.Rename, @"C:\logs\app.log",
            new IOException("x", unchecked((int)0x80070003)),
            job: "app", destination: @"D:\archive\app.log.1");

        d.Message.ShouldContain(@"D:\archive\app.log.1");
        d.Message.ShouldContain("directory in the path does not exist");
    }
}
