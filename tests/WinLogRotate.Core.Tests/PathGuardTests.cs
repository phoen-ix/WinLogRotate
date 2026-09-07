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
    /// Any user can create a junction with no privilege whatsoever. Following one while
    /// running as SYSTEM is how a low-privileged user borrows our token, so this refusal has
    /// no override at all - not even allowDangerous.
    /// </summary>
    [Fact]
    public void ElevatedRunsNeverFollowReparsePoints()
    {
        var guard = new PathGuard(new GuardOptions
        {
            Elevated = true,
            FollowReparsePoints = true,                    // asked for, and still refused
            AllowDangerous = [@"C:\App\logs"],             // overridden, and still refused
        });

        var decision = guard.CheckReparsePoint(@"C:\App\logs", @"C:\Windows\System32", @"C:\App");
        decision.Verdict.ShouldBe(GuardVerdict.ReparsePoint);
        decision.Remedy.ShouldNotBeNull().ShouldContain("elevated");
    }

    [Fact]
    public void UnelevatedRunsMayFollowALinkThatStaysInsideTheRoot()
    {
        var guard = new PathGuard(new GuardOptions { Elevated = false, FollowReparsePoints = true });
        guard.CheckReparsePoint(@"C:\App\logs", @"C:\App\real-logs", @"C:\App").IsAllowed.ShouldBeTrue();
        guard.CheckReparsePoint(@"C:\App\logs", @"D:\elsewhere", @"C:\App").IsAllowed.ShouldBeFalse();
    }
}
