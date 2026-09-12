using Shouldly;
using WinLogRotate.Core.Globbing;
using WinLogRotate.Core.Safety;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// One location, several spellings, one answer from the guard.
/// </summary>
/// <remarks>
/// <para>
/// The protected-root rule is a textual comparison, and Windows accepts more than one spelling
/// of the same directory. <c>\\?\C:\Windows\System32</c> and <c>C:\Windows\System32</c> are the
/// same place; the guard said no to the second and yes to the first. `README.md` and
/// `docs/configuration.md` both promise this product refuses to delete inside Windows, System32
/// and Program Files.
/// </para>
/// <para>
/// The prefix is not exotic. .NET emits it internally for long paths, and an operator who has hit
/// MAX_PATH has very likely been told to write it.
/// </para>
/// <para>
/// Everything here is arithmetic over strings, so all of it runs on both legs - which matters,
/// because the rule it defends is one only a Windows machine can ever be hurt by.
/// </para>
/// </remarks>
public sealed class ExtendedPathGuardTests
{
    private static PathGuard Guard() => new(new GuardOptions
    {
        ProtectedRoots = [@"C:\Windows", @"C:\Program Files"],
    });

    /// <summary>
    /// A protected location is protected however it is spelled.
    /// </summary>
    /// <remarks>
    /// The plain row is the control: it passed before and it passes now, so a failure here is
    /// about the prefix rather than about the rule having been broken outright.
    /// </remarks>
    [Theory]
    [InlineData(@"C:\Windows\System32\archive")]
    [InlineData(@"\\?\C:\Windows\System32\archive")]
    [InlineData(@"\\?\c:\windows\system32\archive")]
    public void AProtectedLocationIsProtectedHoweverItIsSpelled(string path) =>
        Guard().CheckPath(path, new GuardScope()).IsAllowed.ShouldBeFalse();

    /// <summary>The same, for the pattern a job actually configures.</summary>
    [Theory]
    [InlineData(@"C:\Windows\System32\*.log")]
    [InlineData(@"\\?\C:\Windows\System32\*.log")]
    public void AProtectedPatternIsRefusedHoweverItIsSpelled(string pattern) =>
        Guard().CheckPattern(pattern, new GuardScope())
            .Verdict.ShouldBe(GuardVerdict.ProtectedLocation);

    /// <summary>
    /// A pattern anchored at a whole volume is refused, prefix or no prefix.
    /// </summary>
    /// <remarks>
    /// Two defects met here. <c>IsRoot</c> compared the prefixed spelling, and
    /// <c>Glob.LiteralPrefix</c> scanned the <c>?</c> of <c>\\?\</c> as a wildcard - so the
    /// anchor came back as a single backslash and every rule downstream was measured against the
    /// wrong directory.
    /// </remarks>
    [Theory]
    [InlineData(@"C:\*.log")]
    [InlineData(@"\\?\C:\*.log")]
    public void AVolumeRootIsRefusedHoweverItIsSpelled(string pattern) =>
        Guard().CheckPattern(pattern, new GuardScope())
            .Verdict.ShouldBe(GuardVerdict.VolumeRoot);

    /// <summary>
    /// The anchor of an extended-length pattern is the directory, not a backslash.
    /// </summary>
    /// <remarks>
    /// Asserted directly as well as through the guard, because this is also what
    /// <c>FileEnumerator</c> walks from: anchored at <c>\</c> it would have enumerated the root
    /// of the current drive for a pattern naming one directory. The prefix is kept, because it
    /// is what makes a path past MAX_PATH work and this string is handed to the OS.
    /// </remarks>
    [Fact]
    public void AnExtendedLengthPatternAnchorsAtItsDirectory()
    {
        Glob.LiteralPrefix(@"\\?\C:\logs\app\*.log").ShouldBe(@"\\?\C:\logs\app");
        Glob.LiteralPrefix(@"C:\logs\app\*.log").ShouldBe(@"C:\logs\app");

        // A genuine wildcard after the prefix is still found.
        Glob.LiteralPrefix(@"\\?\C:\logs\app?\x.log").ShouldBe(@"\\?\C:\logs");
    }

    /// <summary>
    /// A path that names a device is refused rather than reasoned about.
    /// </summary>
    /// <remarks>
    /// <c>\\?\GLOBALROOT\Device\HarddiskVolume1\Windows\System32</c> and
    /// <c>\\.\C:\Windows\System32</c> both reach the files the protected-root rule exists to keep
    /// this product out of, and neither can be compared against <c>C:\Windows</c> without asking
    /// the operating system where the device points. The guard is biased toward refusing, and
    /// nothing legitimate spells a log directory this way.
    /// </remarks>
    [Theory]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume1\Windows\System32\archive")]
    [InlineData(@"\\.\C:\Windows\System32\archive")]
    [InlineData(@"\\?\Volume{11111111-2222-3333-4444-555555555555}\logs")]
    public void ADevicePathIsRefusedRatherThanReasonedAbout(string path)
    {
        WinPath.Validate(path).ShouldBe(PathProblem.DeviceNamespace);

        var decision = Guard().CheckPath(path, new GuardScope());

        decision.IsAllowed.ShouldBeFalse();
        decision.Verdict.ShouldBe(GuardVerdict.InvalidPath);
    }

    /// <summary>
    /// An ordinary long path is still an ordinary path.
    /// </summary>
    /// <remarks>
    /// The other half, and the one that keeps the fix from being "refuse anything unusual". The
    /// prefix exists to make paths past MAX_PATH work, and a job that uses it on a directory
    /// nobody protects must go on working - including the UNC spelling, which unwraps to a share
    /// rather than to a drive.
    /// </remarks>
    [Theory]
    [InlineData(@"\\?\D:\logs\app\*.log")]
    [InlineData(@"\\?\UNC\fileserver\logs$\app\*.log")]
    public void AnExtendedLengthPathSomewhereOrdinaryIsStillAllowed(string pattern) =>
        Guard().CheckPattern(pattern, new GuardScope()).IsAllowed.ShouldBeTrue();

    /// <summary>Unwrapping names the same location, in the spelling everything else uses.</summary>
    [Theory]
    [InlineData(@"\\?\C:\logs", @"C:\logs")]
    [InlineData(@"\\?\UNC\server\share\logs", @"\\server\share\logs")]
    [InlineData(@"C:\logs", @"C:\logs")]
    [InlineData(@"\\server\share\logs", @"\\server\share\logs")]
    public void UnprefixedNamesTheSameLocation(string path, string expected) =>
        WinPath.Unprefixed(path).ShouldBe(expected);
}
