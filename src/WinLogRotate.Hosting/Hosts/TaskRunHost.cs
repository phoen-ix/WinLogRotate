using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;
using WinLogRotate.Core.Configuration;

namespace WinLogRotate.Hosting.Hosts;

/// <summary>
/// Registers rotations as a Windows Scheduled Task.
/// </summary>
/// <remarks>
/// Driven through <c>schtasks /xml</c> rather than the COM API, so the whole path stays usable
/// from a NativeAOT binary and on Server Core. The XML form is also the locale-independent one:
/// the flag form parses dates and times according to the machine's locale and simply fails on a
/// German or Turkish system.
/// </remarks>
/// <param name="clock">Decides the task's first run; see <see cref="TaskXmlBuilder.NextOccurrence"/>.</param>
[SupportedOSPlatform("windows")]
public sealed class TaskRunHost(TimeProvider clock) : IRunHost
{
    public RunHostKind Kind => RunHostKind.Task;

    /// <summary>
    /// Registers the task, replacing whatever is registered under its name.
    /// </summary>
    /// <remarks>
    /// <c>/f</c> is what makes this safe to call over a working task: the replacement is accepted
    /// or refused as a whole, and a refusal leaves the existing registration exactly as it was.
    /// That is why <c>host use task</c> does not remove the old task first - it used to, and a
    /// registration schtasks then refused left nothing running rotations at all.
    /// </remarks>
    /// <exception cref="HostRegistrationException">schtasks refused, or could not be run.</exception>
    public void Install(HostInstallOptions options)
    {
        var xml = TaskXmlBuilder.Build(options.ToTaskDefinition(), clock);

        // Task Scheduler requires UTF-16 for a task XML file; UTF-8 is rejected with an
        // unhelpfully generic error.
        var temp = Path.Combine(Path.GetTempPath(), $"winlogrotate-task-{Guid.NewGuid():N}.xml");

        try
        {
            File.WriteAllText(temp, xml, System.Text.Encoding.Unicode);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new HostRegistrationException(
                $"The task definition could not be written to '{temp}': {e.Message}", e);
        }

        try
        {
            // /f so re-registering replaces rather than failing: the installer, the GUI and the
            // CLI all reach this path and may well overlap.
            Run("schtasks.exe",
                ["/create", "/tn", Names.TaskPath, "/xml", temp, "/f"]);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    public void Uninstall()
    {
        // Removing something already absent is the desired end state, so a failure here is only
        // interesting if the task exists and will not go.
        Run("schtasks.exe", ["/delete", "/tn", Names.TaskPath, "/f"], throwOnFailure: false);
    }

    public HostStatus Query()
    {
        var (exitCode, output) = TryRun("schtasks.exe", ["/query", "/tn", Names.TaskPath, "/fo", "list"]);

        return exitCode == 0
            ? new HostStatus
            {
                Configured = Configured(),
                Actual = RunHostKind.Task,
                Registered = true,
                Detail = output.Trim(),
            }
            : new HostStatus
            {
                Configured = Configured(),
                Actual = RunHostKind.None,
                Registered = false,
                Detail = "No scheduled task is registered.",
            };
    }

    /// <summary>
    /// What the install was set up to use, according to the installer.
    /// </summary>
    /// <remarks>
    /// Read rather than assumed. This used to return <c>Task</c> unconditionally, which made
    /// <see cref="HostStatus.Drifted"/> unable to tell "somebody deleted the task" from "this
    /// install was never given one" - and those need opposite responses. A portable copy, or one
    /// installed with <c>/HOST=none</c>, has nothing registered on purpose.
    /// </remarks>
    private static RunHostKind Configured()
    {
        try
        {
            foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                using var key = hive.OpenSubKey(Names.UninstallKey);
                if (key?.GetValue(Names.HostKindValue) is string kind
                    && ConfigBinder.TryParseName<RunHostKind>(kind, out var parsed))
                {
                    return parsed;
                }
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException)
        {
            // A reader who cannot see the key learns nothing, which is not the same as learning
            // that nothing is configured - so say None and let the caller report only what it can
            // actually see.
        }

        return RunHostKind.None;
    }

    /// <summary>
    /// Writes <c>HostKind</c> where <see cref="Configured"/> reads it: the uninstall key of
    /// whichever scope this machine has an install under, in the installer's own lower-case
    /// spelling. A portable copy has no uninstall key, records nothing, and therefore never drifts.
    /// </summary>
    public void Record(RunHostKind configured)
    {
        try
        {
            foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                using var key = hive.OpenSubKey(Names.UninstallKey, writable: true);
                if (key is not null)
                {
                    key.SetValue(Names.HostKindValue, configured.ToString().ToLowerInvariant(), RegistryValueKind.String);
                    return;
                }
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // The switch itself succeeded; what is lost is only the record of it, and the cost of
            // that is a drift warning from `host status`, which its remedy already tells the
            // operator how to clear.
        }
    }

    private static void Run(string exe, string[] arguments, bool throwOnFailure = true)
    {
        var (exitCode, output) = TryRun(exe, arguments);
        if (exitCode != 0 && throwOnFailure)
        {
            throw new HostRegistrationException(
                $"{exe} {string.Join(' ', arguments)} exited {exitCode}: {output.Trim()}");
        }
    }

    private static (int ExitCode, string Output) TryRun(string exe, string[] arguments)
    {
        // Fully qualified: we may be running elevated, and resolving a bare name through PATH
        // would let a planted executable in a writable directory run as SYSTEM.
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), exe);

        var info = new ProcessStartInfo(File.Exists(path) ? path : exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(info)
                ?? throw new HostRegistrationException($"Could not start {exe}.");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // AppLocker, a missing System32 binary, or a process limit: the machine refusing,
            // not the product failing.
            throw new HostRegistrationException($"Could not start {exe}: {e.Message}", e);
        }

        using var _ = process;

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, stdout + stderr);
    }
}
