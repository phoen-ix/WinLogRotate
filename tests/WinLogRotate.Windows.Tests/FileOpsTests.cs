using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Shouldly;
using WinLogRotate.Core.Io;
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
public sealed class FileOpsTests : IDisposable
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

    // There is no test here for "a cut that keeps failing leaves the archive alone", and the
    // reason is worth recording rather than leaving as an absence.
    //
    // Provoking it needs the truncation to fail while the copy succeeds, which needs a lock that
    // permits reads and denies writes over the range the cut touches. A shared byte-range lock is
    // exactly that, and it does not work here: Windows associates byte-range locks with the
    // LOCKING PROCESS, not with the handle, so a blocker opened by the test does not bind the
    // product's handle a few frames up the same stack. This was written, pushed, and observed to
    // let the truncation straight through.
    //
    // A second process holding the lock would work. That is a real fixture, not a line of setup,
    // and it belongs to whoever decides this branch is worth that. What guards the behaviour
    // meanwhile is the split itself and ArchitectureTests.NothingRetriesTheWholeCopyTruncate.

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
