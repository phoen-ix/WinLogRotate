using Shouldly;
using WinLogRotate.Core.Safety;
using Xunit;

namespace WinLogRotate.Core.Tests;

public class WinPathTests
{
    [Theory]
    [InlineData(@"C:\logs\app.log", @"C:\logs\app.log")]
    [InlineData("C:/logs/app.log", @"C:\logs\app.log")]          // operators paste Linux-style paths
    [InlineData(@"C:\logs\\app.log", @"C:\logs\app.log")]
    [InlineData(@"C:\logs\", @"C:\logs")]                        // trailing separator dropped
    [InlineData(@"  C:\logs\app.log  ", @"C:\logs\app.log")]
    [InlineData(@"\\srv\share\logs", @"\\srv\share\logs")]       // UNC keeps its leading pair
    public void Normalization(string input, string expected) =>
        WinPath.Normalize(input).ShouldBe(expected);

    // Without a canonical key these are two state entries with independent rotation clocks,
    // so the same file rotates twice as often as configured.
    [Fact]
    public void CaseVariantsShareOneStateKey() =>
        WinPath.CanonicalKey(@"C:\Logs\App.log")
            .ShouldBe(WinPath.CanonicalKey(@"c:\logs\app.log"));

    [Fact]
    public void SeparatorVariantsShareOneStateKey() =>
        WinPath.CanonicalKey("C:/logs/app.log")
            .ShouldBe(WinPath.CanonicalKey(@"C:\logs\app.log"));

    [Theory]
    [InlineData(@"C:\logs\app.log", PathProblem.None)]
    [InlineData(@"\\srv\share\logs\app.log", PathProblem.None)]
    [InlineData("", PathProblem.Empty)]
    [InlineData("   ", PathProblem.Empty)]
    [InlineData(@"logs\app.log", PathProblem.NotAbsolute)]
    [InlineData(@"C:logs\app.log", PathProblem.NotAbsolute)]     // drive-relative
    [InlineData(@"C:\logs\NUL", PathProblem.ReservedName)]
    [InlineData(@"C:\logs\NUL.log", PathProblem.ReservedName)]    // extension does not help
    [InlineData(@"C:\logs\COM1.txt", PathProblem.ReservedName)]
    [InlineData(@"C:\logs\app.log.", PathProblem.TrailingDotOrSpace)]
    [InlineData(@"C:\logs \app.log", PathProblem.TrailingDotOrSpace)]
    [InlineData(@"C:\logs\app.log:hidden", PathProblem.AlternateDataStream)]
    [InlineData(@"C:\logs\..\..\Windows\app.log", PathProblem.ParentTraversal)]
    [InlineData(@"C:\logs\a<b.log", PathProblem.InvalidCharacter)]
    public void Validation(string path, PathProblem expected) =>
        WinPath.Validate(path).ShouldBe(expected);

    /// <summary>
    /// Trailing whitespace around the whole value is operator noise and is trimmed; trailing
    /// whitespace on a segment is a hazard and is rejected.
    /// <para>
    /// The distinction is not pedantry. Win32 silently strips a trailing space, so
    /// <c>C:\logs\app.log </c> opens <c>app.log</c> - but a <c>\\?\</c> path does not strip,
    /// and we use <c>\\?\</c> for every destructive operation. A segment that ends in a space
    /// therefore names one file to the API that lists it and a different one to the API that
    /// deletes it, which is precisely the confusion this tool must never have.
    /// </para>
    /// </summary>
    [Fact]
    public void OuterWhitespaceIsTrimmedButSegmentWhitespaceIsRejected()
    {
        WinPath.Validate(@"C:\logs\app.log ").ShouldBe(PathProblem.None);
        WinPath.Normalize(@"C:\logs\app.log ").ShouldBe(@"C:\logs\app.log");

        WinPath.Validate(@"C:\logs \app.log").ShouldBe(PathProblem.TrailingDotOrSpace);
    }

    [Fact]
    public void WildcardsAreInvalidInAPathAndValidInAPattern()
    {
        WinPath.Validate(@"C:\logs\*.log").ShouldBe(PathProblem.InvalidCharacter);
        WinPath.Validate(@"C:\logs\*.log", allowWildcards: true).ShouldBe(PathProblem.None);
    }

    [Theory]
    [InlineData(@"C:\", true)]
    [InlineData("C:", true)]
    [InlineData(@"\\srv\share", true)]
    [InlineData(@"C:\logs", false)]
    [InlineData(@"\\srv\share\logs", false)]
    public void RootDetection(string path, bool expected) =>
        WinPath.IsRoot(path).ShouldBe(expected);

    [Theory]
    [InlineData(@"C:\logs\app.log", @"\\?\C:\logs\app.log")]
    [InlineData(@"\\srv\share\app.log", @"\\?\UNC\srv\share\app.log")]
    [InlineData(@"\\?\C:\already", @"\\?\C:\already")]
    public void ExtendedLengthPrefixing(string input, string expected) =>
        WinPath.ToExtendedLength(WinPath.Normalize(input)).ShouldBe(expected);

    /// <summary>
    /// <c>FromExtendedLength</c> really inverts <c>ToExtendedLength</c>.
    /// </summary>
    /// <remarks>
    /// The UNC case is the one that bites. <c>\\?\UNC\</c> replaces the two leading backslashes of
    /// <c>\\server\share</c>, so undoing it by the wrong offset yields <c>\server\share</c> - not
    /// UNC, not absolute, and equal to nothing. A guard handed that quietly stops matching the
    /// root it was written to protect, and every test that does not use a network path stays green.
    /// </remarks>
    [Theory]
    [InlineData(@"C:\logs\app")]
    [InlineData(@"C:\")]
    [InlineData(@"\\server\share\logs")]
    [InlineData(@"\\server\share")]
    public void ExtendedLengthRoundTrips(string path) =>
        WinPath.FromExtendedLength(WinPath.ToExtendedLength(path)).ShouldBe(path);

    [Fact]
    public void FromExtendedLengthStripsThePrefixItIsGiven()
    {
        WinPath.FromExtendedLength(@"\\?\C:\logs").ShouldBe(@"C:\logs");
        WinPath.FromExtendedLength(@"\\?\UNC\srv\share\logs").ShouldBe(@"\\srv\share\logs");
    }

    /// <summary>What it cannot shorten, it leaves alone.</summary>
    /// <remarks>
    /// A volume GUID path is what the final-path call answers for a mounted folder with no drive
    /// letter. There is no shorter spelling, and inventing one would name a volume that does not
    /// exist.
    /// </remarks>
    [Fact]
    public void FromExtendedLengthLeavesWhatItCannotShorten()
    {
        WinPath.FromExtendedLength(@"C:\logs").ShouldBe(@"C:\logs");

        const string volume = @"\\?\Volume{b75e2c83-0000-0000-0000-602f00000000}\logs";
        WinPath.FromExtendedLength(volume).ShouldBe(volume);
    }
}
