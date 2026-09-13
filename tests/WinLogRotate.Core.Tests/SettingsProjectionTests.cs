using System.Text.Json;
using Shouldly;
using WinLogRotate.Cli.Commands;
using WinLogRotate.Cli.Output;
using WinLogRotate.Gui.Cli;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// When the Settings page interrupts about the configuration directory's permissions.
/// </summary>
/// <remarks>
/// <para>
/// It interrupted every per-user installation on every visit. The page warned whenever hooks
/// were refused and the verdict was neither Hardened nor NotApplicable - which is the per-user
/// case by design, since <c>ConfDirGuard.Scoped</c> keeps the loose verdict for a directory in
/// the user's own profile, and Repair then declines to touch it for the same reason. A security
/// modal, pointing at a button that would refuse, for the one operator whose directory cannot be
/// secured.
/// </para>
/// <para>
/// <c>doctor</c> reports that case as Info and the others as Critical. The projection follows
/// its rule, and these are both scopes of it.
/// </para>
/// </remarks>
public sealed class SettingsProjectionTests
{
    /// <summary>A loose per-machine directory is the finding worth interrupting for.</summary>
    /// <remarks>
    /// It means any local user could have the run host execute their command as SYSTEM, and the
    /// details carry the exact command that closes it.
    /// </remarks>
    [Theory]
    [InlineData("LooseWritable")]
    [InlineData("LooseOwner")]
    [InlineData("Inherited")]
    public void ALoosePerMachineDirectoryIsWarnedAbout(string verdict)
    {
        var warning = SettingsProjection
            .PermissionsWarning(hooksAllowed: false, verdict, "PerMachine", "icacls ...")
            .ShouldNotBeNull();

        warning.Message.ShouldContain("not an administrator");
        warning.Message.ShouldContain("Repair permissions");
        warning.Details.ShouldBe("icacls ...");
    }

    /// <summary>
    /// A per-user installation is not warned about, because it is like that by design.
    /// </summary>
    /// <remarks>
    /// The defect. The directory is in the user's own profile, the hardened descriptor would
    /// take away their write access to their own jobs, and Repair declines for exactly that
    /// reason - so the modal pointed at a button that would refuse, on every visit.
    /// </remarks>
    [Theory]
    [InlineData("LooseWritable")]
    [InlineData("LooseOwner")]
    [InlineData("Inherited")]
    public void APerUserInstallationIsNotWarnedAbout(string verdict) =>
        SettingsProjection.PermissionsWarning(hooksAllowed: false, verdict, "PerUser", "icacls ...")
            .ShouldBeNull();

    /// <summary>A portable copy stays strict, as the guard keeps it.</summary>
    [Fact]
    public void APortableCopyIsWarnedAboutLikeAMachineInstall() =>
        SettingsProjection.PermissionsWarning(hooksAllowed: false, "LooseWritable", "Portable", null)
            .ShouldNotBeNull();

    /// <summary>Nothing to warn about when hooks are allowed, or when the verdict is not about writers.</summary>
    /// <remarks>
    /// Hardened is the goal; NotApplicable is not Windows; Unknown is an ACL nobody could read,
    /// and the sentence would claim something nobody checked.
    /// </remarks>
    [Theory]
    [InlineData("PerMachine", "Hardened", true)]
    [InlineData("PerMachine", "Hardened", false)]
    [InlineData("PerMachine", "NotApplicable", false)]
    [InlineData("PerMachine", "Unknown", false)]
    [InlineData("PerMachine", "LooseWritable", true)]
    public void NothingIsWarnedAboutWhenThereIsNothingToFix(string scope, string verdict, bool hooksAllowed) =>
        SettingsProjection.PermissionsWarning(hooksAllowed, verdict, scope, "icacls ...").ShouldBeNull();

    /// <summary>The wire spelling is matched without regard to case.</summary>
    /// <remarks>
    /// The GUI ships separately and may be driven by an older or newer winlogrotate.exe. A casing
    /// change must not silently turn the per-user exemption off, which is the direction that
    /// brings the modal back.
    /// </remarks>
    [Theory]
    [InlineData("peruser")]
    [InlineData("PERUSER")]
    public void TheScopeIsMatchedWithoutRegardToCase(string scope) =>
        SettingsProjection.PermissionsWarning(hooksAllowed: false, "LooseOwner", scope, null).ShouldBeNull();

    /// <summary>A missing fix command leaves the details empty rather than failing the warning.</summary>
    [Fact]
    public void AMissingFixLeavesTheDetailsEmpty() =>
        SettingsProjection.PermissionsWarning(hooksAllowed: false, "LooseWritable", "PerMachine", null)
            .ShouldNotBeNull()
            .Details.ShouldBeNull();

    /// <summary>
    /// The fields the page reads are the fields the real verb writes.
    /// </summary>
    /// <remarks>
    /// Driven through the real <c>doctor</c> and the real JSON sink, so a renamed property on the
    /// wire turns this red rather than turning the warning permanently off or permanently on. Off
    /// Windows the verdict is NotApplicable and the decision is null; what is asserted is that
    /// every spelling the page reads is present in what the verb actually emits, and that the
    /// real values are ones the decision understands.
    /// </remarks>
    [Fact]
    public void TheFieldsThePageReadsAreTheFieldsTheVerbWrites()
    {
        var dir = Directory.CreateTempSubdirectory("winlogrotate-settings-");

        try
        {
            var writer = new StringWriter();
            var ctx = new CommandContext(
                new JsonOutputSink(verbose: false, stream: false, streamTo: writer),
                CommandTree.Build().Parse(["doctor", "--json", "--config-dir", dir.FullName]));

            DoctorCommand.Run(ctx, dir.FullName).ShouldBe(ExitCode.Ok);

            var payload = JsonDocument.Parse(writer.ToString()).RootElement.GetProperty("result");

            // The same four reads SettingsPage.LoadAsync makes, spelled the same way.
            var hooksAllowed = payload.GetProperty("hooksAllowed").GetBoolean();
            var verdict = payload.GetProperty("aclVerdict").GetString().ShouldNotBeNull();
            var scope = payload.GetProperty("scope").GetString().ShouldNotBeNull();
            var fix = payload.TryGetProperty("aclFix", out var f) ? f.GetString() : null;

            verdict.ShouldBe("NotApplicable", "the ACL is not checked off Windows");
            scope.ShouldBeOneOf("Portable", "PerUser", "PerMachine");

            SettingsProjection.PermissionsWarning(hooksAllowed, verdict, scope, fix)
                .ShouldBeNull("nothing to secure off Windows");
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch (IOException) { }
        }
    }
}
