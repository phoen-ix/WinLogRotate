using Shouldly;
using WinLogRotate.Core.Globbing;
using Xunit;

namespace WinLogRotate.Core.Tests;

public class GlobTests
{
    [Theory]
    // basics
    [InlineData("app.log", "*.log", true)]
    [InlineData("app.log", "app.*", true)]
    [InlineData("app.log", "*", true)]
    [InlineData("app.log", "app.log", true)]
    [InlineData("app.log", "*.txt", false)]
    [InlineData("app.log", "app", false)]
    // case-insensitive, as NTFS is
    [InlineData("APP.LOG", "*.log", true)]
    [InlineData("app.LOG", "APP.*", true)]
    // ?
    [InlineData("app1.log", "app?.log", true)]
    [InlineData("app12.log", "app?.log", false)]
    [InlineData("app.log", "app?.log", false)]
    // character classes - Win32 wildcards have none, and dateext retention needs them
    [InlineData("app.1.log", "app.[0-9].log", true)]
    [InlineData("app.x.log", "app.[0-9].log", false)]
    [InlineData("u_ex260907.log", "u_ex[0-9][0-9][0-9][0-9][0-9][0-9].log", true)]
    [InlineData("app.a.log", "app.[!0-9].log", true)]
    [InlineData("app.5.log", "app.[!0-9].log", false)]
    [InlineData("app-b.log", "app-[abc].log", true)]
    [InlineData("app-d.log", "app-[abc].log", false)]
    // a ']' immediately after '[' is a literal
    [InlineData("a]b", "a[]]b", true)]
    // an unterminated bracket must narrow, never widen
    [InlineData("a[b", "a[b", true)]
    [InlineData("axb", "a[b", false)]
    // pathological patterns must terminate
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaab", "*a*a*a*a*a*a*a*b", true)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaac", "*a*a*a*a*a*a*a*b", false)]
    public void NameMatching(string name, string pattern, bool expected) =>
        Glob.IsNameMatch(name, pattern).ShouldBe(expected);

    [Theory]
    [InlineData(@"C:\logs\app.log", "C:/logs/*.log", true)]
    [InlineData(@"C:\logs\app.log", @"C:\logs\*.log", true)]
    [InlineData(@"C:\logs\sub\app.log", "C:/logs/*.log", false)]      // * never crosses a separator
    [InlineData(@"C:\logs\sub\app.log", "C:/logs/**/*.log", true)]
    [InlineData(@"C:\logs\app.log", "C:/logs/**/*.log", true)]         // ** matches zero segments
    [InlineData(@"C:\logs\a\b\c\app.log", "C:/logs/**/*.log", true)]
    [InlineData(@"C:\inetpub\logs\LogFiles\W3SVC1\u_ex260907.log",
                "C:/inetpub/logs/LogFiles/**/u_ex*.log", true)]
    [InlineData(@"C:\other\app.log", "C:/logs/**/*.log", false)]
    [InlineData(@"C:\logs\a\b\app.log", "C:/logs/**/**/*.log", true)]  // collapsed ** runs
    public void PathMatching(string path, string pattern, bool expected) =>
        Glob.IsMatch(path, pattern).ShouldBe(expected);

    /// <summary>
    /// The test this whole class exists for.
    /// <para>
    /// Windows matches <c>*.log</c> against <c>something.logfile</c> via its 8.3 short name.
    /// For a tool that deletes files, that is the difference between rotating what the
    /// operator named and rotating something they did not. We must reject it - and the second
    /// half asserts the platform still behaves the way that makes our matcher necessary, so
    /// if Microsoft ever changes it we learn from a red test rather than by rediscovery.
    /// </para>
    /// </summary>
    [Fact]
    public void RejectsTheEightDotThreeOverMatch()
    {
        Glob.IsNameMatch("something.logfile", "*.log").ShouldBeFalse();
        Glob.IsNameMatch("app.log.backup", "*.log").ShouldBeFalse();
        Glob.IsNameMatch("app.logs", "*.log").ShouldBeFalse();
    }

    [Fact]
    public void ThePlatformStillOverMatches_WhichIsWhyWeDoNotUseIt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "8.3 short names are a Windows behaviour.");

        var dir = Directory.CreateTempSubdirectory("winlogrotate-glob-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "something.logfile"), "x");

            // MatchType.Win32 is the OS's own matcher. If this ever stops returning the file,
            // the 8.3 hazard is gone and this test should be revisited - not deleted silently.
            var win32 = Directory.GetFiles(dir.FullName, "*.log",
                new EnumerationOptions { MatchType = MatchType.Win32, AttributesToSkip = 0 });

            var simple = Directory.GetFiles(dir.FullName, "*.log",
                new EnumerationOptions { MatchType = MatchType.Simple, AttributesToSkip = 0 });

            // Documented rather than asserted-equal: the point is that the two disagree, and
            // that we sit on the safe side of the disagreement.
            (win32.Length >= simple.Length).ShouldBeTrue();
            simple.ShouldBeEmpty();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("C:/logs/*.log", "C:/logs")]
    [InlineData("C:/logs/**/*.log", "C:/logs")]
    [InlineData("C:/inetpub/logs/LogFiles/W3SVC1/u_ex*.log", "C:/inetpub/logs/LogFiles/W3SVC1")]
    [InlineData("C:/logs/app.log", "C:/logs")]
    public void LiteralPrefixIsTheDirectoryToEnumerateFrom(string pattern, string expected) =>
        Glob.LiteralPrefix(pattern).Replace('\\', '/').ShouldBe(expected);

    [Theory]
    [InlineData("*.log", true)]
    [InlineData("app?.log", true)]
    [InlineData("app.[0-9].log", true)]
    [InlineData("app.log", false)]
    public void WildcardDetection(string pattern, bool expected) =>
        Glob.HasWildcard(pattern).ShouldBe(expected);

    /// <summary>
    /// Which directories are worth walking into, for a pattern with a wildcard above its
    /// filename.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The decision that was missing entirely. <c>FileEnumerator</c> anchored at
    /// <see cref="Glob.LiteralPrefix"/>, which cuts at the <i>first</i> wildcard, and then
    /// descended only where the pattern contained <c>**</c>. So every documented wildcard
    /// outside the final segment matched nothing at all, for ever, in silence - the enumerator
    /// could not produce a candidate for <c>IsMatch</c> to judge.
    /// </para>
    /// <para>
    /// <c>C:/inetpub/logs/LogFiles/W3SVC[0-9]/*.log</c> is the natural IIS spelling and IIS
    /// filling disks is this product's headline case. With <c>missingok</c> the run was silent
    /// and exited 0, and <c>winlogrotate glob</c> printed "no files match", which reads as
    /// "nothing is there".
    /// </para>
    /// <para>
    /// Both answers are asserted. A predicate that always said yes would fix the defect and turn
    /// every job into a walk of the whole volume, so the false rows carry as much weight as the
    /// true ones.
    /// </para>
    /// </remarks>
    [Theory]
    // The anchor of the IIS pattern: two segments left, one of them a directory level.
    [InlineData("C:/inetpub/logs/LogFiles", "C:/inetpub/logs/LogFiles/W3SVC[0-9]/*.log", true)]

    // A site directory that matches the class. Its own files have been listed already, so there
    // is one segment left and nothing below it can match.
    [InlineData("C:/inetpub/logs/LogFiles/W3SVC1", "C:/inetpub/logs/LogFiles/W3SVC[0-9]/*.log", false)]

    // A sibling that does not match the class at all.
    [InlineData("C:/inetpub/logs/LogFiles/FTPSVC2", "C:/inetpub/logs/LogFiles/W3SVC[0-9]/*.log", false)]

    // No wildcard above the filename: * never crosses a separator, so there is nothing deeper.
    [InlineData("C:/logs", "C:/logs/*.log", false)]

    // ** is zero or more segments, so from here down everything is a candidate.
    [InlineData("C:/logs", "C:/logs/**/*.log", true)]
    [InlineData("C:/logs/a/b/c", "C:/logs/**/*.log", true)]

    // Deeper than the pattern goes, with no ** to absorb it.
    [InlineData("C:/logs/a/b", "C:/logs/*/x.log", false)]

    // Two directory levels of wildcard: worth descending at each one until the last.
    [InlineData("C:/a/x", "C:/a/*/b/*.log", true)]
    [InlineData("C:/a/x/b", "C:/a/*/b/*.log", false)]
    [InlineData("C:/a/x/c", "C:/a/*/b/*.log", false)]
    public void WorthDescendingSaysWhereAMatchCanStillBe(string directory, string pattern, bool expected) =>
        Glob.WorthDescending(directory, pattern).ShouldBe(expected);
}
