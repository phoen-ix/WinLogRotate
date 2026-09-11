using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

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
[SupportedOSPlatform("windows")]
public sealed class TaskRunHost : IRunHost
{
    public RunHostKind Kind => RunHostKind.Task;

    public void Install(HostInstallOptions options)
    {
        var xml = TaskXmlBuilder.Build(options.ToTaskDefinition());

        // Task Scheduler requires UTF-16 for a task XML file; UTF-8 is rejected with an
        // unhelpfully generic error.
        var temp = Path.Combine(Path.GetTempPath(), $"winlogrotate-task-{Guid.NewGuid():N}.xml");
        File.WriteAllText(temp, xml, System.Text.Encoding.Unicode);

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
                if (key?.GetValue("HostKind") is string kind
                    && Enum.TryParse<RunHostKind>(kind, ignoreCase: true, out var parsed))
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

    private static void Run(string exe, string[] arguments, bool throwOnFailure = true)
    {
        var (exitCode, output) = TryRun(exe, arguments);
        if (exitCode != 0 && throwOnFailure)
        {
            throw new InvalidOperationException(
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

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException($"Could not start {exe}.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, stdout + stderr);
    }
}
