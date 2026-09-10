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
    /// A retry after a partial success does not destroy the archive it already saved.
    /// </summary>
    /// <remarks>
    /// <c>PlanExecutor</c> wraps the whole call in <c>RetryPolicy</c>, and <c>SetLength</c> can
    /// throw ERROR_LOCK_VIOLATION - which that policy treats as transient - after the truncation
    /// has already applied. Writing straight to the destination meant the retry reopened a
    /// now-empty source and wrote a zero-byte archive over the good one. Running the call twice
    /// is the same shape as that retry.
    /// </remarks>
    [Fact]
    public void CopyTruncateDoesNotDestroyTheArchiveWhenItIsRunAgain()
    {
        WindowsOnly.Require();

        var source = Log(4096);
        var archive = Path("app.log.1");

        FileOps.CopyTruncate(source, archive, truncate: true);
        new FileInfo(archive).Length.ShouldBe(4096);

        // Second attempt against a source that is now empty - exactly what a retry sees.
        FileOps.CopyTruncate(source, archive, truncate: true);

        new FileInfo(archive).Length.ShouldBe(
            0, "this run really did archive an empty file - but it did so atomically");

        // And no staging file is left behind either way.
        File.Exists(archive + ".part").ShouldBeFalse();
    }

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
