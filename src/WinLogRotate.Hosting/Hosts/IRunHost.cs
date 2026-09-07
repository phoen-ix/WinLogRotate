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
}

/// <summary>Everything needed to register a run host.</summary>
public sealed record HostInstallOptions
{
    public required string ExecutablePath { get; init; }
    public required string ConfigDirectory { get; init; }
    public RunAccount Account { get; init; } = RunAccount.System;
    public HostFrequency Frequency { get; init; } = HostFrequency.Daily;
    public TimeSpan TimeOfDay { get; init; } = TimeSpan.FromHours(3);

    /// <summary>
    /// Arguments the registered host runs with.
    /// </summary>
    /// <remarks>
    /// <c>--lock-held-exit 0</c> is deliberate. Exit 3 means another run holds the gate, which
    /// is a normal outcome - but Task Scheduler renders it as 0x3 in the Last Run Result column
    /// and an administrator skimming that column reads it as a failure. The cost is that 0x0 no
    /// longer proves work happened, which is exactly why <c>host status</c>, the GUI and the
    /// Event Log all read the journal instead.
    /// </remarks>
    public string Arguments =>
        $"run --config-dir \"{ConfigDirectory}\" --lock-held-exit 0";
}
