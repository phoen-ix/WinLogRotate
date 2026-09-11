using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using WinLogRotate.Core;

namespace WinLogRotate.Gui.Cli;

/// <summary>
/// Runs the command-line engine on the GUI's behalf.
/// </summary>
/// <remarks>
/// The GUI never reimplements anything the CLI does. Every action shells out, which is what
/// guarantees the two can never disagree about what a rotation means - and it gives elevation
/// per operation rather than for the whole window.
/// </remarks>
public sealed class CliRunner(string executablePath)
{
    public string ExecutablePath { get; } = executablePath;

    /// <summary>
    /// Locates <c>winlogrotate.exe</c>.
    /// </summary>
    /// <remarks>
    /// Beside this executable first, then the recorded install location. Deliberately never the
    /// current working directory: this path is launched elevated, and a planted
    /// <c>winlogrotate.exe</c> in a user-writable working directory would then run as
    /// administrator.
    /// </remarks>
    public static CliRunner Resolve()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "winlogrotate.exe");
        if (File.Exists(beside))
        {
            return new CliRunner(beside);
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
            {
                using var hive = Microsoft.Win32.RegistryKey.OpenBaseKey(
                    Microsoft.Win32.RegistryHive.LocalMachine, view);
                using var key = hive.OpenSubKey(Hosting.Names.UninstallKey);

                if (key?.GetValue("InstallLocation") is string location)
                {
                    var candidate = Path.Combine(location, "winlogrotate.exe");
                    if (File.Exists(candidate))
                    {
                        return new CliRunner(candidate);
                    }
                }
            }
        }

        return new CliRunner("winlogrotate.exe");
    }

    /// <summary>Runs a verb unelevated and captures its output.</summary>
    public async Task<CliResult> RunAsync(
        IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        var info = new ProcessStartInfo(ExecutablePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        info.ArgumentList.Add("--no-color");

        try
        {
            using var process = Process.Start(info);
            if (process is null)
            {
                return Failed(CliFailure.NotFound);
            }

            // Both pipes are drained on their own tasks, started BEFORE waiting for exit. Doing
            // it the other way round deadlocks the first time a run writes more than the pipe
            // buffer holds - which for a rotation is immediately.
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            var written = await stderr.ConfigureAwait(false);

            return new CliResult
            {
                ExitCode = process.ExitCode,
                StdOut = await stdout.ConfigureAwait(false),
                StdErr = written,
            };
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 2)
        {
            return Failed(CliFailure.NotFound);
        }
        catch (OperationCanceledException)
        {
            return Failed(CliFailure.Timeout);
        }
    }

    /// <summary>
    /// Runs a verb elevated, streaming its progress.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the awkward one, and worth doing properly. Elevation needs
    /// <c>Verb = "runas"</c>, which needs <c>UseShellExecute = true</c>, which forbids
    /// redirecting the child's pipes. The obvious consequence is that every privileged
    /// operation shows a frozen window and then a result - which is exactly what makes admin
    /// tools feel broken.
    /// </para>
    /// <para>
    /// So the child writes newline-delimited JSON to a file under the user's own temporary
    /// directory - inheriting that directory's permissions, which this does not set itself - and
    /// we tail it. The event pipeline downstream is then identical for elevated and unelevated
    /// runs. Nothing sensitive goes through it; a credential travels by pipe precisely because
    /// this channel is a file.
    /// </para>
    /// </remarks>
    /// <param name="onStarted">
    /// Called on a thread-pool thread with the elevated child's process id, as soon as it exists.
    /// </param>
    /// <remarks>
    /// <para>
    /// <paramref name="onStarted"/> is how a credential reaches the child: the caller uses the id
    /// to prove which process may open its pipe. It runs on the pool rather than inline because
    /// it blocks until the child connects, and inline it would block whichever thread called this
    /// - which is the UI thread, for as long as the child takes to start.
    /// </para>
    /// </remarks>
    public async Task<CliResult> RunElevatedAsync(
        IReadOnlyList<string> arguments,
        Action<string>? onLine = null,
        Action<int>? onStarted = null,
        CancellationToken cancellationToken = default)
    {
        var workDirectory = Path.Combine(
            Path.GetTempPath(), $"WinLogRotate-op-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDirectory);
        var eventFile = Path.Combine(workDirectory, "events.ndjson");

        var info = new ProcessStartInfo(ExecutablePath)
        {
            UseShellExecute = true,
            Verb = "runas",
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        info.ArgumentList.Add("--json-stream");
        info.ArgumentList.Add("--output");
        info.ArgumentList.Add(eventFile);

        try
        {
            using var process = Process.Start(info);
            if (process is null)
            {
                return Failed(CliFailure.NotFound);
            }

            var tail = EventTail.FollowAsync(
                eventFile, onLine, () => process.HasExited, cancellationToken);

            var started = onStarted is null
                ? Task.CompletedTask
                : Task.Run(() => onStarted(process.Id), cancellationToken);

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await tail.ConfigureAwait(false);
            await started.ConfigureAwait(false);

            var written = File.Exists(eventFile)
                ? await File.ReadAllTextAsync(eventFile, cancellationToken).ConfigureAwait(false)
                : string.Empty;

            return new CliResult
            {
                ExitCode = process.ExitCode,
                StdOut = written,

                // Genuinely empty, and not a stand-in for "we did not look": a runas child
                // cannot have its pipes redirected, so there is no stderr to capture. What it
                // would have written there is in the envelope instead.
                StdErr = string.Empty,
            };
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            // The user answered No to the UAC prompt. Not an error, and never a stack trace.
            return Failed(CliFailure.UacDeclined);
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 2)
        {
            // ERROR_FILE_NOT_FOUND, as RunAsync already handles. Without this a GUI installed
            // beside a deleted winlogrotate.exe throws out of an async void handler, which ends
            // the process instead of showing the one dialog that explains it.
            return Failed(CliFailure.NotFound);
        }
        catch (OperationCanceledException)
        {
            return Failed(CliFailure.Timeout);
        }
        finally
        {
            TryCleanUp(workDirectory);
        }
    }

    private static void TryCleanUp(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // An elevated child may still be exiting and holding the file. Left behind on
            // purpose rather than retried here; the next GUI start sweeps WinLogRotate-op-*.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static CliResult Failed(CliFailure failure) =>
        new()
        {
            ExitCode = -1,
            StdOut = string.Empty,
            StdErr = string.Empty,
            Failure = failure,
        };
}
