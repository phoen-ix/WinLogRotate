using WinLogRotate.Core.Notify;

namespace WinLogRotate.Core.Hooks;

/// <summary>Which bracket a hook sits in.</summary>
public enum HookStage
{
    /// <summary>Before anything is touched. A failure here skips the job.</summary>
    PreRotate,

    /// <summary>After the files have moved. A failure here is an error; the rotation stands.</summary>
    PostRotate,
}

/// <summary>One hook this run intends to execute.</summary>
public sealed record PlannedHook
{
    public required HookAction Action { get; init; }
    public required HookStage Stage { get; init; }
    public required string JobName { get; init; }

    /// <summary>The executable, for <see cref="HookScheme.Command"/>. Null for every other scheme.</summary>
    public string? Program { get; init; }

    /// <summary>Already split, so nothing downstream re-parses a command line.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>What a journal line or a diagnostic prints. Masked, like everything else.</summary>
    public string Display => Action.Display;

    public string StageName => Stage == HookStage.PreRotate ? "prerotate" : "postrotate";
}

/// <summary>How a hook ended.</summary>
public enum HookResult
{
    Ok,

    /// <summary>It ran to completion and said it failed.</summary>
    Failed,

    /// <summary>It was still running when its timeout expired, and was killed.</summary>
    TimedOut,

    /// <summary>
    /// It never started - a missing executable, a service that does not exist, an event nobody
    /// has created.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="Failed"/> because the remedies have nothing in common: one is
    /// "fix the thing the hook does", the other is "fix the hook".
    /// </remarks>
    CouldNotStart,
}

/// <summary>What running one hook produced.</summary>
public sealed record HookOutcome
{
    public required HookResult Result { get; init; }

    /// <summary>The process exit code, where there was a process. Zero otherwise.</summary>
    public int ExitCode { get; init; }

    /// <summary>Why it failed, or the tail of what it printed. Never null on a failure.</summary>
    public string? Detail { get; init; }

    public TimeSpan Elapsed { get; init; }

    public bool Ok => Result == HookResult.Ok;

    public static HookOutcome Succeeded(TimeSpan elapsed) =>
        new() { Result = HookResult.Ok, Elapsed = elapsed };

    public static HookOutcome CouldNotStart(string detail) =>
        new() { Result = HookResult.CouldNotStart, Detail = detail };
}

/// <summary>
/// Runs one hook, on this machine.
/// </summary>
/// <remarks>
/// <para>
/// The seam that keeps the engine platform-neutral: spawning a process, sending a service control
/// code and signalling a kernel event are all Win32, and Core cannot reference Hosting. It is also
/// what lets the whole bracket - the gate, the timeouts, the skip-on-prerotate-failure rule - be
/// driven by a fake and proved on the Linux leg.
/// </para>
/// <para>
/// <b>Never throws.</b> An implementation that lets an exception out puts it in the middle of a
/// rotation loop that was previously incapable of producing one, which is precisely the defect
/// this milestone was written around.
/// </para>
/// </remarks>
public interface IHookHost
{
    HookOutcome Run(PlannedHook hook, TimeSpan timeout);
}
