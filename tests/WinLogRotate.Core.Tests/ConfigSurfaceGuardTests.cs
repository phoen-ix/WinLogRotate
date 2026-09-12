using Shouldly;
using WinLogRotate.Hosting.Security;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// The gate that decides whether a hook may run, judged without a security descriptor in sight.
/// </summary>
/// <remarks>
/// <para>
/// The rule that stops a local user handing themselves SYSTEM lived inside a
/// <c>[SupportedOSPlatform("windows")]</c> class, welded to <c>GetAccessControl</c>. It could
/// therefore only ever be executed by windows-2025, which is why it stayed a
/// <i>directory</i> check for six milestones while the thing actually executed as SYSTEM was a
/// <i>file</i> - and why the four ordinary ways reading a descriptor can fail were never
/// considered at all.
/// </para>
/// <para>
/// <see cref="AclJudgement"/> and <c>ConfigSurfaceGuard</c> are the arithmetic, with the syscall
/// behind a delegate, so all of it runs on both legs. The projection from a real
/// <c>FileSecurity</c> into a <c>Subject</c> is still windows-only, and
/// <c>ConfDirHardeningTests</c> is still the only judge of it.
/// </para>
/// </remarks>
public sealed class ConfigSurfaceGuardTests
{
    private const string Everyone = "S-1-1-0";
    private const string Administrators = "S-1-5-32-544";
    private const string LocalSystem = "S-1-5-18";
    private const string LocalUser = "S-1-5-21-1-2-3-1001";

    private static AclJudgement.Subject Clean(string path, bool isDirectory = true) => new(
        path,
        isDirectory,
        Administrators,
        "BUILTIN\\Administrators",
        [
            new AclJudgement.Ace(LocalSystem, 0x1F01FF, "NT AUTHORITY\\SYSTEM : FullControl"),
            new AclJudgement.Ace(Administrators, 0x1F01FF, "BUILTIN\\Administrators : FullControl"),
            new AclJudgement.Ace("S-1-5-32-545", 0x1200A9, "BUILTIN\\Users : ReadAndExecute"),
        ],
        Protected: true);

    /// <summary>Answers from a dictionary, and null for anything it was not given.</summary>
    private static ConfigSurfaceGuard.Descriptor Reading(
        params (string Path, AclJudgement.Subject? Subject)[] known) =>
        (path, _) => known.FirstOrDefault(k => k.Path == path).Subject;

    private static AclFinding Verify(
        ConfigSurfaceGuard.Descriptor read, params (string Path, bool IsDirectory)[] surface) =>
        ConfigSurfaceGuard.Verify(surface, ConfigSurfaceGuard.Trusted(null), read);

    /// <summary>A hardened directory permits hooks. The floor under everything below.</summary>
    [Fact]
    public void AHardenedDirectoryPermitsHooks()
    {
        var finding = Verify(Reading((@"C:\pd\conf.d", Clean(@"C:\pd\conf.d"))), (@"C:\pd\conf.d", true));

        finding.Verdict.ShouldBe(AclVerdict.Hardened);
        finding.HooksAllowed.ShouldBeTrue();
    }

    /// <summary>
    /// A descriptor that cannot be read refuses the run rather than crashing it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>GetAccessControl</c> throws for four ordinary reasons on a live installation - the
    /// path was replaced between being enumerated and being read, which is what an editor's
    /// save looks like; it became unreachable; the descriptor needs a privilege this process
    /// does not hold; or the account may not read it, which is what a correctly protected path
    /// looks like from an unelevated one. Every one of those escaped to
    /// <c>CommandContext.Guarded</c> and came back as <c>LR1006</c>, exit 4, <i>"This is a
    /// defect"</i>, about a machine that was merely busy.
    /// </para>
    /// <para>
    /// It is <see cref="AclVerdict.Unknown"/> now, which refuses: the guard is biased toward
    /// refusing, and a hook is worth less than a wrong "yes". <c>Unknown</c> has been documented
    /// as "checked, and the answer could not be established" since it was written and was
    /// reachable only when the directory did not exist.
    /// </para>
    /// </remarks>
    [Fact]
    public void ADescriptorThatCannotBeReadRefusesTheRun()
    {
        var finding = Verify(Reading(), (@"C:\pd\conf.d", true));

        finding.Verdict.ShouldBe(AclVerdict.Unknown);
        finding.HooksAllowed.ShouldBeFalse();
        finding.Path.ShouldBe(@"C:\pd\conf.d");
        finding.FixCommand.ShouldNotBeNull();
    }

    /// <summary>An owner who is not an administrator is a write grant wearing a disguise.</summary>
    [Fact]
    public void ANonAdministratorOwnerRefusesTheRun()
    {
        var subject = Clean(@"C:\pd\conf.d") with { OwnerSid = LocalUser, OwnerDescribe = "CONTOSO\\dana" };

        var finding = Verify(Reading((@"C:\pd\conf.d", subject)), (@"C:\pd\conf.d", true));

        finding.Verdict.ShouldBe(AclVerdict.LooseOwner);
        finding.Explanation.ShouldNotBeNull().ShouldContain("CONTOSO\\dana");
    }

    /// <summary>
    /// A generic-rights grant to Everyone is seen, which is the whole of the arithmetic this
    /// seam exists to expose to a test.
    /// </summary>
    [Fact]
    public void AWorldWritableDirectoryRefusesTheRun()
    {
        var subject = Clean(@"C:\pd\conf.d");
        var loose = subject with
        {
            Allow = [.. subject.Allow, new AclJudgement.Ace(Everyone, 0x10000000, "Everyone : GenericAll")],
        };

        var finding = Verify(Reading((@"C:\pd\conf.d", loose)), (@"C:\pd\conf.d", true));

        finding.Verdict.ShouldBe(AclVerdict.LooseWritable);
        finding.OffendingAces.ShouldHaveSingleItem().ShouldBe("Everyone : GenericAll");
    }

    /// <summary>
    /// The outermost refusal is the one reported, however many there are further in.
    /// </summary>
    /// <remarks>
    /// Not a severity ranking. Repairing a job file inside a directory a local user can write
    /// repairs nothing, so the container has to be the answer given first or the operator fixes
    /// the wrong thing and is told it is still broken.
    /// </remarks>
    [Fact]
    public void TheOutermostRefusalIsTheOneReported()
    {
        var loose = Clean(@"C:\pd") with { OwnerSid = LocalUser, OwnerDescribe = "CONTOSO\\dana" };
        var alsoLoose = Clean(@"C:\pd\conf.d") with { OwnerSid = LocalUser, OwnerDescribe = "CONTOSO\\dana" };

        var finding = Verify(
            Reading((@"C:\pd", loose), (@"C:\pd\conf.d", alsoLoose)),
            (@"C:\pd", true),
            (@"C:\pd\conf.d", true));

        finding.Path.ShouldBe(@"C:\pd");
    }

    /// <summary>
    /// A directory must sever inheritance; a file inside one must not be required to.
    /// </summary>
    /// <remarks>
    /// The repair re-enables inheritance on each job file on purpose, so that what a child gets
    /// stays exactly the rule when the rule changes. Demanding <c>D:P</c> per file would refuse
    /// precisely the state the repair leaves behind - the guard would condemn its own fix.
    /// </remarks>
    [Fact]
    public void OnlyADirectoryHasToSeverInheritance()
    {
        var directory = Clean(@"C:\pd\conf.d") with { Protected = false };
        var file = Clean(@"C:\pd\conf.d\app.toml", isDirectory: false) with { Protected = false };

        Verify(Reading((@"C:\pd\conf.d", directory)), (@"C:\pd\conf.d", true))
            .Verdict.ShouldBe(AclVerdict.Inherited);

        Verify(Reading((@"C:\pd\conf.d\app.toml", file)), (@"C:\pd\conf.d\app.toml", false))
            .Verdict.ShouldBe(AclVerdict.Hardened);
    }
}
