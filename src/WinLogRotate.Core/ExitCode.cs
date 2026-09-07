namespace WinLogRotate.Core;

/// <summary>
/// Process exit codes. 0, 1 and 3 match logrotate exactly so existing monitoring and
/// runbooks transfer; 2 is ours.
/// </summary>
public static class ExitCode
{
    /// <summary>Everything asked for was done.</summary>
    public const int Ok = 0;

    /// <summary>One or more errors occurred while rotating. State is still written -
    /// deliberately, and matching upstream: a failing job must not cause a retry storm
    /// that rotates the healthy ones over and over.</summary>
    public const int Errors = 1;

    /// <summary>The configuration is invalid and NOTHING was attempted.
    /// <para>
    /// Not a logrotate code - upstream folds this into 1. Windows monitoring keys off exit
    /// codes far harder than cron does, and "your config has a typo" is genuinely a
    /// different condition from "we rotated 40 logs and one was locked". Nothing on disk
    /// was touched when you see this.
    /// </para></summary>
    public const int ConfigInvalid = 2;

    /// <summary>Another run holds the rotation lock. Not an error - the expected outcome
    /// when a manual run overlaps the scheduled one.
    /// <para>
    /// Task Scheduler shows this as 0x3, which reads as a failure to an admin skimming the
    /// Last Run Result column, so the task we register passes --lock-held-exit 0. The cost
    /// is that 0x0 no longer proves work happened, which is why `host status`, the GUI and
    /// the Event Log all read the journal and never LastTaskResult.
    /// </para></summary>
    public const int LockHeld = 3;
}
