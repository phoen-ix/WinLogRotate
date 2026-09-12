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

    /// <summary>
    /// A wildcard in a directory position matches the directories it names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The end of the defect, asserted through the real enumerator rather than through the
    /// predicate that decides it. <c>docs/configuration.md</c> has documented <c>*</c>, <c>?</c>,
    /// <c>**</c> and <c>[abc]</c> since the glob was written, and <c>Glob.IsMatch</c> implements
    /// all four faithfully - but the enumerator could not produce a candidate for it to judge
    /// unless the wildcard was in the final segment.
    /// </para>
    /// <para>
    /// This is the IIS spelling, and IIS filling disks is the case this product exists for.
    /// </para>
    /// <para>
    /// The site that does not match the class is seeded too. Without it a predicate that
    /// descended into everything would pass, and the fix would be "walk the whole volume".
    /// </para>
    /// </remarks>
    [Fact]
    public void AWildcardInADirectoryPositionMatchesTheDirectoriesItNames()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Resolves Windows paths.");

        Seed(@"LogFiles\W3SVC1\u_ex260912.log");
        Seed(@"LogFiles\W3SVC2\u_ex260912.log");
        Seed(@"LogFiles\FTPSVC3\u_ex260912.log");
        Seed(@"LogFiles\W3SVC1\notes.txt");

        var matches = Enumerator()
            .Resolve(Path.Combine(_dir.FullName, @"LogFiles\W3SVC[0-9]\*.log"))
            .Files;

        matches
            .Select(m => Path.GetFileName(Path.GetDirectoryName(m.Path))!)
            .ShouldBe(["W3SVC1", "W3SVC2"]);
    }

    /// <summary>
    /// The walk still stops where the pattern cannot reach.
    /// </summary>
    /// <remarks>
    /// The other half of the same change, and the one that keeps it from being "enumerate
    /// everything and filter". A single <c>*</c> never crosses a separator, so a file one level
    /// below the pattern's last directory is not a match and the directory holding it is never
    /// entered.
    /// </remarks>
    [Fact]
    public void ASingleWildcardStillDoesNotCrossASeparator()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Resolves Windows paths.");

        Seed(@"LogFiles\W3SVC1\u_ex260912.log");
        Seed(@"LogFiles\W3SVC1\archive\u_ex260911.log");

        var matches = Enumerator()
            .Resolve(Path.Combine(_dir.FullName, @"LogFiles\W3SVC[0-9]\*.log"))
            .Files;

        matches.Select(m => Path.GetFileName(m.Path)).ShouldBe(["u_ex260912.log"]);
    }
}
