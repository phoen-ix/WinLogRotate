using Shouldly;
using WinLogRotate.Core.Safety;
using Xunit;

namespace WinLogRotate.Core.Tests;

public class PathGuardTests
{
    /// <summary>
    /// A guard on a machine whose conf.d is safe to read an override from.
    /// </summary>
    /// <remarks>
    /// Stated, not defaulted: GuardOptions.Overrides defaults to Unknown, which refuses. That is
    /// the right default - a gate that opens when nobody asked is indistinguishable from no gate -
    /// and it means every test below that exercises an override has to say which machine it is on.
    /// </remarks>
    private static PathGuard Guard(int maxMatches = 1000) =>
        new(new GuardOptions
        {
            ProtectedRoots = [@"C:\Windows", @"C:\Windows\System32", @"C:\Program Files"],
            MaxMatches = maxMatches,
            Overrides = OverrideGate.Open,
        });

    /// <summary>One job's overrides. There is deliberately no machine-wide equivalent.</summary>
    private static GuardScope Allowing(params string[] entries) =>
        new() { AllowDangerous = entries };

    [Theory]
    [InlineData(@"C:\logs\*.log")]
    [InlineData(@"D:\apps\myapp\logs\*.log")]
    [InlineData(@"C:\inetpub\logs\LogFiles\**\u_ex*.log")]
    [InlineData(@"\\srv\share\logs\*.log")]
    public void OrdinaryPatternsAreAllowed(string pattern) =>
        Guard().CheckPattern(pattern, GuardScope.None).IsAllowed.ShouldBeTrue();

    [Theory]
    [InlineData(@"C:\Windows\*.log")]
    [InlineData(@"C:\Windows\System32\LogFiles\*.log")]
    [InlineData(@"C:\Program Files\app\*.log")]
    public void ProtectedLocationsAreRefused(string pattern)
    {
        var decision = Guard().CheckPattern(pattern, GuardScope.None);
        decision.Verdict.ShouldBe(GuardVerdict.ProtectedLocation);
        decision.Message.ShouldNotBeNullOrEmpty();
        decision.Remedy.ShouldNotBeNullOrEmpty();
    }

    // A plain StartsWith would call this protected. It is a different directory.
    [Fact]
    public void ASimilarlyNamedSiblingIsNotProtected() =>
        Guard().CheckPattern(@"C:\Program Files Custom\logs\*.log", GuardScope.None).IsAllowed.ShouldBeTrue();

    [Theory]
    [InlineData(@"C:\*.log")]
    [InlineData(@"C:\*\*.log")]
    [InlineData(@"\\srv\share\*.log")]
    public void VolumeRootsAreRefused(string pattern) =>
        Guard().CheckPattern(pattern, GuardScope.None).Verdict.ShouldBe(GuardVerdict.VolumeRoot);

    // The override is per job and leaves a mark. There is deliberately no machine-wide switch -
    // GuardOptions has no AllowDangerous at all, which is what makes that structural.
    [Fact]
    public void AnExactOverridePermitsOneProtectedPattern()
    {
        var decision = Guard().CheckPattern(
            @"C:\Windows\Logs\CBS\*.log", Allowing(@"C:\Windows\Logs\CBS\*.log"));

        decision.IsAllowed.ShouldBeTrue();
        decision.Overridden.ShouldBeTrue();
        decision.Message.ShouldNotBeNull().ShouldContain("allowDangerous");
    }

    [Fact]
    public void AnOverrideDoesNotLeakToOtherPaths() =>
        Guard().CheckPattern(@"C:\Windows\System32\*.log", Allowing(@"C:\Windows\Logs\CBS\*.log"))
            .IsAllowed.ShouldBeFalse();

    /// <summary>
    /// An override covers the archives beside the log, not only the log.
    /// </summary>
    /// <remarks>
    /// The difference between a feature that works and one that only looks wired. PlanExecutor
    /// re-checks every concrete file at the last moment before destroying it, and those files
    /// include the job's own generations. An entry of <c>...\CBS\*.log</c> glob-matches
    /// <c>CbsPersist.log</c> and does not match <c>CbsPersist.log.1</c>, so a glob-matching
    /// override would validate, plan, and then refuse every compress and delete at apply time.
    /// </remarks>
    [Theory]
    [InlineData(@"C:\Windows\Logs\CBS\CbsPersist.log")]
    [InlineData(@"C:\Windows\Logs\CBS\CbsPersist.log.1")]
    [InlineData(@"C:\Windows\Logs\CBS\CbsPersist.log.2.zip")]
    public void AnOverrideAlsoPermitsTheArchivesBesideTheLog(string path) =>
        Guard().CheckPath(path, Allowing(@"C:\Windows\Logs\CBS\*.log"))
            .IsAllowed.ShouldBeTrue();

    /// <summary>An entry naming a directory outright is judged on itself, not on its parent.</summary>
    /// <remarks>
    /// <para>
    /// Glob.LiteralPrefix treats a wildcard-free entry's last segment as a filename and hands back
    /// the parent, so an entry of <c>...\System32\LogFiles</c> would anchor at
    /// <c>...\System32</c> - and unlock the whole of it.
    /// </para>
    /// <para>
    /// The second assertion is the one that matters, and the first alone would have been a test
    /// that passes for the wrong reason: a file under LogFiles is inside System32 too, so it stays
    /// permitted either way. What the parent anchor actually breaks is the refusal of everything
    /// else in System32, which is the whole point of naming a directory.
    /// </para>
    /// </remarks>
    [Fact]
    public void AWildcardFreeEntryIsJudgedOnItselfRatherThanItsParent()
    {
        var scope = Allowing(@"C:\Windows\System32\LogFiles");

        Guard().CheckPath(@"C:\Windows\System32\LogFiles\HTTPERR\httperr1.log", scope)
            .IsAllowed.ShouldBeTrue();

        Guard().CheckPath(@"C:\Windows\System32\config\SAM", scope)
            .IsAllowed.ShouldBeFalse(
                "an entry naming one directory must not unlock everything beside it");
    }

    /// <summary>An override relaxes where, never how many.</summary>
    /// <remarks>
    /// maxfiles is the count override. One entry silently unlocking a second, unrelated rule is
    /// the "disable safety" switch this product says it does not have - and the old code reached
    /// the override lookup here by normalising a job name as a filesystem path.
    /// </remarks>
    [Fact]
    public void AnOverrideDoesNotRaiseTheMatchCeiling() =>
        Guard(maxMatches: 500)
            .CheckMatchCount(@"C:\Windows\Logs\CBS\*.log", 4_000, Allowing(@"C:\Windows\Logs\CBS"))
            .Verdict.ShouldBe(GuardVerdict.TooManyMatches);

    /// <summary>maxfiles raises the ceiling for the job that set it, and for nobody else.</summary>
    [Fact]
    public void MaxfilesRaisesTheCeilingForOneJobOnly()
    {
        var guard = Guard(maxMatches: 500);
        var job = new GuardScope { MaxMatches = 5_000 };

        guard.CheckMatchCount(@"C:\logs\*", 4_000, job).IsAllowed.ShouldBeTrue();
        guard.CheckMatchCount(@"C:\logs\*", 4_000, GuardScope.None).IsAllowed.ShouldBeFalse();

        // The ceiling actually applied, not the guard's default: telling an operator their files
        // exceed "500" while enforcing 5,000 sends them to raise a number already raised.
        guard.CheckMatchCount(@"C:\logs\*", 9_000, job)
            .Message.ShouldNotBeNull().ShouldContain("5,000");
    }

    [Fact]
    public void TooManyMatchesIsRefused()
    {
        var guard = Guard(maxMatches: 500);
        guard.CheckMatchCount(@"C:\logs\*", 499, GuardScope.None).IsAllowed.ShouldBeTrue();
        guard.CheckMatchCount(@"C:\logs\*", 500, GuardScope.None).IsAllowed.ShouldBeTrue();

        var decision = guard.CheckMatchCount(@"C:\logs\*", 1842, GuardScope.None);
        decision.Verdict.ShouldBe(GuardVerdict.TooManyMatches);
        decision.Message.ShouldNotBeNull().ShouldContain("1,842");
    }

    // ---- what an override entry may say ---------------------------------------------------

    /// <summary>A whole drive cannot be unlocked.</summary>
    /// <remarks>
    /// <c>allowdangerous = ["C:/**"]</c> was accepted, and one six-character line defeated every
    /// location rule at once - because ** matches zero or more segments, so it matched the anchor
    /// of every protected pattern on the machine. That made the claim on GuardScope.AllowDangerous
    /// that there is no "disable safety" switch straightforwardly false.
    /// </remarks>
    [Theory]
    [InlineData(@"C:\**")]
    [InlineData(@"C:\*")]
    [InlineData(@"\\srv\share\**")]
    public void AWholeDriveCannotBeUnlocked(string entry) =>
        Guard().CheckOverrideEntry(entry).Verdict.ShouldBe(GuardVerdict.VolumeRoot);

    /// <summary>An entry may sit below a protected location; it may not be one.</summary>
    [Theory]
    [InlineData(@"C:\Windows\**")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Windows\*.log")]
    public void AnEntryThatUnlocksAWholeProtectedRootIsRefused(string entry) =>
        Guard().CheckOverrideEntry(entry).Verdict.ShouldBe(GuardVerdict.ProtectedLocation);

    /// <summary>A narrow entry inside a protected root is exactly what the feature is for.</summary>
    /// <remarks>
    /// The direction a breadth rule gets wrong by becoming "refuse everything" - which is what
    /// the dead CheckReparsePoint did, and why milestone 15 had to reason about it rather than
    /// simply wire it up.
    /// </remarks>
    [Theory]
    [InlineData(@"C:\Windows\System32\LogFiles\*.log")]
    [InlineData(@"C:\Windows\System32\LogFiles")]
    [InlineData(@"C:\Windows\Logs\CBS\*.log")]
    [InlineData(@"D:\logs\app\*.log")]
    public void ANarrowEntryIsAccepted(string entry) =>
        Guard().CheckOverrideEntry(entry).IsAllowed.ShouldBeTrue();

    /// <summary>
    /// The remedy names something the guard would actually accept.
    /// </summary>
    /// <remarks>
    /// It used to say "add the exact pattern to allowDangerous" - advice to type a key that
    /// reached nothing at all. Now that the key works, advice that would be rejected as too broad
    /// would be no better, so the loop is closed here rather than trusted.
    /// </remarks>
    [Fact]
    public void TheRemedyNamesAnEntryTheGuardWouldAccept()
    {
        var remedy = Guard()
            .CheckPattern(@"C:\Windows\Logs\CBS\*.log", GuardScope.None)
            .Remedy.ShouldNotBeNull();

        var quoted = remedy.Split('"');
        quoted.Length.ShouldBeGreaterThan(1, "the remedy has to name an entry, not describe one");

        Guard().CheckOverrideEntry(quoted[1]).IsAllowed.ShouldBeTrue();

        // And it really does unlock the pattern it was suggested for.
        Guard().CheckPattern(@"C:\Windows\Logs\CBS\*.log", Allowing(quoted[1]))
            .IsAllowed.ShouldBeTrue();
    }

    /// <summary>A pattern anchored at a protected root is told no entry can help.</summary>
    /// <remarks>
    /// Proposing an impossible fix is worse than proposing none: the operator writes it, the
    /// entry is refused as too broad, and they have two diagnostics and no way forward.
    /// </remarks>
    [Fact]
    public void APatternAnchoredAtAProtectedRootIsToldNoEntryCanHelp()
    {
        var remedy = Guard()
            .CheckPattern(@"C:\Windows\*.log", GuardScope.None)
            .Remedy.ShouldNotBeNull();

        remedy.ShouldContain("no allowdangerous entry can permit it");
        remedy.ShouldNotContain("add \"");
    }

    /// <summary>
    /// An override written in a directory the machine does not trust is not honoured.
    /// </summary>
    /// <remarks>
    /// LR9001 has always told operators that a writable conf.d refuses "all hooks and all
    /// dangerous-path overrides". Hooks honoured that; overrides had nothing to honour, so a local
    /// user who could write one file in conf.d could have a SYSTEM-privileged process delete from
    /// a protected location. That is the escalation LR9001 exists to describe, arriving through
    /// the door LR9001 claimed was shut.
    /// </remarks>
    [Fact]
    public void AnOverrideIsNotHonouredWhenTheGateIsShut()
    {
        var guard = new PathGuard(new GuardOptions
        {
            ProtectedRoots = [@"C:\Windows"],
            Overrides = OverrideGate.Shut("conf.d can be written by an ordinary user", "icacls ..."),
        });

        var decision = guard.CheckPattern(
            @"C:\Windows\Logs\CBS\*.log", Allowing(@"C:\Windows\Logs\CBS"));

        decision.IsAllowed.ShouldBeFalse();

        // Said out loud. Otherwise the operator reads a refusal, looks at the entry they already
        // wrote, and cannot tell that it was read and discarded rather than never noticed.
        decision.Message.ShouldNotBeNull()
            .ShouldContain("conf.d can be written by an ordinary user");
        decision.Remedy.ShouldBe("icacls ...");
    }

    /// <summary>An unasked gate refuses, rather than quietly permitting.</summary>
    [Fact]
    public void AnOverrideIsNotHonouredWhenNobodyEstablishedTheVerdict()
    {
        var guard = new PathGuard(new GuardOptions { ProtectedRoots = [@"C:\Windows"] });

        guard.CheckPattern(@"C:\Windows\Logs\CBS\*.log", Allowing(@"C:\Windows\Logs\CBS"))
            .IsAllowed.ShouldBeFalse("GuardOptions.Overrides defaults to Unknown, which refuses");
    }

    [Fact]
    public void MalformedPatternsAreRefusedBeforeAnyFilesystemAccess()
    {
        Guard().CheckPattern(@"C:logs\*.log", GuardScope.None).Verdict.ShouldBe(GuardVerdict.InvalidPath);
        Guard().CheckPattern(@"C:\logs\..\..\Windows\*.log", GuardScope.None).Verdict.ShouldBe(GuardVerdict.InvalidPath);
    }

    /// <summary>
    /// A link to an ordinary directory is followed.
    /// </summary>
    /// <remarks>
    /// Relocating a log directory onto another volume when a system drive fills up is ordinary
    /// practice, and this is the case the old rule got wrong in both directions: it refused every
    /// link once elevated, and the check it belonged to was never called at all. The elevation
    /// half is now unrepresentable - GuardOptions.Elevated is gone, so no rule can consult it.
    /// </remarks>
    [Fact]
    public void ALinkToAnOrdinaryDirectoryIsFollowed()
    {
        var guard = new PathGuard(new GuardOptions
        {
            ProtectedRoots = [@"C:\Windows", @"C:\Program Files"],
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

    /// <summary>
    /// A link refusal has no override at all - not even allowdangerous.
    /// </summary>
    /// <remarks>
    /// An override lives in conf.d, whose whole LR9001 machinery exists because a
    /// non-administrator may be able to write there. An override a local user can author would be
    /// the escalation rather than the fix.
    /// <para>
    /// This now holds by construction rather than by coincidence: CheckLinkTarget takes no
    /// GuardScope, so there is no override to hand it. It used to pass only because one method
    /// happened not to consult a field that was in scope - the shape of the defect milestone 15
    /// fixed. TheLinkChecksCannotBeHandedAnOverride in ArchitectureTests keeps it that way.
    /// </para>
    /// </remarks>
    [Fact]
    public void ALinkRefusalIsNotOverridable()
    {
        var guard = new PathGuard(new GuardOptions { ProtectedRoots = [@"C:\Windows"] });

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
