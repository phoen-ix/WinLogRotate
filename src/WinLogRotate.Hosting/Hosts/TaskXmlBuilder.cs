using System.Globalization;
using System.Xml.Linq;

namespace WinLogRotate.Hosting.Hosts;

/// <summary>How often the run host fires.</summary>
public enum HostFrequency
{
    Hourly,
    Daily,
}

/// <summary>Who the run host runs as.</summary>
public sealed record RunAccount
{
    /// <summary>LocalSystem. Needs no password and holds the privileges file operations want,
    /// but authenticates on the network as DOMAIN\MACHINE$ - so a UNC log share must grant the
    /// computer account, or a domain account / gMSA must be used instead.</summary>
    public static RunAccount System { get; } = new() { UserId = "NT AUTHORITY\\SYSTEM", ServiceAccount = true };

    public required string UserId { get; init; }
    public bool ServiceAccount { get; init; }
}

/// <summary>Everything the registered task needs to say.</summary>
public sealed record TaskDefinition
{
    public required string ExecutablePath { get; init; }
    public required string Arguments { get; init; }
    public required RunAccount Account { get; init; }
    public HostFrequency Frequency { get; init; } = HostFrequency.Daily;
    public TimeSpan TimeOfDay { get; init; } = TimeSpan.FromHours(3);
    public TimeSpan ExecutionTimeLimit { get; init; } = TimeSpan.FromHours(1);
    public bool StartWhenAvailable { get; init; } = true;
    public bool RunOnBatteries { get; init; } = true;
    public string Description { get; init; } = "Rotates log files according to the WinLogRotate configuration.";
}

/// <summary>
/// Emits Task Scheduler XML.
/// </summary>
/// <remarks>
/// <para>
/// XML rather than the COM API, because the CLI is NativeAOT and the
/// <c>Microsoft.Win32.TaskScheduler</c> package is built on built-in COM interop, which a
/// native image does not have. XML also beats the flag form of <c>schtasks</c>, whose date and
/// time parsing is locale-dependent and simply breaks on a German machine.
/// </para>
/// <para>
/// The defaults below are the point of this class. Task Scheduler's own defaults will silently
/// stop a maintenance task from ever running, and each of the four overridden here has cost
/// somebody a full disk at some point.
/// </para>
/// </remarks>
public static class TaskXmlBuilder
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public static string Build(TaskDefinition definition)
    {
        var start = DateTime.Today.Add(definition.TimeOfDay)
            .ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

        var trigger = new XElement(Ns + "CalendarTrigger",
            new XElement(Ns + "StartBoundary", start),
            new XElement(Ns + "Enabled", true),
            new XElement(Ns + "ScheduleByDay", new XElement(Ns + "DaysInterval", 1)));

        if (definition.Frequency == HostFrequency.Hourly)
        {
            // An hourly cadence is expressed as a daily trigger repeating every hour for a day,
            // which is what Task Scheduler actually understands.
            trigger.AddFirst(new XElement(Ns + "Repetition",
                new XElement(Ns + "Interval", "PT1H"),
                new XElement(Ns + "Duration", "P1D"),
                new XElement(Ns + "StopAtDurationEnd", false)));
        }

        var document = new XDocument(
            new XDeclaration("1.0", "UTF-16", null),
            new XElement(Ns + "Task",
                new XAttribute("version", "1.4"),
                new XElement(Ns + "RegistrationInfo",
                    new XElement(Ns + "Description", definition.Description),
                    new XElement(Ns + "URI", @"\WinLogRotate\Rotate")),
                new XElement(Ns + "Triggers", trigger),
                new XElement(Ns + "Principals",
                    new XElement(Ns + "Principal",
                        new XAttribute("id", "Author"),
                        new XElement(Ns + "UserId", definition.Account.UserId),
                        new XElement(Ns + "LogonType",
                            definition.Account.ServiceAccount ? "ServiceAccount" : "Password"),
                        new XElement(Ns + "RunLevel", "HighestAvailable"))),
                new XElement(Ns + "Settings",
                    // The anacron equivalent. Without it, a machine that was switched off at
                    // the scheduled time simply skips that day and logs Event ID 153,
                    // "Missed task start rejected" - and nobody ever reads that.
                    new XElement(Ns + "StartWhenAvailable", definition.StartWhenAvailable),

                    // Both of these default to TRUE, and between them they silently stop the
                    // task on any laptop and on any VM whose host reports a battery.
                    new XElement(Ns + "DisallowStartIfOnBatteries", !definition.RunOnBatteries),
                    new XElement(Ns + "StopIfGoingOnBatteries", !definition.RunOnBatteries),

                    // Defaults to PT72H. A rotation wedged for three days is not a rotation.
                    new XElement(Ns + "ExecutionTimeLimit",
                        XmlDuration(definition.ExecutionTimeLimit)),

                    // A free second line of defence behind the Global\ mutex.
                    new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"),

                    new XElement(Ns + "AllowHardTerminate", false),
                    new XElement(Ns + "RunOnlyIfNetworkAvailable", false),
                    new XElement(Ns + "AllowStartOnDemand", true),
                    new XElement(Ns + "Enabled", true),
                    new XElement(Ns + "Hidden", false),
                    new XElement(Ns + "WakeToRun", false),
                    new XElement(Ns + "Priority", 7),
                    new XElement(Ns + "IdleSettings",
                        new XElement(Ns + "StopOnIdleEnd", false),
                        new XElement(Ns + "RestartOnIdle", false))),
                new XElement(Ns + "Actions",
                    new XAttribute("Context", "Author"),
                    new XElement(Ns + "Exec",
                        new XElement(Ns + "Command", definition.ExecutablePath),
                        new XElement(Ns + "Arguments", definition.Arguments)))));

        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        document.Save(writer);
        return writer.ToString();
    }

    /// <summary>Formats a duration the way the Task Scheduler schema expects.</summary>
    internal static string XmlDuration(TimeSpan value) =>
        value.TotalHours >= 1 && value.Minutes == 0 && value.Seconds == 0
            ? $"PT{(int)value.TotalHours}H"
            : $"PT{(int)value.TotalMinutes}M";
}
