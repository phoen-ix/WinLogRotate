using Shouldly;
using WinLogRotate.Cli.Commands;
using WinLogRotate.Core;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// What <c>doctor</c> says about the Windows Event Log.
/// </summary>
/// <remarks>
/// <para>
/// <c>docs/diagnostics.md</c> has promised that "winlogrotate doctor tells you which state you
/// are in" since the Event Log shipped, and doctor said nothing at all.
/// <c>EventLogWriter.IsRegistered</c> was written, public and documented, with exactly one caller
/// that was not this.
/// </para>
/// <para>
/// The decision is pure and takes the answer rather than asking for it, so this runs on both CI
/// legs. Neither runner has an event source registered, so a test that called into the writer
/// would be measuring the absence of advapi32 - the mistake this milestone spent its first commit
/// undoing.
/// </para>
/// </remarks>
public sealed class DoctorEventLogTests
{
    /// <summary>
    /// A registered source is writable; an unregistered one is a fault only where a source was
    /// ever going to exist.
    /// </summary>
    /// <remarks>
    /// Creating a source needs administrator, so the installer does it and only a per-machine
    /// install has one. Telling somebody running a portable copy that their Event Log is broken
    /// is crying wolf at the person least able to act on it - and it is the failure mode this
    /// whole milestone is about, pointed the other way: reporting a fault that is not one erodes
    /// exactly the same trust as reporting a success that was not one.
    /// </remarks>
    [Theory]
    [InlineData(InstallScope.PerMachine, true, "writable", true)]
    [InlineData(InstallScope.PerMachine, false, "unregistered", true)]
    [InlineData(InstallScope.PerUser, false, "unregistered", false)]
    [InlineData(InstallScope.Portable, false, "unregistered", false)]
    public void DoctorReportsWhetherTheEventLogCanBeWritten(
        InstallScope scope, bool registered, string state, bool expected)
    {
        var verdict = DoctorCommand.EventLogVerdict(supported: true, registered, scope);

        verdict.State.ShouldBe(state);
        verdict.Expected.ShouldBe(expected);
    }

    /// <summary>Off Windows there is nothing to report and nothing to repair.</summary>
    [Fact]
    public void OffWindowsThereIsNothingToReport()
    {
        foreach (var scope in Enum.GetValues<InstallScope>())
        {
            var verdict = DoctorCommand.EventLogVerdict(supported: false, registered: false, scope);

            verdict.State.ShouldBe("unsupported", scope.ToString());
            verdict.Expected.ShouldBeFalse("nothing is missing on a machine that has no Event Log");
        }
    }

    /// <summary>
    /// Every install scope has an answer, so none can fall through to a default.
    /// </summary>
    /// <remarks>
    /// The same assertion as <c>EventLogBudgetTests.EveryVerdictIsAccountedFor</c> and for the
    /// same reason. A scope added later and silently treated as "no source expected here" would
    /// be a per-machine install quietly reported as healthy while nothing reached Event Viewer.
    /// </remarks>
    [Fact]
    public void EveryInstallScopeIsAccountedFor()
    {
        var scopes = Enum.GetValues<InstallScope>();

        scopes.Length.ShouldBeGreaterThan(2, "Portable, PerUser and PerMachine at the least");

        foreach (var scope in scopes)
        {
            Should.NotThrow(() => DoctorCommand.EventLogVerdict(supported: true, registered: false, scope));
            Should.NotThrow(() => DoctorCommand.EventLogVerdict(supported: true, registered: true, scope));
        }
    }
}
