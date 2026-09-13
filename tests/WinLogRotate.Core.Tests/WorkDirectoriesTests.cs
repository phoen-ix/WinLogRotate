using Shouldly;
using WinLogRotate.Gui.Cli;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// When a scratch directory an elevated child wrote into may be swept.
/// </summary>
/// <remarks>
/// <c>CliRunner</c> claimed for several milestones that the next start swept
/// <c>%TEMP%\WinLogRotate-op-*</c>, and nothing did. The sweep exists now; this is the one part
/// of it that can be wrong in an interesting way, which is deleting the directory of a child that
/// is still running.
/// </remarks>
public sealed class WorkDirectoriesTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 3, 0, 0, TimeSpan.Zero);

    /// <summary>A name the pattern finds, and that two operations never share.</summary>
    [Fact]
    public void ANameMatchesThePatternAndIsUnique()
    {
        var a = WorkDirectories.Name(Guid.NewGuid());
        var b = WorkDirectories.Name(Guid.NewGuid());

        a.ShouldStartWith(WorkDirectories.Prefix);
        a.ShouldNotBe(b);
        a.ShouldNotContain(Path.DirectorySeparatorChar.ToString());
        WorkDirectories.Pattern.ShouldBe(WorkDirectories.Prefix + "*");
    }

    /// <summary>A directory written to recently belongs to a child that may still be running.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(59)]
    public void ARecentlyWrittenDirectoryIsLeftAlone(int minutesAgo) =>
        WorkDirectories.IsStale(Now.AddMinutes(-minutesAgo), Now).ShouldBeFalse();

    /// <summary>A directory nobody has written to for longer than the grace period is swept.</summary>
    [Theory]
    [InlineData(61)]
    [InlineData(60 * 24)]
    public void ADirectoryNobodyHasWrittenToIsSwept(int minutesAgo) =>
        WorkDirectories.IsStale(Now.AddMinutes(-minutesAgo), Now).ShouldBeTrue();

    /// <summary>A write stamped in the future is recent, not stale.</summary>
    /// <remarks>
    /// A clock set back would otherwise make a running child's directory look a year old.
    /// </remarks>
    [Fact]
    public void AWriteInTheFutureIsNotStale() =>
        WorkDirectories.IsStale(Now.AddDays(1), Now).ShouldBeFalse();
}
