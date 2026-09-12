using Shouldly;
using WinLogRotate.Cli.Commands;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Hosting.Security;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// Every file this product leaves in its own configuration directory is claimed for
/// Administrators.
/// </summary>
/// <remarks>
/// <para>
/// The gate judges the owner of each file it would execute a hook from, because rewriting a
/// directory's DACL changes neither a child's explicit entries nor a child's owner, and an owner
/// holds implicit WRITE_DAC.
/// </para>
/// <para>
/// An elevated administrator's created objects are owned by that account's own SID rather than
/// by <c>BUILTIN\Administrators</c> - a fact this product learned from a shipped release. So
/// without this, every <c>import</c> and every <c>secret set</c> would leave behind a file the
/// gate refuses, hooks would go off on healthy machines as a matter of routine, and the printed
/// remedy would hold only until the next edit.
/// </para>
/// <para>
/// Setting the owner is a Windows call and <c>ConfDirHardeningTests</c> is the only judge of
/// whether it lands. What runs on both legs is the wiring: that each writer claims the path it
/// actually wrote, and that the thing it claims with is the real one.
/// </para>
/// </remarks>
public sealed class ConfigWritesTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-writes-");

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void AJobFileIsWrittenAndThenClaimed()
    {
        var path = Path.Combine(_dir.FullName, "iis.toml");
        var claimed = new List<string>();

        ConfigWrites.Job(path, "name = \"iis\"\n", claim: p =>
        {
            // Ordering, asserted where it is decided: a claim before the write would be a claim
            // on a file that is not there.
            File.Exists(p).ShouldBeTrue("the file has to exist by the time it is claimed");
            claimed.Add(p);
            return true;
        });

        File.ReadAllText(path).ShouldBe("name = \"iis\"\n");
        claimed.ShouldHaveSingleItem().ShouldBe(path);
    }

    [Fact]
    public void SavingTheConfigurationClaimsTheFileItSaved()
    {
        var path = Path.Combine(_dir.FullName, "config.toml");
        File.WriteAllText(path, "[defaults]\nrotate = 4\n");

        var file = TomlFile.Load(path);
        var claimed = new List<string>();

        ConfigWrites.Config(file, claim: p => { claimed.Add(p); return true; });

        claimed.ShouldHaveSingleItem().ShouldBe(path);
    }

    /// <summary>
    /// The claim the product makes is the real one.
    /// </summary>
    /// <remarks>
    /// Without this the two facts above are satisfied by any lambda a test cares to pass, and
    /// the production default could be <c>_ =&gt; true</c> - a writer that claims nothing, under
    /// two green tests that watched a test double do it.
    /// </remarks>
    [Fact]
    public void TheClaimTheProductMakesIsTheRealOne()
    {
        ConfigWrites.Owner.Method.Name.ShouldBe(nameof(ConfigFileOwner.Claim));
        ConfigWrites.Owner.Method.DeclaringType.ShouldBe(typeof(ConfigFileOwner));
    }

    /// <summary>Off Windows there is no owner to set, and saying so is not a failure.</summary>
    [Fact]
    public void ClaimingIsHonestAboutNotBeingWindows()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "this is the answer on the other leg");

        var path = Path.Combine(_dir.FullName, "somewhere.toml");
        File.WriteAllText(path, "");

        ConfigFileOwner.Claim(path).ShouldBeFalse();
    }
}
