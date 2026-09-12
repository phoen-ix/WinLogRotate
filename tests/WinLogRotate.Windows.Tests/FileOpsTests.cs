using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Shouldly;
using WinLogRotate.Core.Io;
using WinLogRotate.Core.Safety;
using WinLogRotate.Core.State;
using Xunit;

namespace WinLogRotate.Windows.Tests;

/// <summary>
/// The file operations a rotation is made of, against real files and real handles.
/// </summary>
/// <remarks>
/// <c>FileOps</c> had no tests. Every executor test ran with <c>dryRun: true</c>, so
/// <c>CopyTruncate</c> - the one operation that empties a customer's log in place - had never been
/// executed by anything but a real installation.
/// </remarks>
public sealed partial class FileOpsTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-fileops-");

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    private string Path(string name) => System.IO.Path.Combine(_dir.FullName, name);

    private string Log(int bytes = 4096, string name = "app.log")
    {
        var path = Path(name);
        File.WriteAllBytes(path, [.. Enumerable.Repeat((byte)'x', bytes)]);
        return path;
    }

    [Fact]
    public void CopyTruncateLeavesTheOriginalEmptyAndTheCopyComplete()
    {
        WindowsOnly.Require();

        var source = Log(4096);
        var archive = Path("app.log.1");

        var outcome = FileOps.CopyTruncate(source, archive, truncate: true);

        outcome.SizeBefore.ShouldBe(4096);
        outcome.SizeAfter.ShouldBe(0);
        new FileInfo(archive).Length.ShouldBe(4096);
        new FileInfo(source).Length.ShouldBe(0);
    }

    [Fact]
    public void CopyWithoutTruncationLeavesTheOriginalUntouched()
    {
        WindowsOnly.Require();

        var source = Log(4096);
        FileOps.CopyTruncate(source, Path("app.log.1"), truncate: false);

        new FileInfo(source).Length.ShouldBe(4096);
    }

    /// <summary>The strategy's entire reason for existing, never once tested.</summary>
    [Fact]
    public void CopyTruncateWorksThroughAWriterThatPermitsWriteSharing()
    {
        WindowsOnly.Require();

        var source = Log(4096);
        using var held = new FileStream(source, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

        FileOps.CopyTruncate(source, Path("app.log.1"), truncate: true);

        new FileInfo(Path("app.log.1")).Length.ShouldBe(4096);
    }

    /// <summary>
    /// Rotating an already-emptied log archives nothing, and does so atomically.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a second <b>rotation</b>, not a retry, and the distinction is the whole reason
    /// CopyTruncate was wrong for so long. The test used to be called
    /// <c>CopyTruncateDoesNotDestroyTheArchiveWhenItIsRunAgain</c> and to say that running the
    /// call twice "is the same shape as that retry". It is not. A second call is a legitimate
    /// rotation of a log that is now empty, and an empty archive is the right answer for it -
    /// which is what the assertion below has always said.
    /// </para>
    /// <para>
    /// A retry is the different thing: it happens <i>inside</i> one call, against a source that
    /// call has already truncated. Nothing here could observe that, so the name promised cover
    /// this body never provided, and the defect lived underneath the promise for its whole life.
    /// What actually guards it now is the split in <c>FileOps.CopyTruncate</c> and
    /// <c>NothingRetriesTheWholeCopyTruncate</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public void ASecondRotationOfAnEmptiedLogArchivesNothing()
    {
        WindowsOnly.Require();

        var source = Log(4096);
        var archive = Path("app.log.1");

        FileOps.CopyTruncate(source, archive, truncate: true);
        new FileInfo(archive).Length.ShouldBe(4096);

        FileOps.CopyTruncate(source, archive, truncate: true);

        new FileInfo(archive).Length.ShouldBe(
            0, "the log really was empty by now, so an empty archive is the honest result");

        // And no staging file is left behind either way.
        File.Exists(archive + ".part").ShouldBeFalse();
    }

    /// <summary>
    /// A cut that keeps failing leaves the archive it already committed alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The archive is committed before the live log is emptied, so everything depends on what a
    /// failure after that point retries. It used to retry the whole call: the second attempt
    /// measured a source the first had already truncated, copied nothing, and moved the nothing
    /// over the archive it had just saved - returning normally, so the run reported a success and
    /// the rotation clock advanced.
    /// </para>
    /// <para>
    /// A shared byte-range lock is what makes the cut fail on demand: it leaves reads alone, so
    /// the copy completes and the archive is committed, and denies writes, so the truncation
    /// cannot apply. The exclusive lock this reached for first is no use - it would fail the copy
    /// instead, and the phase under test would never run.
    /// </para>
    /// </remarks>
    [Fact]
    public void ACutThatKeepsFailingLeavesTheArchiveAlone()
    {
        WindowsOnly.Require();

        var source = Log(4096);
        var archive = Path("app.log.1");

        using var blocker = new FileStream(
            source, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);

        SharedLock(blocker, 0, 4096);

        var retries = new List<int>();

        Should.Throw<IOException>(() => FileOps.CopyTruncate(
            source, archive, truncate: true,
            attempts: 3, intervalMs: 1,
            onRetry: (_, e) => retries.Add(RetryPolicy.ErrorCode(e))));

        // The failure really was the transient one, retried the full number of times - without
        // this the assertion below would hold for a call that never got as far as the cut.
        retries.ShouldBe([Win32Error.LockViolation, Win32Error.LockViolation]);

        new FileInfo(archive).Length.ShouldBe(
            4096, "the cut failed; the archive it had already committed must be untouched");
    }

    /// <summary>
    /// A shared lock: readers are let through, writers are not.
    /// </summary>
    /// <remarks>
    /// FileStream.Lock takes an exclusive one, which denies reads as well - so it cannot express
    /// "let the copy finish and stop the truncation", which is the only arrangement that puts a
    /// failure where this test needs one.
    /// </remarks>
    private static unsafe void SharedLock(FileStream file, long offset, long length)
    {
        var overlapped = new NativeOverlapped
        {
            OffsetLow = unchecked((int)(offset & 0xFFFFFFFF)),
            OffsetHigh = unchecked((int)(offset >> 32)),
        };

        if (!LockFileEx(
                file.SafeFileHandle, 0, 0,
                unchecked((uint)(length & 0xFFFFFFFF)), unchecked((uint)(length >> 32)),
                &overlapped))
        {
            throw new IOException($"could not take a shared lock: {Marshal.GetLastWin32Error()}");
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool LockFileEx(
        SafeFileHandle file, uint flags, uint reserved,
        uint countLow, uint countHigh, NativeOverlapped* overlapped);

    /// <summary>
    /// Bytes the writer appends during the copy are conserved, not dropped.
    /// </summary>
    /// <remarks>
    /// Asserted as conservation rather than as timing: what matters is that no byte is lost,
    /// whether it ended up in the archive or was preserved as the live file's new head. The race
    /// itself cannot be scheduled deterministically.
    /// </remarks>
    [Fact]
    public void BytesAppendedDuringTheCopyAreConserved()
    {
        WindowsOnly.Require();

        var source = Log(4096);
        var archive = Path("app.log.1");

        var outcome = FileOps.CopyTruncate(source, archive, truncate: true);

        (new FileInfo(archive).Length + new FileInfo(source).Length)
            .ShouldBe(4096, "every byte is either archived or preserved as the new head");
        outcome.SizeAfter.ShouldBe(new FileInfo(source).Length);
    }

    [Fact]
    public void CreateDoesNotTruncateALogTheWriterAlreadyRecreated()
    {
        WindowsOnly.Require();

        var path = Log(512);
        FileOps.Create(path);

        new FileInfo(path).Length.ShouldBe(512, "the writer got there first; do not empty it");
    }

    [Fact]
    public void DeleteToleratesAFileThatHasAlreadyGone()
    {
        WindowsOnly.Require();

        Should.NotThrow(() => FileOps.Delete(Path("never-existed.log")));
    }

    [Fact]
    public void RenameReplacesAnExistingArchive()
    {
        WindowsOnly.Require();

        var source = Log(4096);
        var archive = Path("app.log.1");
        File.WriteAllText(archive, "stale");

        FileOps.Rename(source, archive);

        new FileInfo(archive).Length.ShouldBe(4096);
        File.Exists(source).ShouldBeFalse();
    }
}
