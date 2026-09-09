using Shouldly;
using WinLogRotate.Hosting.Security;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// The access-mask arithmetic behind "can a non-administrator change what we execute?".
/// </summary>
/// <remarks>
/// This is the test that was missing. The mask used to include FileSystemRights.FullControl,
/// which is not a flag but the composite 0x1F01FF - so it matched every read bit too. The
/// configuration directory grants BUILTIN\Users read on purpose, which meant a correctly
/// hardened directory was reported as writable and every hook was refused on every install.
/// Nothing caught it: the SDDL was right, the installer was right, and the ACL comparison in
/// the smoke test was right. Only the arithmetic was wrong, and nothing was testing arithmetic.
/// </remarks>
public sealed class AclMaskTests
{
    [Fact]
    public void ReadAndExecuteIsNotWrite()
    {
        // The regression, stated as plainly as it can be. Sddl.ConfigDirectory grants exactly
        // this to Users so the unelevated read-only GUI works.
        AclMask.GrantsWrite(AclMask.UsersReadExecute).ShouldBeFalse();
    }

    [Fact]
    public void FullControlIsAComposite_NotAFlag()
    {
        // Why the bug existed. Anyone adding a right to Writeish needs to see this.
        (AclMask.FullControl & AclMask.UsersReadExecute).ShouldBe(AclMask.UsersReadExecute);
        (AclMask.Writeish & AclMask.FullControl).ShouldNotBe(AclMask.FullControl);
    }

    [Fact]
    public void FullControlStillCountsAsWrite()
    {
        // Omitting the composite from the mask must not stop us noticing a real grant of it.
        AclMask.GrantsWrite(AclMask.FullControl).ShouldBeTrue();
    }

    [Theory]
    [InlineData(AclMask.WriteData)]
    [InlineData(AclMask.AppendData)]
    [InlineData(AclMask.WriteExtendedAttributes)]
    [InlineData(AclMask.WriteAttributes)]
    [InlineData(AclMask.Delete)]
    [InlineData(AclMask.DeleteSubdirectoriesAndFiles)]
    [InlineData(AclMask.ChangePermissions)]
    [InlineData(AclMask.TakeOwnership)]
    public void EveryWayOfChangingOurContentsCounts(int right)
    {
        AclMask.GrantsWrite(right).ShouldBeTrue();
    }

    [Theory]
    [InlineData(0x000001)] // FILE_READ_DATA
    [InlineData(0x000008)] // FILE_READ_EA
    [InlineData(0x000020)] // FILE_EXECUTE
    [InlineData(0x000080)] // FILE_READ_ATTRIBUTES
    [InlineData(0x020000)] // READ_CONTROL
    [InlineData(0x100000)] // SYNCHRONIZE
    public void NoPurelyReadingRightCounts(int right)
    {
        AclMask.GrantsWrite(right).ShouldBeFalse();
    }

    [Fact]
    public void NothingCountsAsWriteByDefault()
    {
        AclMask.GrantsWrite(0).ShouldBeFalse();
    }

    [Fact]
    public void ChangePermissionsCounts_BecauseItIsWriteAccessWearingADisguise()
    {
        // An account that can rewrite the DACL can grant itself anything else a moment later.
        AclMask.GrantsWrite(AclMask.ChangePermissions).ShouldBeTrue();
        AclMask.GrantsWrite(AclMask.TakeOwnership).ShouldBeTrue();
    }
}
