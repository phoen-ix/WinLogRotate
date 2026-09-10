using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading;
using WinLogRotate.Core.Hooks;
using WinLogRotate.Core.Notify;

namespace WinLogRotate.Hosting.Hooks;

/// <summary>
/// Runs a hook on this machine.
/// </summary>
/// <remarks>
/// <para>
/// Three primitives, none of which existed anywhere in this product before: spawning a bounded
/// child process, sending a service control code, and signalling a named kernel event.
/// </para>
/// <para>
/// <b>The pipe discipline is the whole of the first one.</b> The nearest existing code,
/// <c>TaskRunHost.TryRun</c>, reads stdout to the end, then stderr to the end, then waits - and
/// survives only because <c>schtasks</c> prints a line or two. A hook that writes more than the
/// pipe buffer holds under that pattern blocks in its own write while this side blocks in a read
/// it will never finish, and the rotation stops for ever. <c>CliRunner.RunAsync</c> learned the
/// other half and says so; this follows that, not <c>TryRun</c>.
/// </para>
/// <para>
/// <b>And the timeout has to actually kill.</b> Without it the child outlives the rotation, Task
/// Scheduler reaches <c>ExecutionTimeLimit</c> and reports <c>0x41306</c> - indistinguishable from
/// an operator pressing Stop, on a run that had succeeded. <c>Process.Kill(entireProcessTree)</c>
/// appears nowhere else in this repository; a hook that spawns a helper and exits would otherwise
/// leave the helper holding the pipe.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsHookHost : IHookHost
{
    /// <summary>How long a killed process is given to actually die before we stop waiting.</summary>
    private static readonly TimeSpan Reaping = TimeSpan.FromSeconds(5);

    /// <summary>How much of what a hook printed is kept for the diagnostic.</summary>
    private const int TailLength = 500;

    public HookOutcome Run(PlannedHook hook, TimeSpan timeout) => hook.Action.Scheme switch
    {
        HookScheme.Command => RunCommand(hook, timeout),
        HookScheme.Service => SendParamChange(hook),
        HookScheme.Event => SignalEvent(hook),
        _ => HookOutcome.CouldNotStart(
            $"{HookSchemes.Name(hook.Action.Scheme)}: is not something a hook host runs"),
    };

    private static HookOutcome RunCommand(PlannedHook hook, TimeSpan timeout)
    {
        var info = new ProcessStartInfo
        {
            FileName = hook.Program!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,

            // Explicit rather than inherited. A scheduled task's working directory is System32,
            // and a hook that writes a file relative to it would put it there - so the program's
            // own directory is both the more useful answer and the more predictable one.
            WorkingDirectory = Path.GetDirectoryName(hook.Program!) ?? string.Empty,
        };

        foreach (var argument in hook.Arguments)
        {
            // ArgumentList, never a joined string: it re-quotes each element on the way out, so
            // an argument containing a space or a quote cannot become two arguments - or, worse,
            // become part of the program name.
            info.ArgumentList.Add(argument);
        }

        var clock = Stopwatch.StartNew();
        using var process = new Process { StartInfo = info };

        try
        {
            process.Start();
        }
        catch (Win32Exception e)
        {
            // The ordinary way to get here is ERROR_FILE_NOT_FOUND: the executable named in the
            // configuration is not there. Reported, never thrown - see IHookHost.
            return HookOutcome.CouldNotStart($"{hook.Program} could not be started ({e.Message})");
        }

        // Started BEFORE the wait, both of them, and this ordering is the bug that is being
        // avoided rather than a stylistic preference.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit((int)Math.Min(timeout.TotalMilliseconds, int.MaxValue)))
        {
            return Kill(process, clock.Elapsed, timeout);
        }

        // The tasks complete when the pipes close, which is at exit - so this is a formality by
        // now, and bounded anyway so a grandchild still holding the handle cannot hang the run.
        Task.WhenAll(stdout, stderr).Wait(Reaping);

        var elapsed = clock.Elapsed;

        return process.ExitCode == 0
            ? HookOutcome.Succeeded(elapsed)
            : new HookOutcome
            {
                Result = HookResult.Failed,
                ExitCode = process.ExitCode,
                Detail = Tail(stderr, stdout),
                Elapsed = elapsed,
            };
    }

    private static HookOutcome Kill(Process process, TimeSpan elapsed, TimeSpan timeout)
    {
        try
        {
            // entireProcessTree, because a hook that launches a helper and returns would otherwise
            // leave the helper running - holding the pipe this side has already stopped reading,
            // and outliving the rotation it belonged to.
            process.Kill(entireProcessTree: true);
            process.WaitForExit((int)Reaping.TotalMilliseconds);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or AggregateException)
        {
            // It exited between the timeout expiring and the kill. Still a timeout: it did not
            // finish inside the limit, and saying otherwise would make the limit unenforceable.
        }

        return new HookOutcome
        {
            Result = HookResult.TimedOut,
            Detail = $"the limit was {timeout.TotalSeconds:0}s",
            Elapsed = elapsed,
        };
    }

    /// <summary>Whatever the hook said about itself, stderr first, bounded.</summary>
    private static string Tail(Task<string> stderr, Task<string> stdout)
    {
        var text = Read(stderr);

        if (text.Length == 0)
        {
            text = Read(stdout);
        }

        text = text.Trim();
        return text.Length <= TailLength ? text : "..." + text[^TailLength..];

        static string Read(Task<string> task) =>
            task.IsCompletedSuccessfully ? task.Result : string.Empty;
    }

    private static HookOutcome SendParamChange(PlannedHook hook)
    {
        var manager = ServiceControlNative.OpenSCManager(
            null, null, ServiceControlNative.ScManagerConnect);

        if (manager == nint.Zero)
        {
            return HookOutcome.CouldNotStart(
                $"the service control manager could not be opened ({Describe(LastError())})");
        }

        try
        {
            var service = ServiceControlNative.OpenService(
                manager, hook.Action.Target,
                ServiceControlNative.ServicePauseContinue | ServiceControlNative.ServiceQueryStatus);

            if (service == nint.Zero)
            {
                return HookOutcome.CouldNotStart(
                    $"'{hook.Action.Target}' could not be opened ({Describe(LastError())})");
            }

            try
            {
                var status = default(ServiceStatus);

                if (ServiceControlNative.ControlService(
                        service, ServiceControlNative.ControlParamChange, ref status))
                {
                    return HookOutcome.Succeeded(TimeSpan.Zero);
                }

                var error = LastError();

                return new HookOutcome
                {
                    Result = error == ServiceControlNative.ErrorServiceNotActive
                        ? HookResult.CouldNotStart
                        : HookResult.Failed,
                    ExitCode = error,
                    Detail = $"'{hook.Action.Target}' did not accept PARAMCHANGE ({Describe(error)})",
                };
            }
            finally
            {
                ServiceControlNative.CloseServiceHandle(service);
            }
        }
        finally
        {
            ServiceControlNative.CloseServiceHandle(manager);
        }
    }

    private static int LastError() => System.Runtime.InteropServices.Marshal.GetLastWin32Error();

    /// <summary>
    /// The four Win32 errors this call actually produces, in the operator's terms.
    /// </summary>
    /// <remarks>
    /// 1052 is the interesting one and the least self-explanatory: the service is running and
    /// simply does not handle PARAMCHANGE. That is a fact about the service, not a misconfiguration
    /// here, and the message has to say which - otherwise the obvious next move is to go and
    /// "fix" a hook that is written correctly.
    /// </remarks>
    private static string Describe(int error) => error switch
    {
        ServiceControlNative.ErrorServiceDoesNotExist => "there is no such service",
        ServiceControlNative.ErrorAccessDenied =>
            "access was denied - the run account may not control this service",
        ServiceControlNative.ErrorServiceNotActive => "the service is not running",
        ServiceControlNative.ErrorInvalidServiceControl =>
            "the service does not handle PARAMCHANGE; it may need a command: hook instead",
        _ => $"Win32 error {error}",
    };

    private static HookOutcome SignalEvent(PlannedHook hook)
    {
        try
        {
            // Opened, never created. EventWaitHandle's constructor would happily make an event
            // nobody is waiting on and then set it, and the hook would report success every night
            // while the program it was meant to poke was not even running - the silent-no-op shape
            // this whole milestone exists to remove. The GUI's quit listener creates its event
            // because it is the waiter; a hook never is.
            using var handle = EventWaitHandle.OpenExisting(hook.Action.Target);
            handle.Set();
            return HookOutcome.Succeeded(TimeSpan.Zero);
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return HookOutcome.CouldNotStart(
                $"no event named '{hook.Action.Target}' exists, so nothing is waiting on it");
        }
        catch (UnauthorizedAccessException)
        {
            return HookOutcome.CouldNotStart(
                $"'{hook.Action.Target}' exists but this account may not signal it");
        }
        catch (Exception e) when (e is IOException or ArgumentException)
        {
            return HookOutcome.CouldNotStart($"'{hook.Action.Target}' could not be signalled: {e.Message}");
        }
    }
}
