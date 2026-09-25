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

        /// <summary>What the registrar was asked to register, last.</summary>
        public HostInstallOptions? Installed { get; private set; }

        public HostRegistrationException? Refusal { get; init; }

        public RunHostKind Kind => RunHostKind.Task;

        public void Install(HostInstallOptions options)
        {
            Calls.Add("install");
            Installed = options;
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

    // ---- when the task fires -----------------------------------------------------------------

    /// <summary>A directory with a config.toml saying <paramref name="toml"/>, for one test.</summary>
    private static string ConfigDir(string? toml)
    {
        var dir = Directory.CreateTempSubdirectory("winlogrotate-host-").FullName;

        if (toml is not null)
        {
            File.WriteAllText(Path.Combine(dir, "config.toml"), toml);
        }

        return dir;
    }

    /// <summary>
    /// A time that is not one is refused before anything is touched, on every platform.
    /// </summary>
    /// <remarks>
    /// Judged above the Windows guard, like the unsupported host is: a fact about the arguments
    /// is the same answer everywhere, and this is what makes it provable where the tests run.
    /// </remarks>
    [Theory]
    [InlineData("25:00")]
    [InlineData("3pm")]
    [InlineData("03:00:00")]
    public void ATimeThatIsNotOneIsRefusedBeforeAnythingIsTouched(string at)
    {
        var host = new FakeRunHost();
        var (sink, ctx) = Context("host", "use", "task", "--at", at);

        var exit = Cli.Commands.HostCommand.Use(ctx, "task", null, host, elevated: () => true, verb: "host use", at);

        exit.ShouldBe(ExitCode.ConfigInvalid);
        host.Calls.ShouldBeEmpty();

        var d = sink.Diagnostics.ShouldHaveSingleItem();
        d.Code.ShouldBe(DiagnosticCode.ArgumentUnusable);
        d.Remedy.ShouldNotBeNull().ShouldContain("24-hour");
    }

    /// <summary>
    /// A [host] table added to a CRLF config.toml is written in CRLF.
    /// </summary>
    /// <remarks>
    /// The installer seeds the file with CRLF, and every install older than the table has one
    /// without it. The table went in with "\n" inside and Environment.NewLine around it, so the
    /// file came out with both - which TomlEditor, reading the ending from the file, never does.
    /// </remarks>
    [Fact]
    public void AnAddedTableKeepsTheFilesLineEndings()
    {
        var dir = ConfigDir("schema = 1\r\n\r\n[defaults]\r\nrotate = 7\r\n");

        Cli.Commands.HostCommand.Remember(InstallPaths.Resolve(dir), new TimeSpan(22, 30, 0)).ShouldBeNull();

        var written = File.ReadAllText(Path.Combine(dir, "config.toml"));
        written.ShouldEndWith("[host]\r\ntime = \"22:30\"\r\n");
        written.Replace("\r\n", string.Empty, StringComparison.Ordinal).ShouldNotContain('\n', "every line ends the file's own way");
    }

    /// <summary>A run model is a name: <c>host use 0</c> is refused, not read as <c>none</c>.</summary>
    /// <remarks>
    /// <c>Enum.TryParse</c> reads a number as the member with that value, so <c>0</c> meant
    /// <c>none</c> - which unregisters the scheduled task - and <c>7</c> a run model that does not
    /// exist.
    /// </remarks>
    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("7")]
    public void ARunModelIsANameAndNeverANumber(string kind)
    {
        var host = new FakeRunHost();
        var (sink, ctx) = Context("host", "use", kind);

        var exit = Cli.Commands.HostCommand.Use(ctx, kind, null, host, elevated: () => true, verb: "host use");

        exit.ShouldBe(ExitCode.ConfigInvalid);
        host.Calls.ShouldBeEmpty();
        sink.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(DiagnosticCode.ArgumentUnusable);
    }

    /// <summary>--at says when the task fires, and 'none' registers no task.</summary>
    [Fact]
    public void AtMeansNothingWithoutATask()
    {
        var host = new FakeRunHost();
        var (sink, ctx) = Context("host", "use", "none", "--at", "22:30");

        var exit = Cli.Commands.HostCommand.Use(ctx, "none", null, host, elevated: () => true, verb: "host use", "22:30");

        exit.ShouldBe(ExitCode.ConfigInvalid);
        host.Calls.ShouldBeEmpty("the task that is registered stays registered");
        sink.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(DiagnosticCode.ArgumentUnusable);
    }

    /// <summary>
    /// The time is written to config.toml and the task is registered at it, in that order.
    /// </summary>
    /// <remarks>
    /// The file first: host repair re-registers from it, so a task at a time the file does not
    /// hold is put back to 03:00 by the next repair without a word. Every installation older
    /// than the [host] table has a config.toml without one, so this is the case that matters.
    /// </remarks>
    [Fact]
    public void TheTimeIsRememberedInTheConfigurationAndRegistered()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the platform refusal comes first off Windows.");

        var host = new FakeRunHost();
        var dir = ConfigDir("schema = 1\n\n[journal]\nretain = 7\n");
        var (sink, ctx) = Context("host", "use", "task", "--at", "22:30");

        var exit = Cli.Commands.HostCommand.Use(ctx, "task", dir, host, elevated: () => true, verb: "host use", "22:30");

        exit.ShouldBe(ExitCode.Ok);
        sink.Diagnostics.ShouldBeEmpty();
        host.Installed.ShouldNotBeNull().TimeOfDay.ShouldBe(new TimeSpan(22, 30, 0));

        var written = File.ReadAllText(Path.Combine(dir, "config.toml"));
        written.ShouldContain("retain = 7", Case.Sensitive, "what was there is untouched");
        written.ShouldContain("[host]", Case.Sensitive);
        written.ShouldContain("time = \"22:30\"", Case.Sensitive);
        sink.Lines.ShouldContain(l => l.Contains("22:30", StringComparison.Ordinal));
    }

    /// <summary>Without --at, the configured time is what is registered - which is how repair keeps it.</summary>
    [Fact]
    public void WithoutAtTheConfiguredTimeIsRegistered()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the platform refusal comes first off Windows.");

        var host = new FakeRunHost();
        var dir = ConfigDir("schema = 1\n\n[host]\ntime = \"05:15\"\n");
        var (_, ctx) = Context("host", "use", "task");

        Cli.Commands.HostCommand.Use(ctx, "task", dir, host, elevated: () => true, verb: "host repair");

        host.Installed.ShouldNotBeNull().TimeOfDay.ShouldBe(new TimeSpan(5, 15, 0));
    }

    /// <summary>A configuration that will not give a time registers nothing, and says why.</summary>
    [Fact]
    public void AConfigurationThatWillNotGiveATimeRegistersNothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the platform refusal comes first off Windows.");

        var host = new FakeRunHost();
        var dir = ConfigDir("schema = 1\n\n[host]\ntime = \"25:00\"\n");
        var (sink, ctx) = Context("host", "use", "task");

        var exit = Cli.Commands.HostCommand.Use(ctx, "task", dir, host, elevated: () => true, verb: "host use");

        exit.ShouldBe(ExitCode.ConfigInvalid);
        host.Calls.ShouldBeEmpty();
        sink.Diagnostics.ShouldContain(d => d.Code == DiagnosticCode.ConfigInvalid);
    }

    /// <summary>
    /// The reader every verb shares: absent means the default, a bad time is an error, a
    /// mistyped key is a warning that still yields an answer.
    /// </summary>
    [Fact]
    public void TheConfiguredTimeIsReadTheSameWayEverywhere()
    {
        var paths = InstallPaths.Resolve(ConfigDir(null));
        Cli.Commands.HostCommand.ConfiguredTime(paths).Settings.ShouldNotBeNull().TimeText.ShouldBe("03:00");

        paths = InstallPaths.Resolve(ConfigDir("[host]\ntime = \"22:30\"\ntme = 1\n"));
        var read = Cli.Commands.HostCommand.ConfiguredTime(paths);
        read.Settings.ShouldNotBeNull().TimeText.ShouldBe("22:30");
        read.Diagnostics.ShouldHaveSingleItem().Severity.ShouldBe(Severity.Warning);

        paths = InstallPaths.Resolve(ConfigDir("[host\n"));
        read = Cli.Commands.HostCommand.ConfiguredTime(paths);
        read.Settings.ShouldBeNull();
        read.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(DiagnosticCode.ConfigInvalid);
    }

    /// <summary>An exported task fires when the configuration says, not at the builder's default.</summary>
    [Fact]
    public void AnExportedTaskFiresAtTheConfiguredTime()
    {
        var dir = ConfigDir("schema = 1\n\n[host]\ntime = \"22:30\"\n");
        var (sink, ctx) = Context("host", "export-task");

        var exit = Cli.Commands.ExportTaskCommand.Run(ctx, dir);

        exit.ShouldBe(ExitCode.Ok);
        sink.Lines.ShouldHaveSingleItem().ShouldContain("T22:30:00</StartBoundary>", Case.Sensitive);
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
