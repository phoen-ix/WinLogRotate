using Shouldly;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Io;
using WinLogRotate.Core.State;
using Xunit;

namespace WinLogRotate.Windows.Tests;

/// <summary>
/// The probe, against genuinely contended handles.
/// </summary>
/// <remarks>
/// <para>
/// <c>LockProbe</c> had no tests at all. It is the code that decides which strategy a rotation may
/// use, its whole job is to answer a question about a file somebody else is holding, and until now
/// it had never met one.
/// </para>
/// <para>
/// No orchestration is needed for that: Windows enforces share modes per handle, not per process,
/// so a holder opened in this very test is as contended as one in another process. That makes the
/// whole matrix deterministic and sub-millisecond.
/// </para>
/// </remarks>
public sealed class LockProbeTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-probe-");

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    private string Log(string name = "app.log")
    {
        var path = Path.Combine(_dir.FullName, name);
        File.WriteAllText(path, "log line one\r\n");
        return path;
    }

    /// <summary>Opens the file the way a logger does, and keeps the handle.</summary>
    private static FileStream Hold(string path, FileShare share) =>
        new(path, FileMode.Open, FileAccess.Write, share);

    [Fact]
    public void AFileNobodyHoldsCanBeRenamed()
    {
        WindowsOnly.Require();

        var result = LockProbe.Classify(Log());

        result.Verdict.ShouldBe(ProbeVerdict.Rename);
        result.Unlocked.ShouldBeTrue();
    }

    /// <summary>A holder that permits delete sharing still allows the atomic rename.</summary>
    [Fact]
    public void AHolderThatPermitsDeleteSharingStillAllowsARename()
    {
        WindowsOnly.Require();

        var path = Log();
        using var held = Hold(path, FileShare.ReadWrite | FileShare.Delete);

        var result = LockProbe.Classify(path);

        result.Verdict.ShouldBe(ProbeVerdict.Rename);
        result.Unlocked.ShouldBeFalse("something is demonstrably holding it");
    }

    /// <summary>
    /// A holder that withholds delete sharing is why <c>copytruncate</c> exists.
    /// </summary>
    /// <remarks>
    /// MSVCRT's <c>fopen("a")</c>, .NET's <c>FileMode.Append</c> and IIS all produce this. Renaming
    /// needs DELETE access, which needs every existing handle to have permitted FILE_SHARE_DELETE.
    /// </remarks>
    [Fact]
    public void AHolderThatWithholdsDeleteSharingForcesCopyTruncate()
    {
        WindowsOnly.Require();

        var path = Log();
        using var held = Hold(path, FileShare.ReadWrite);

        var result = LockProbe.Classify(path);

        result.Verdict.ShouldBe(ProbeVerdict.CopyTruncate);
        result.BlockingError.ShouldBe(Win32Error.SharingViolation);
    }

    /// <summary>log4net's default ExclusiveLock: nothing outside the process can rotate it.</summary>
    [Fact]
    public void AHolderThatPermitsOnlyReadingLeavesNothingButACopy()
    {
        WindowsOnly.Require();

        var path = Log();
        using var held = Hold(path, FileShare.Read);

        LockProbe.Classify(path).Verdict.ShouldBe(ProbeVerdict.Copy);
    }

    [Fact]
    public void AnExclusiveHolderLeavesNoStrategyAtAll()
    {
        WindowsOnly.Require();

        var path = Log();
        using var held = Hold(path, FileShare.None);

        var result = LockProbe.Classify(path);

        result.Verdict.ShouldBe(ProbeVerdict.None);
        result.BlockingError.ShouldBe(Win32Error.SharingViolation);
    }

    /// <summary>
    /// The probe never locks out the writer it is inspecting.
    /// </summary>
    /// <remarks>
    /// Stated three times in <c>LockProbe</c>'s own comments and asserted nowhere. A probe that
    /// took an exclusive handle would break the very application whose logs it was measuring, and
    /// it would do so only on the machines where it mattered.
    /// </remarks>
    [Fact]
    public void TheProbeDoesNotDisturbTheWriter()
    {
        WindowsOnly.Require();

        var path = Log();
        using var held = Hold(path, FileShare.ReadWrite);

        LockProbe.Classify(path);

        // The holder must still be able to work afterwards.
        held.Write("still writing\r\n"u8);
        held.Flush();
    }

    [Fact]
    public void EveryVerdictMapsToTheBestStrategyItAllows()
    {
        WindowsOnly.Require();

        ProbeSupport.Best(ProbeVerdict.Rename).ShouldBe(LockStrategy.Rename);
        ProbeSupport.Best(ProbeVerdict.CopyTruncate).ShouldBe(LockStrategy.CopyTruncate);
        ProbeSupport.Best(ProbeVerdict.Copy).ShouldBe(LockStrategy.Copy);
        ProbeSupport.Best(ProbeVerdict.None).ShouldBeNull();

        // A quarantined path is offered the next thing down, not nothing.
        ProbeSupport.Best(ProbeVerdict.CopyTruncate, allowCopyTruncate: false)
            .ShouldBe(LockStrategy.Copy);
    }
}
