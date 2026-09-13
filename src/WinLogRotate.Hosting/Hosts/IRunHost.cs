namespace WinLogRotate.Hosting.Hosts;

/// <summary>What runs rotations on this machine.</summary>
public enum RunHostKind
{
    /// <summary>Nothing is registered; the operator drives it themselves.</summary>
    None,

    /// <summary>A Windows Scheduled Task. The default, and the right answer for a log rotator:
    /// no resident process, missed runs caught up after a reboot, and visible in a console
    /// every administrator already knows.</summary>
    Task,

    /// <summary>A Windows Service, for people who want a resident agent.</summary>
    Service,
}

/// <summary>What is actually registered, versus what the configuration expects.</summary>
public sealed record HostStatus
{
    public required RunHostKind Configured { get; init; }
    public required RunHostKind Actual { get; init; }
    public bool Registered { get; init; }
    public string? Detail { get; init; }
    public DateTimeOffset? NextRun { get; init; }

    /// <summary>True when reality and configuration disagree - usually because somebody deleted
    /// the task by hand, which is a thing that happens and should be reported rather than
    /// silently re-created.</summary>
    public bool Drifted => Configured != Actual;
}

/// <summary>One way of running rotations.</summary>
public interface IRunHost
{
    RunHostKind Kind { get; }

    /// <summary>Registers this host. Idempotent: registering twice is not an error, because the
    /// installer, the GUI and the CLI all call the same path and may overlap.</summary>
    void Install(HostInstallOptions options);

    /// <summary>Removes it. Also idempotent - removing something absent is the desired end
    /// state, not a failure.</summary>
    void Uninstall();

    HostStatus Query();

    /// <summary>
    /// Records what this install is now set up to use, where <see cref="HostStatus.Configured"/>
    /// reads it from.
    /// </summary>
    /// <remarks>
    /// The installer writes that fact once, at install time, and nothing else ever did: after the
    /// operator's own <c>host use none</c> - or the GUI's Scheduling page, which shells the same
    /// verb - <see cref="HostStatus.Drifted"/> stayed true for ever, and every <c>host status</c>
    /// warned that "something removed" a task the operator had removed themselves. A failure to
    /// record is swallowed: it costs a stale drift warning, not a rotation.
    /// </remarks>
    void Record(RunHostKind configured);
}

/// <summary>
/// The run host could not be registered or removed, and says what the registrar said.
/// </summary>
/// <remarks>
/// Its own type so a verb can tell "schtasks refused" from a defect. It used to surface as
/// <see cref="InvalidOperationException"/>, which nothing caught, so a registration a Group
/// Policy forbade was reported as <c>LR1006</c>, "a defect in the product", with exit 4 - after
/// the working task had already been deleted.
/// </remarks>
public sealed class HostRegistrationException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>Everything needed to register a run host.</summary>
public sealed record HostInstallOptions
{
    public required string ExecutablePath { get; init; }
    public required string ConfigDirectory { get; init; }
    public RunAccount Account { get; init; } = RunAccount.System;
    public HostFrequency Frequency { get; init; } = HostFrequency.Daily;
    public TimeSpan TimeOfDay { get; init; } = TimeSpan.FromHours(3);

    /// <summary>
    /// How long the host lets one run take before killing it.
    /// </summary>
    /// <remarks>
    /// The single source of truth for two things that must agree: the task's
    /// <c>ExecutionTimeLimit</c> element, and the <c>--run-deadline</c> argument the task passes
    /// to the run it starts. If they disagree, the notification phase reserves time against a
    /// limit that is not the real one - which is worse than not reserving at all, because it
    /// looks handled. <c>TheRegisteredTaskAndItsDeadlineFlagAgree</c> reads both out of the
    /// generated XML and asserts they denote the same span.
    /// </remarks>
    public TimeSpan ExecutionTimeLimit { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Arguments the registered host runs with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>--lock-held-exit 0</c> is deliberate. Exit 3 means another run holds the gate, which
    /// is a normal outcome - but Task Scheduler renders it as 0x3 in the Last Run Result column
    /// and an administrator skimming that column reads it as a failure. The cost is that 0x0 no
    /// longer proves work happened, which is exactly why the GUI and the Event Log report
    /// from the journal instead.
    /// </para>
    /// <para>
    /// <c>--run-deadline</c> tells the run how long it has, so the notification phase can stop
    /// short of the kill rather than be terminated inside it. An existing installation keeps the
    /// command line it was registered with until <c>winlogrotate host repair</c> re-registers the
    /// task; without the flag there is simply no clamp, which is the pre-milestone-10 behaviour.
    /// </para>
    /// </remarks>
    public string Arguments =>
        $"run --config-dir \"{ConfigDirectory}\" --lock-held-exit 0 "
        + $"--run-deadline {ExecutionTimeLimit.ToString("c", System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>
    /// The task definition these options describe.
    /// </summary>
    /// <remarks>
    /// The one mapping, used by both the registrar and <c>host export-task</c>. They used to build
    /// a <see cref="TaskDefinition"/> each, and the exporter reached
    /// <see cref="Arguments"/> by constructing a throwaway copy of this record with an empty
    /// executable path - so a field added to one and not the other produced an exported task that
    /// differed from the registered one in exactly the way nobody would think to check.
    /// </remarks>
    public TaskDefinition ToTaskDefinition() => new()
    {
        ExecutablePath = ExecutablePath,
        Arguments = Arguments,
        Account = Account,
        Frequency = Frequency,
        TimeOfDay = TimeOfDay,
        ExecutionTimeLimit = ExecutionTimeLimit,
    };
}
