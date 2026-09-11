using Shouldly;
using WinLogRotate.Core.Globbing;
using WinLogRotate.Core.Safety;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// The enumerator resolves real Windows paths, so these only run on Windows. The matching
/// logic they depend on is covered exhaustively and platform-independently in
/// <see cref="GlobTests"/>; what is left here is the filesystem behaviour that genuinely
/// cannot be tested anywhere else.
/// </summary>
public sealed class FileEnumeratorTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-enum-");

    public void Dispose()
    {
        try
        {
            _dir.Delete(recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    /// <summary>
    /// An enumerator with nothing protected, so these tests measure matching and not the guard.
    /// </summary>
    /// <remarks>
    /// The default roots include real system directories, and a temp directory is not inside one -
    /// but naming the roots here keeps these tests answering the question they are about, and
    /// stops a change to the defaults reaching in and altering them.
    /// </remarks>
    private static FileEnumerator Enumerator() =>
        new(new PathGuard(new GuardOptions { ProtectedRoots = [] }));

    private void Seed(string relative, string content = "x")
    {
        var path = Path.Combine(_dir.FullName, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void ResolvesOnlyRealMatches()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Resolves Windows paths.");

        Seed(@"logs\app.log");
        Seed(@"logs\app.log.1");
        Seed(@"logs\something.logfile");   // the 8.3 over-match Win32 would have included

        var matches = Enumerator().Resolve(Path.Combine(_dir.FullName, @"logs\*.log")).Files;

        matches.Select(m => Path.GetFileName(m.Path)).ShouldBe(["app.log"]);
    }

    [Fact]
    public void ResultsAreOrdinallySorted()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Resolves Windows paths.");

        Seed(@"logs\c.log");
        Seed(@"logs\a.log");
        Seed(@"logs\b.log");

        var matches = Enumerator().Resolve(Path.Combine(_dir.FullName, @"logs\*.log")).Files;

        // Windows enumeration order is unspecified and differs between NTFS, ReFS and SMB.
        // Retention decides which files die, so the order must not depend on the filesystem.
        matches.Select(m => Path.GetFileName(m.Path)).ShouldBe(["a.log", "b.log", "c.log"]);
    }

    [Fact]
    public void DoubleStarCrossesDirectoriesAndPlainStarDoesNot()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Resolves Windows paths.");

        Seed(@"logs\top.log");
        Seed(@"logs\W3SVC1\a.log");
        Seed(@"logs\W3SVC1\deep\b.log");

        var shallow = Enumerator().Resolve(Path.Combine(_dir.FullName, @"logs\*.log")).Files;
        shallow.Count.ShouldBe(1);

        var deep = Enumerator().Resolve(Path.Combine(_dir.FullName, @"logs\**\*.log")).Files;
        deep.Count.ShouldBe(3);
    }

    [Fact]
    public void HiddenFilesAreStillFound()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Uses Windows file attributes.");

        Seed(@"logs\hidden.log");
        File.SetAttributes(Path.Combine(_dir.FullName, @"logs\hidden.log"), FileAttributes.Hidden);

        // .NET's EnumerationOptions defaults to skipping Hidden and System. A log we silently
        // skip is a log that grows forever, which is the worst failure this product can have.
        Enumerator().Resolve(Path.Combine(_dir.FullName, @"logs\*.log")).Files.Count.ShouldBe(1);
    }

    [Fact]
    public void AnAbsentDirectoryResolvesToNothingRatherThanThrowing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Resolves Windows paths.");
        Enumerator().Resolve(Path.Combine(_dir.FullName, @"nope\*.log")).Files.ShouldBeEmpty();
    }
}
