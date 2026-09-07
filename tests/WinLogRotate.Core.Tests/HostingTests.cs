using System.Xml.Linq;
using Shouldly;
using WinLogRotate.Hosting;
using WinLogRotate.Hosting.Hosts;
using WinLogRotate.Hosting.Security;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// The task XML decides whether rotation ever runs. These assertions exist because every one of
/// Task Scheduler's relevant defaults is wrong for a maintenance task, and each wrong default
/// fails silently - the task simply never fires and nobody notices until a disk fills.
/// </summary>
public class TaskXmlBuilderTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private static XDocument Build(TaskDefinition? definition = null) =>
        XDocument.Parse(TaskXmlBuilder.Build(definition ?? new TaskDefinition
        {
            ExecutablePath = @"C:\Program Files\WinLogRotate\winlogrotate.exe",
            Arguments = "run --lock-held-exit 0",
            Account = RunAccount.System,
        }));

    private static string Setting(XDocument doc, string name) =>
        doc.Root!.Element(Ns + "Settings")!.Element(Ns + name)!.Value;

    /// <summary>
    /// The anacron equivalent. Without it a machine that was off at 03:00 skips that day
    /// entirely and logs Event ID 153, "Missed task start rejected", which nobody reads.
    /// </summary>
    [Fact]
    public void MissedRunsAreCaughtUpAfterTheMachineComesBack() =>
        Setting(Build(), "StartWhenAvailable").ShouldBe("true");

    /// <summary>
    /// Both battery settings default to true, and between them they silently stop the task on
    /// every laptop and on any VM whose host reports a battery.
    /// </summary>
    [Fact]
    public void BatteryPolicyDoesNotSuppressTheTask()
    {
        var doc = Build();
        Setting(doc, "DisallowStartIfOnBatteries").ShouldBe("false");
        Setting(doc, "StopIfGoingOnBatteries").ShouldBe("false");
    }

    /// <summary>Task Scheduler's default is PT72H. A rotation wedged for three days is not a
    /// rotation.</summary>
    [Fact]
    public void ThereIsARealTimeLimit() =>
        Setting(Build(), "ExecutionTimeLimit").ShouldBe("PT1H");

    /// <summary>A free second line of defence behind the Global\ mutex.</summary>
    [Fact]
    public void OverlappingRunsAreRefusedByTheSchedulerToo() =>
        Setting(Build(), "MultipleInstancesPolicy").ShouldBe("IgnoreNew");

    [Fact]
    public void RunsAsSystemWithHighestPrivileges()
    {
        var principal = Build().Root!.Element(Ns + "Principals")!.Element(Ns + "Principal")!;

        principal.Element(Ns + "UserId")!.Value.ShouldBe("NT AUTHORITY\\SYSTEM");
        principal.Element(Ns + "LogonType")!.Value.ShouldBe("ServiceAccount");
        principal.Element(Ns + "RunLevel")!.Value.ShouldBe("HighestAvailable");
    }

    [Fact]
    public void AnHourlyCadenceRepeatsWithinADailyTrigger()
    {
        var doc = Build(new TaskDefinition
        {
            ExecutablePath = @"C:\x\winlogrotate.exe",
            Arguments = "run",
            Account = RunAccount.System,
            Frequency = HostFrequency.Hourly,
        });

        var repetition = doc.Root!.Element(Ns + "Triggers")!
            .Element(Ns + "CalendarTrigger")!.Element(Ns + "Repetition")!;

        repetition.Element(Ns + "Interval")!.Value.ShouldBe("PT1H");
        repetition.Element(Ns + "Duration")!.Value.ShouldBe("P1D");
    }

    /// <summary>
    /// The task passes --lock-held-exit 0 because Task Scheduler renders exit 3 as 0x3, which
    /// an admin skimming the Last Run Result column reads as a failure. The cost is that 0x0 no
    /// longer proves work happened, which is why status is read from the journal instead.
    /// </summary>
    [Fact]
    public void TheCommandAndArgumentsAreCarriedThrough()
    {
        var exec = Build().Root!.Element(Ns + "Actions")!.Element(Ns + "Exec")!;

        exec.Element(Ns + "Command")!.Value.ShouldBe(@"C:\Program Files\WinLogRotate\winlogrotate.exe");
        exec.Element(Ns + "Arguments")!.Value.ShouldContain("--lock-held-exit 0");
    }

    [Fact]
    public void TheXmlParses() => Build().Root!.Name.ShouldBe(Ns + "Task");

    /// <summary>
    /// Keeps Settings in the documented settingsType sequence.
    /// <para>
    /// Whether Task Scheduler enforces the order is not established - a real
    /// <c>schtasks /query /xml</c> export puts some of these elsewhere, which suggests it does
    /// not. Matching the documented sequence anyway costs nothing and removes one variable from
    /// any future investigation of a rejected task.
    /// </para>
    /// </summary>
    [Fact]
    public void SettingsFollowTheSchemaSequence()
    {
        // The subsequence of taskSchedulerSchema's settingsType that we actually emit.
        string[] schemaOrder =
        [
            "AllowStartOnDemand",
            "RestartOnFailure",
            "MultipleInstancesPolicy",
            "DisallowStartIfOnBatteries",
            "StopIfGoingOnBatteries",
            "AllowHardTerminate",
            "StartWhenAvailable",
            "NetworkProfileName",
            "RunOnlyIfNetworkAvailable",
            "WakeToRun",
            "Enabled",
            "Hidden",
            "DeleteExpiredTaskAfter",
            "IdleSettings",
            "NetworkSettings",
            "ExecutionTimeLimit",
            "Priority",
            "RunOnlyIfIdle",
            "UseUnifiedSchedulingEngine",
        ];

        var emitted = Build().Root!.Element(Ns + "Settings")!
            .Elements()
            .Select(e => e.Name.LocalName)
            .ToArray();

        emitted.ShouldAllBe(name => schemaOrder.Contains(name));

        var positions = emitted.Select(name => Array.IndexOf(schemaOrder, name)).ToArray();
        positions.ShouldBe(
            positions.OrderBy(p => p).ToArray(),
            $"Settings must follow the schema sequence; got: {string.Join(", ", emitted)}");
    }


    [Theory]
    [InlineData(1, "PT1H")]
    [InlineData(4, "PT4H")]
    public void DurationsUseTheSchedulerSchema(int hours, string expected) =>
        TaskXmlBuilder.XmlDuration(TimeSpan.FromHours(hours)).ShouldBe(expected);
}

public class SddlTests
{
    /// <summary>
    /// The single most security-sensitive string in the product. Default ProgramData
    /// inheritance lets any local user create files and gives CREATOR OWNER full control of
    /// them, so an unhardened conf.d means any user can have SYSTEM run their command.
    /// </summary>
    [Fact]
    public void TheConfigDirectoryDaclIsProtectedAndAdminOnly()
    {
        var sddl = Sddl.ConfigDirectory;

        // D:P - protected. Without this flag a correct DACL silently re-inherits ProgramData's
        // permissive entries the moment anyone edits them.
        sddl.ShouldContain("D:PAI");
        sddl.ShouldContain($"(A;OICI;FA;;;SY)");
        sddl.ShouldContain($"(A;OICI;FA;;;BA)");

        // Users get read and execute only. Read is deliberate - the unelevated read-only GUI
        // is a shipped feature, and reading a job file grants nothing.
        sddl.ShouldContain("(A;OICI;0x1200a9;;;BU)");

        sddl.ShouldNotContain("WD");   // Everyone
        sddl.ShouldNotContain("CO");   // CREATOR OWNER - the exact ProgramData hole
    }

    [Fact]
    public void TheRunAccountGetsWriteOnlyWhereItIsNeeded()
    {
        var sddl = Sddl.WritableFor("S-1-5-21-1-2-3-1001");

        sddl.ShouldContain("S-1-5-21-1-2-3-1001");
        sddl.ShouldContain("D:PAI");
    }

    /// <summary>
    /// Never by name. On this developer's machine the Administrators group is called
    /// "Administratoren", and an icacls that fails on a localised name leaves the permissive
    /// inherited entry in place - silently, which is the whole hole.
    /// </summary>
    [Fact]
    public void WellKnownSidsAreUsedRatherThanGroupNames()
    {
        Sddl.WellKnown.LocalSystem.ShouldBe("S-1-5-18");
        Sddl.WellKnown.Administrators.ShouldBe("S-1-5-32-544");
        Sddl.WellKnown.CreatorOwner.ShouldBe("S-1-3-0");
    }
}

public class NamePinningTests
{
    /// <summary>
    /// These names are hard-coded in both the application and the installer script. Changing
    /// one without the other does not fail loudly - the installer simply stops seeing a running
    /// rotation and overwrites the exe underneath it. The installer half of this assertion
    /// arrives with the .nsi file.
    /// </summary>
    [Fact]
    public void TheRotationMutexIsGlobalAndNamed()
    {
        Names.RotationMutex.ShouldBe(@"Global\WinLogRotate.Rotation");

        // Local\ would be per-session, so the SYSTEM task in session 0 and the user's GUI would
        // create two separate mutexes and both would run at once over the same files.
        Names.RotationMutex.ShouldStartWith(@"Global\");
    }
}

/// <summary>
/// Pins the names duplicated between the application and the installer script.
/// </summary>
/// <remarks>
/// Renaming one of these in only one place does not fail loudly. The installer simply stops
/// recognising a running rotation and overwrites the executable underneath it, or stops finding
/// the service it is meant to remove. Both are silent, and both would be discovered by a user
/// rather than by CI - which is what this test exists to prevent.
/// </remarks>
public class InstallerNamePinningTests
{
    private static string Nsi() =>
        File.ReadAllText(Path.Combine(RepoRoot.Find().FullName, "packaging", "winlogrotate.nsi"));

    /// <summary>
    /// Reads a !define, resolving any ${OTHER} references in its value.
    /// <para>
    /// The installer legitimately composes values - UNINST_KEY ends in ${APP} - so comparing
    /// raw text would force the script to repeat itself purely to satisfy a test. Resolving the
    /// substitution keeps the assertion honest without dictating the file's style.
    /// </para>
    /// </summary>
    private static string Define(string text, string name)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            text, "^\\s*!define\\s+" + name + "\\s+\"([^\"]*)\"",
            System.Text.RegularExpressions.RegexOptions.Multiline);

        match.Success.ShouldBeTrue($"the installer should define {name}");

        return System.Text.RegularExpressions.Regex.Replace(
            match.Groups[1].Value,
            @"\$\{(\w+)\}",
            m => Define(text, m.Groups[1].Value));
    }

    [Fact]
    public void TheInstallerAndTheApplicationAgreeOnEverySharedName()
    {
        var nsi = Nsi();

        Define(nsi, "ROTATION_MUTEX").ShouldBe(Names.RotationMutex);
        Define(nsi, "GUI_QUIT_EVENT").ShouldBe(Names.GuiQuitEvent);
        Define(nsi, "SERVICE_NAME").ShouldBe(Names.ServiceName);
        Define(nsi, "TASK_PATH").ShouldBe(Names.TaskPath);
        Define(nsi, "UNINST_KEY").ShouldBe(Names.UninstallKey);
    }

    /// <summary>
    /// The security descriptor appears in three places - this constant, the installer's icacls
    /// fallback, and the smoke test's expected value. Two of the three are bound here.
    /// </summary>
    [Fact]
    public void TheInstallerAppliesTheSameSecurityDescriptorTheCodeDoes() =>
        Define(Nsi(), "DATA_SDDL").ShouldBe(Sddl.ConfigDirectory);

    [Fact]
    public void TheEventLogKeyMatches() =>
        Define(Nsi(), "EVENTLOG_KEY")
            .ShouldBe($@"SYSTEM\CurrentControlSet\Services\EventLog\{Names.EventLogName}\{Names.EventLogSource}");
}
