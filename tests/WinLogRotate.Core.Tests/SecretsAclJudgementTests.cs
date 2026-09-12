using Shouldly;
using WinLogRotate.Hosting.Security;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// Who may reach <c>secrets.dat</c>, judged without a security descriptor in sight.
/// </summary>
/// <remarks>
/// <para>
/// The opposite question to the configuration directory's. That one asks who can <i>write</i>,
/// because writing a job file is SYSTEM code execution. This asks who can read at all: reading a
/// stored credential is the whole harm, so a read-only grant counts.
/// </para>
/// <para>
/// Extracted for the reason the conf.d gate was, and it had the same consequence: welded to
/// <c>FileSecurity</c> inside a Windows-annotated class, the rule could only ever be executed by
/// windows-2025, and the half of it that was missing stayed missing for six milestones.
/// </para>
/// </remarks>
public sealed class SecretsAclJudgementTests
{
    private const string LocalSystem = "S-1-5-18";
    private const string Administrators = "S-1-5-32-544";
    private const string Users = "S-1-5-32-545";
    private const string LocalUser = "S-1-5-21-1-2-3-1001";

    private static readonly HashSet<string> Trusted =
        new(StringComparer.OrdinalIgnoreCase) { LocalSystem, Administrators };

    private static AclJudgement.Subject File(string? owner, params (string Sid, int Mask)[] allow) =>
        new(@"C:\pd\secrets.dat",
            IsDirectory: false,
            owner,
            owner ?? "nobody",
            [.. allow.Select(a => new AclJudgement.Ace(a.Sid, a.Mask, a.Sid))],
            Protected: true);

    /// <summary>SYSTEM and Administrators, and nothing else, is the shipped descriptor.</summary>
    [Fact]
    public void TheShippedDescriptorIsHardened() =>
        SecretsAclJudgement.Judge(
                File(Administrators, (LocalSystem, 0x1F01FF), (Administrators, 0x1F01FF)),
                Trusted)
            .Verdict.ShouldBe(SecretsAclVerdict.Hardened);

    /// <summary>
    /// A file owned by somebody outside the trusted set is too open, whatever its DACL says.
    /// </summary>
    /// <remarks>
    /// The half that was missing. An owner holds implicit WRITE_DAC, so it can grant itself read
    /// whenever it likes - and the DACL check would go on reporting <c>Hardened</c> right up
    /// until it did. <c>Sddl.SecretsFile</c> sets <c>O:BA</c>, so <c>Apply</c> has always
    /// produced the right owner; nothing ever checked that the file on disk still had it.
    /// </remarks>
    [Fact]
    public void AFileOwnedByALocalUserIsTooOpenWhateverItsDaclSays()
    {
        var perfect = File(LocalUser, (LocalSystem, 0x1F01FF), (Administrators, 0x1F01FF));

        var (verdict, offending) = SecretsAclJudgement.Judge(perfect, Trusted);

        verdict.ShouldBe(SecretsAclVerdict.TooOpen);
        offending.ShouldHaveSingleItem().ShouldContain(LocalUser);
    }

    /// <summary>
    /// A read-only grant counts, which is the difference between this rule and the other one.
    /// </summary>
    /// <remarks>
    /// <c>AclMask.GrantsWrite</c> is deliberately not consulted here. The configuration directory
    /// grants <c>BUILTIN\Users</c> read on purpose so the unelevated GUI works; a credential store
    /// that did the same would be readable by every local account, which is the whole harm.
    /// </remarks>
    [Fact]
    public void AReadOnlyGrantIsStillTooOpen() =>
        SecretsAclJudgement.Judge(
                File(Administrators,
                    (LocalSystem, 0x1F01FF),
                    (Administrators, 0x1F01FF),
                    (Users, 0x1200A9)),
                Trusted)
            .Verdict.ShouldBe(SecretsAclVerdict.TooOpen);

    /// <summary>
    /// A per-user installation's owner is trusted, because the store is theirs.
    /// </summary>
    /// <remarks>
    /// The other direction, and the reason the trusted set is a parameter. Locking the installing
    /// user out of their own credential store would be a fix that breaks the product.
    /// </remarks>
    [Fact]
    public void APerUserInstallationsOwnerIsTrusted()
    {
        var mine = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            LocalSystem, Administrators, LocalUser,
        };

        SecretsAclJudgement.Judge(
                File(LocalUser, (LocalSystem, 0x1F01FF), (Administrators, 0x1F01FF), (LocalUser, 0x1F01FF)),
                mine)
            .Verdict.ShouldBe(SecretsAclVerdict.Hardened);

        // And the same file judged per-machine is a finding, twice over.
        SecretsAclJudgement.Judge(
                File(LocalUser, (LocalSystem, 0x1F01FF), (Administrators, 0x1F01FF), (LocalUser, 0x1F01FF)),
                Trusted)
            .Verdict.ShouldBe(SecretsAclVerdict.TooOpen);
    }
}
