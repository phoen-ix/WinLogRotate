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
    /// <summary>
    /// LocalSystem, identified by SID.
    /// </summary>
    /// <remarks>
    /// <para>
    /// S-1-5-18 rather than "NT AUTHORITY\SYSTEM" because that name is localised - a German
    /// machine calls it "NT-AUTORIT&#xC4;T\SYSTEM". Hardcoding the English form would fail on
    /// exactly the machines the /xml form of schtasks was chosen to protect against.
    /// </para>
    /// <para>
    /// Needs no password and holds the privileges file operations want, but authenticates on
    /// the network as DOMAIN\MACHINE$ - so a UNC log share must grant the computer account, or
    /// a domain account / gMSA must be used instead.
    /// </para>
    /// </remarks>
    public static RunAccount System { get; } = new() { UserId = "S-1-5-18", ServiceAccount = true };

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

    /// <summary>Builds the task, with its first run computed against <paramref name="clock"/>.</summary>
    public static string Build(TaskDefinition definition, TimeProvider clock)
    {
        // No offset, deliberately: Task Scheduler reads a bare StartBoundary as local time, so
        // it is computed on the local clock and written without one.
        var start = NextOccurrence(definition.TimeOfDay, definition.Frequency, clock.GetLocalNow())
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

                        // LogonType is OMITTED for a service account, and that is not an
                        // oversight. "ServiceAccount" is a value from the COM enumeration
                        // (TASK_LOGON_SERVICE_ACCOUNT); the XML schema's logonType has no such
                        // member - only S4U, Password, InteractiveToken,
                        // InteractiveTokenOrPassword, Group and None. Emitting it makes
                        // schtasks reject the whole file with
                        //   "The task XML contains a value which is incorrectly formatted or
                        //    out of range. (19,35):LogonType:ServiceAccount"
                        // and, because the installer treats a failed registration as
                        // non-fatal, the result is an install that reports success with nothing
                        // scheduled to run. That is what shipped in v0.0.1. A real
                        // schtasks /query /xml export of a SYSTEM task omits it too.
                        definition.Account.ServiceAccount
                            ? null
                            : new XElement(Ns + "LogonType", "Password"),

                        new XElement(Ns + "RunLevel", "HighestAvailable"))),
                // Ordered to match taskSchedulerSchema's documented settingsType sequence and
                // what schtasks itself emits, on the principle that resembling a known-good
                // export is free. Whether the scheduler actually enforces this order is not
                // established - the smoke test captures schtasks' own error now, which will
                // say so one way or the other.
                new XElement(Ns + "Settings",
                    new XElement(Ns + "AllowStartOnDemand", true),

                    // A free second line of defence behind the Global\ mutex.
                    new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"),

                    // Both of these default to TRUE, and between them they silently stop the
                    // task on any laptop and on any VM whose host reports a battery.
                    new XElement(Ns + "DisallowStartIfOnBatteries", !definition.RunOnBatteries),
                    new XElement(Ns + "StopIfGoingOnBatteries", !definition.RunOnBatteries),

                    // Task Scheduler's own default, restored. It was false here for a release,
                    // which made ExecutionTimeLimit below advisory: at the limit the scheduler
                    // then only asks the process to close, and a console process with no window
                    // has nothing that hears the request. A wedged rotation was never killed,
                    // held the gate for days, and the 0x41306 reasoning in docs/hooks.md and
                    // GateHoldRule rested on a kill that did not happen. True makes the limit
                    // a kill.
                    new XElement(Ns + "AllowHardTerminate", true),

                    // The anacron equivalent. Without it, a machine that was switched off at
                    // the scheduled time simply skips that day and logs Event ID 153,
                    // "Missed task start rejected" - and nobody ever reads that.
                    new XElement(Ns + "StartWhenAvailable", definition.StartWhenAvailable),

                    new XElement(Ns + "RunOnlyIfNetworkAvailable", false),
                    new XElement(Ns + "WakeToRun", false),
                    new XElement(Ns + "Enabled", true),
                    new XElement(Ns + "Hidden", false),
                    new XElement(Ns + "IdleSettings",
                        new XElement(Ns + "StopOnIdleEnd", false),
                        new XElement(Ns + "RestartOnIdle", false)),

                    // Defaults to PT72H. A rotation wedged for three days is not a rotation.
                    new XElement(Ns + "ExecutionTimeLimit", XmlDuration(definition.ExecutionTimeLimit)),

                    new XElement(Ns + "Priority", 7)),
                new XElement(Ns + "Actions",
                    new XAttribute("Context", "Author"),
                    new XElement(Ns + "Exec",
                        new XElement(Ns + "Command", definition.ExecutablePath),
                        new XElement(Ns + "Arguments", definition.Arguments)))));

        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        document.Save(writer);
        return writer.ToString();
    }

    /// <summary>
    /// The first run: the next time the cadence would fire after <paramref name="now"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This used to be today at <paramref name="timeOfDay"/>, whatever the time of day. With
    /// StartWhenAvailable set - and it is, deliberately - a task registered at 14:00 had a start
    /// eleven hours in its past, which Task Scheduler treats as a missed run and catches up at
    /// once. So <c>host use task</c>, and the installer that shells it, rotated the machine's
    /// logs there and then, in the middle of the working day.
    /// </para>
    /// <para>
    /// A start equal to <paramref name="now"/> counts as gone, for the same reason. The hourly
    /// cadence is a daily trigger repeating every hour, so any slot on its hourly grid serves as
    /// the anchor and the next one is used - tomorrow's 03:00 would leave an hourly rotation idle
    /// for most of a day. Wall-clock arithmetic throughout: the offset is dropped because the
    /// boundary is written without one and Task Scheduler reads it as local.
    /// </para>
    /// </remarks>
    internal static DateTime NextOccurrence(TimeSpan timeOfDay, HostFrequency frequency, DateTimeOffset now)
    {
        var wall = now.DateTime;
        var anchor = wall.Date.Add(timeOfDay);

        if (anchor > wall)
        {
            return anchor;
        }

        var interval = frequency == HostFrequency.Hourly ? TimeSpan.FromHours(1) : TimeSpan.FromDays(1);
        var missed = Math.Floor((wall - anchor) / interval) + 1;

        return anchor.Add(interval * missed);
    }

    /// <summary>Formats a duration the way the Task Scheduler schema expects.</summary>
    internal static string XmlDuration(TimeSpan value) =>
        value.TotalHours >= 1 && value.Minutes == 0 && value.Seconds == 0
            ? $"PT{(int)value.TotalHours}H"
            : $"PT{(int)value.TotalMinutes}M";
}
