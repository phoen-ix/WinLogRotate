using Shouldly;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Hosting.Hosts;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// <c>host use</c> switches run hosts without a window in which nothing runs rotations, and
/// records what it did.
/// </summary>
/// <remarks>
/// <para>
/// It used to remove the registered task first and register the new one second, and a
/// registration schtasks refused escaped as <c>InvalidOperationException</c> - so a Group Policy
/// that forbids task creation produced exit 4, "a defect in the product", on a machine that had
/// just lost its working task. And nothing ever wrote <c>HostKind</c> back, so after the
/// operator's own <c>host use none</c> every <c>host status</c> warned that something had removed
/// the task.
/// </para>
/// <para>
/// Windows only: the platform refusal comes before the registrar, and there is no seam to make
/// <c>OperatingSystem.IsWindows()</c> answer otherwise. The mapping from a refusal to its
/// diagnostic is pure and runs everywhere.
/// </para>
/// </remarks>
public sealed class HostUseTests
{
    private sealed class FakeRunHost : IRunHost
    {
        public List<string> Calls { get; } = [];

        public RunHostKind? Recorded { get; private set; }

        public HostRegistrationException? Refusal { get; init; }

        public RunHostKind Kind => RunHostKind.Task;

        public void Install(HostInstallOptions options)
        {
            Calls.Add("install");
            if (Refusal is { } refusal)
            {
                throw refusal;
            }
        }

        public void Uninstall() => Calls.Add("uninstall");

        public HostStatus Query() => new()
        {
            Configured = Recorded ?? RunHostKind.None,
            Actual = Calls.Contains("install") && Refusal is null ? RunHostKind.Task : RunHostKind.None,
            Registered = Calls.Contains("install") && Refusal is null,
        };

        public void Record(RunHostKind configured)
        {
            Calls.Add("record");
            Recorded = configured;
        }
    }

    private static (RecordingSink Sink, Cli.Commands.CommandContext Ctx) Context(params string[] args)
    {
        var sink = new RecordingSink();
        return (sink, new Cli.Commands.CommandContext(sink, Cli.Commands.CommandTree.Build().Parse(args)));
    }

    [Fact]
    public void ARefusedRegistrationIsSaidUnderItsOwnCodeAndTakesNothingAway()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the platform refusal comes first off Windows.");

        var host = new FakeRunHost { Refusal = new HostRegistrationException("schtasks.exe exited 1: Access is denied.") };
        var (sink, ctx) = Context("host", "use", "task");

        var exit = Cli.Commands.HostCommand.Use(ctx, "task", null, host, elevated: () => true, verb: "host use");

        exit.ShouldBe(ExitCode.Errors);
        host.Calls.ShouldBe(["install"], "the working task is not removed first, and nothing is recorded");

        var d = sink.Diagnostics.ShouldHaveSingleItem();
        d.Code.ShouldBe(DiagnosticCode.HostRegistrationFailed);
        d.Message.ShouldContain("Access is denied", Case.Sensitive, "what schtasks said is what the operator reads");
        sink.Verb.ShouldBe("host use");
    }

    [Fact]
    public void ASuccessfulSwitchToATaskIsRecordedWithoutRemovingTheOldOneFirst()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the platform refusal comes first off Windows.");

        var host = new FakeRunHost();
        var (sink, ctx) = Context("host", "use", "task");

        var exit = Cli.Commands.HostCommand.Use(ctx, "task", null, host, elevated: () => true, verb: "host use");

        exit.ShouldBe(ExitCode.Ok);
        host.Calls.ShouldBe(["install", "record"]);
        host.Recorded.ShouldBe(RunHostKind.Task);
        sink.Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void SwitchingToNothingRemovesTheTaskAndRecordsThatChoice()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the platform refusal comes first off Windows.");

        var host = new FakeRunHost();
        var (sink, ctx) = Context("host", "use", "none");

        var exit = Cli.Commands.HostCommand.Use(ctx, "none", null, host, elevated: () => true, verb: "host use");

        exit.ShouldBe(ExitCode.Ok);
        host.Calls.ShouldBe(["uninstall", "record"]);
        host.Recorded.ShouldBe(RunHostKind.None, "so host status stops calling the operator's own choice drift");
        sink.Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void RepairReportsItselfAsRepair()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the platform refusal comes first off Windows.");

        var host = new FakeRunHost();
        var (sink, ctx) = Context("host", "repair");

        Cli.Commands.HostCommand.Use(ctx, "task", null, host, elevated: () => true, verb: "host repair");

        sink.Verb.ShouldBe("host repair");
    }

    /// <summary>The mapping itself, on every leg.</summary>
    [Fact]
    public void ARefusalCarriesWhatTheRegistrarSaidAndPromisesNothingWasRemoved()
    {
        var d = Cli.Commands.HostCommand.RegistrationFailed(
            new HostRegistrationException("schtasks.exe /create exited 1: ERROR: Access is denied."));

        d.Severity.ShouldBe(Severity.Error);
        d.Code.ShouldBe(DiagnosticCode.HostRegistrationFailed);
        d.Message.ShouldContain("Access is denied", Case.Sensitive);
        d.Remedy.ShouldNotBeNull();
        d.Remedy.ShouldContain("still registered", Case.Sensitive);
    }
}
