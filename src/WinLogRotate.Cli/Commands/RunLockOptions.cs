namespace WinLogRotate.Cli.Commands;

/// <summary>
/// How this invocation should treat the machine-wide rotation gate.
/// </summary>
/// <remarks>
/// <para>
/// Grouped into a record rather than threaded through as three more parameters, because they are
/// one decision - "may this run proceed, and what does it say if not" - and because
/// <see cref="RunCommand.Run"/> already takes four arguments.
/// </para>
/// <para>
/// These options were declared on the verb and never wired: <c>RunCommand.Run</c> did not accept
/// them, and nothing outside <c>RotationGate.cs</c> ever referenced the gate. So the scheduled
/// task's <c>--lock-held-exit 0</c> was parsed and discarded, and two concurrent runs would both
/// rotate the same files - which the class comment on RotationGate calls the worst bug this
/// product could have.
/// </para>
/// </remarks>
internal sealed record RunLockOptions
{
    /// <summary>Do not take the gate at all.</summary>
    public bool Skip { get; init; }

    /// <summary>Block until it is free instead of giving up immediately.</summary>
    public bool Wait { get; init; }

    /// <summary>
    /// How long to block for when <see cref="Wait"/> is set.
    /// </summary>
    /// <remarks>
    /// Bounded rather than infinite. A run that waits for ever is a run that Task Scheduler
    /// eventually kills at ExecutionTimeLimit, reporting 0x41306 - indistinguishable from an
    /// operator pressing Stop. Giving up cleanly with a recorded reason beats that.
    /// </remarks>
    public TimeSpan WaitFor { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// What to exit with when another run holds the gate.
    /// </summary>
    /// <remarks>
    /// The registered task passes 0. Task Scheduler renders exit 3 as 0x3, which an
    /// administrator reads as a failure - and "another rotation was already running" is not one.
    /// The cost is that 0x0 stops proving work happened, which is why the GUI and the Event
    /// Log report from the journal instead. That is an invariant, because reading LastTaskResult
    /// is the tempting shortcut.
    /// </remarks>
    public int HeldExitCode { get; init; } = Core.ExitCode.LockHeld;
}
