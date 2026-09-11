using Shouldly;
using WinLogRotate.Core.Safety;
using Xunit;

namespace WinLogRotate.Core.Tests;

public class PathGuardTests
{
    private static PathGuard Guard(
        IReadOnlyList<string>? allow = null, int maxMatches = 1000, bool elevated = false) =>
        new(new GuardOptions
        {
            ProtectedRoots = [@"C:\Windows", @"C:\Windows\System32", @"C:\Program Files"],
            AllowDangerous = allow ?? [],
            MaxMatches = maxMatches,
            Elevated = elevated,
        });

    [Theory]
    [InlineData(@"C:\logs\*.log")]
    [InlineData(@"D:\apps\myapp\logs\*.log")]
    [InlineData(@"C:\inetpub\logs\LogFiles\**\u_ex*.log")]
    [InlineData(@"\\srv\share\logs\*.log")]
    public void OrdinaryPatternsAreAllowed(string pattern) =>
        Guard().CheckPattern(pattern).IsAllowed.ShouldBeTrue();

    [Theory]
    [InlineData(@"C:\Windows\*.log")]
    [InlineData(@"C:\Windows\System32\LogFiles\*.log")]
    [InlineData(@"C:\Program Files\app\*.log")]
    public void ProtectedLocationsAreRefused(string pattern)
    {
        var decision = Guard().CheckPattern(pattern);
        decision.Verdict.ShouldBe(GuardVerdict.ProtectedLocation);
        decision.Message.ShouldNotBeNullOrEmpty();
        decision.Remedy.ShouldNotBeNullOrEmpty();
    }

    // A plain StartsWith would call this protected. It is a different directory.
    [Fact]
    public void ASimilarlyNamedSiblingIsNotProtected() =>
        Guard().CheckPattern(@"C:\Program Files Custom\logs\*.log").IsAllowed.ShouldBeTrue();

    [Theory]
    [InlineData(@"C:\*.log")]
    [InlineData(@"C:\*\*.log")]
    [InlineData(@"\\srv\share\*.log")]
    public void VolumeRootsAreRefused(string pattern) =>
        Guard().CheckPattern(pattern).Verdict.ShouldBe(GuardVerdict.VolumeRoot);

    // The override is per-pattern and leaves a mark. There is deliberately no global switch.
    [Fact]
    public void AnExactOverridePermitsOneProtectedPattern()
    {
        var decision = Guard(allow: [@"C:\Windows\Logs\CBS\*.log"])
            .CheckPattern(@"C:\Windows\Logs\CBS\*.log");

        decision.IsAllowed.ShouldBeTrue();
        decision.Overridden.ShouldBeTrue();
        decision.Message.ShouldNotBeNull().ShouldContain("allowDangerous");
    }

    [Fact]
    public void AnOverrideDoesNotLeakToOtherPaths()
    {
        var guard = Guard(allow: [@"C:\Windows\Logs\CBS\*.log"]);
        guard.CheckPattern(@"C:\Windows\System32\*.log").IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public void TooManyMatchesIsRefused()
    {
        var guard = Guard(maxMatches: 500);
        guard.CheckMatchCount(@"C:\logs\*", 499).IsAllowed.ShouldBeTrue();
        guard.CheckMatchCount(@"C:\logs\*", 500).IsAllowed.ShouldBeTrue();

        var decision = guard.CheckMatchCount(@"C:\logs\*", 1842);
        decision.Verdict.ShouldBe(GuardVerdict.TooManyMatches);
        decision.Message.ShouldNotBeNull().ShouldContain("1,842");
    }

    [Fact]
    public void MalformedPatternsAreRefusedBeforeAnyFilesystemAccess()
    {
        Guard().CheckPattern(@"C:logs\*.log").Verdict.ShouldBe(GuardVerdict.InvalidPath);
        Guard().CheckPattern(@"C:\logs\..\..\Windows\*.log").Verdict.ShouldBe(GuardVerdict.InvalidPath);
    }

    /// <summary>
    /// A link to an ordinary directory is followed, even while elevated.
    /// </summary>
    /// <remarks>
    /// Relocating a log directory onto another volume when a system drive fills up is ordinary
    /// practice, and this is the case the old rule got wrong in both directions: it refused every
    /// link once elevated, and the check it belonged to was never called at all.
    /// </remarks>
    [Fact]
    public void ALinkToAnOrdinaryDirectoryIsFollowedEvenWhileElevated()
    {
        var guard = new PathGuard(new GuardOptions
        {
            ProtectedRoots = [@"C:\Windows", @"C:\Program Files"],
            Elevated = true,
        });

        guard.CheckLinkTarget(@"C:\inetpub\logs", @"D:\logs\iis").IsAllowed.ShouldBeTrue();
    }

    /// <summary>
    /// A link into a protected location is refused, and says where it was aimed.
    /// </summary>
    /// <remarks>
    /// Any user can create a junction with no privilege whatsoever, so following one while running
    /// as SYSTEM is how a low-privileged user borrows this process's token.
    /// </remarks>
    [Fact]
    public void ALinkIntoAProtectedLocationIsRefusedAndNamesBothPaths()
    {
        var decision = Guard().CheckLinkTarget(@"C:\App\logs", @"C:\Windows\System32");

        decision.Verdict.ShouldBe(GuardVerdict.ReparsePoint);

        var message = decision.Message.ShouldNotBeNull();
        message.ShouldContain(@"C:\App\logs");
        message.ShouldContain(@"C:\Windows\System32",
            customMessage: "a refusal that does not say what was aimed where is unactionable");
    }

    /// <summary>A link to a whole volume is refused too.</summary>
    /// <remarks>
    /// It is not inside a protected root, so only the volume-root half of the location rule
    /// catches it - and without that half a junction to <c>D:\</c> makes one pattern walk a drive.
    /// </remarks>
    [Fact]
    public void ALinkToAVolumeRootIsRefused()
    {
        var decision = Guard().CheckLinkTarget(@"C:\App\logs", @"D:\");

        decision.Verdict.ShouldBe(GuardVerdict.ReparsePoint);
        decision.Message.ShouldNotBeNull().ShouldContain("volume root");
    }

    /// <summary>The refusal is identical whether or not this process is elevated.</summary>
    /// <remarks>
    /// One condition arriving at two severities is how an alert rule quietly stops matching, and
    /// the junction is the same junction either way.
    /// </remarks>
    [Fact]
    public void ALinkRefusalDoesNotSoftenWhenUnelevated()
    {
        GuardDecision For(bool elevated) =>
            new PathGuard(new GuardOptions
            {
                ProtectedRoots = [@"C:\Windows"],
                Elevated = elevated,
            }).CheckLinkTarget(@"C:\App\logs", @"C:\Windows\System32");

        For(elevated: true).Verdict.ShouldBe(For(elevated: false).Verdict);
    }

    /// <summary>
    /// A link refusal has no override at all - not even allowdangerous.
    /// </summary>
    /// <remarks>
    /// An override lives in conf.d, whose whole LR9001 machinery exists because a
    /// non-administrator may be able to write there. An override a local user can author would be
    /// the escalation rather than the fix.
    /// </remarks>
    [Fact]
    public void ALinkRefusalIsNotOverridable()
    {
        var guard = new PathGuard(new GuardOptions
        {
            ProtectedRoots = [@"C:\Windows"],
            AllowDangerous = [@"C:\App\logs", @"C:\Windows\System32"],
        });

        guard.CheckLinkTarget(@"C:\App\logs", @"C:\Windows\System32").IsAllowed.ShouldBeFalse();
    }

    /// <summary>A link we could not follow is reported as unproven, not as an accusation.</summary>
    [Fact]
    public void AnUnresolvableLinkIsUnverifiableRatherThanRefused()
    {
        var decision = Guard().UnresolvableLink(
            @"C:\App\logs", "the network path was not found", 53);

        decision.Verdict.ShouldBe(GuardVerdict.Unverifiable);
        decision.NativeError.ShouldBe(53);
        decision.Message.ShouldNotBeNull().ShouldContain("network path was not found");
    }
}
