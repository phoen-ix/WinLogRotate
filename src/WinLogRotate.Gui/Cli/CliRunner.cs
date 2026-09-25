using System.ComponentModel;
using System.Diagnostics;
using System.Text;

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
                Verb = CliArgs.VerbOf(arguments),
                StdOut = await stdout.ConfigureAwait(false),
                StdErr = written,
            };
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 2)
        {
            return Failed(CliFailure.NotFound);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            // AppLocker (5), a corrupt image (193), a pipe that broke. Only code 2 used to be
            // caught, so the rest threw out of whichever page's load had asked - and the stock
            // exception dialog, not this product's, is what the operator saw on start-up.
            return Failed(CliFailure.CouldNotStart, e.Message);
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
    public Task<CliResult> RunElevatedAsync(
        IReadOnlyList<string> arguments,
        Action<string>? onLine = null,
        Action<int>? onStarted = null,
        CancellationToken cancellationToken = default) =>
        RunThroughFileAsync(arguments, elevated: true, onLine, onStarted, cancellationToken);

    /// <summary>
    /// Runs a verb unelevated, streaming its progress the same way an elevated one is streamed.
    /// </summary>
    /// <remarks>
    /// The same file and the same tail as <see cref="RunElevatedAsync"/>, so a page that
    /// shows progress does not need two code paths for the two tokens - which is what the
    /// Updates panel needs: a per-machine install updates elevated and a per-user one does
    /// not, and the download looks the same either way.
    /// </remarks>
    public Task<CliResult> RunStreamingAsync(
        IReadOnlyList<string> arguments,
        Action<string>? onLine = null,
        CancellationToken cancellationToken = default) =>
        RunThroughFileAsync(arguments, elevated: false, onLine, onStarted: null, cancellationToken);

    private async Task<CliResult> RunThroughFileAsync(
        IReadOnlyList<string> arguments,
        bool elevated,
        Action<string>? onLine,
        Action<int>? onStarted,
        CancellationToken cancellationToken)
    {
        var workDirectory = Path.Combine(Path.GetTempPath(), WorkDirectories.Name(Guid.NewGuid()));
        var eventFile = Path.Combine(workDirectory, "events.ndjson");

        var info = new ProcessStartInfo(ExecutablePath)
        {
            UseShellExecute = elevated,
            Verb = elevated ? "runas" : string.Empty,
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

        var ran = false;

        try
        {
            // Inside the try. An unwritable temporary folder threw from here, before any catch
            // below could turn it into a result, and the page that asked went down with it.
            Directory.CreateDirectory(workDirectory);

            using var process = Process.Start(info);
            if (process is null)
            {
                return Failed(CliFailure.NotFound);
            }

            ran = true;

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
                Verb = CliArgs.VerbOf(arguments),
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
        catch (Exception e) when (e is Win32Exception
                                      or InvalidOperationException
                                      or IOException
                                      or UnauthorizedAccessException)
        {
            // Everything else that stops a child running or being read: AppLocker, a corrupt
            // image, a temporary folder that cannot be written, an event file held open by an
            // antivirus scanner after the child exited. The system's sentence travels with it -
            // and so does whether the child ran, because "could not be run" about a Save that
            // had already written is the wrong thing to tell the person who pressed it.
            return Failed(ran ? CliFailure.CouldNotRead : CliFailure.CouldNotStart, e.Message);
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

    /// <summary>
    /// Removes the scratch directories earlier operations could not remove themselves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="TryCleanUp"/> has always said that what it leaves behind is swept on the next
    /// start, and for several milestones nothing did: the comment described a mechanism that did
    /// not exist. This is the mechanism. Called once, from <c>Program.Main</c>, before the window
    /// opens.
    /// </para>
    /// <para>
    /// Only directories nobody has written to for <see cref="WorkDirectories.Grace"/> are removed.
    /// A GUI that was killed leaves a child running, and that child is still appending to its
    /// file; deleting the directory from under it would be a worse outcome than the litter. The
    /// decision is <see cref="WorkDirectories.IsStale"/>, which is asserted where the tests run.
    /// </para>
    /// </remarks>
    public static void SweepStaleWork()
    {
        var now = DateTimeOffset.UtcNow;

        IEnumerable<string> stale;

        try
        {
            stale = Directory.EnumerateDirectories(Path.GetTempPath(), WorkDirectories.Pattern)
                .Where(d => WorkDirectories.IsStale(LastWrite(d), now))
                .ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A temporary folder that cannot be listed is not a reason to refuse to open.
            return;
        }

        foreach (var directory in stale)
        {
            TryCleanUp(directory);
        }
    }

    /// <summary>The newest write in a directory: the directory itself, or any file in it.</summary>
    private static DateTimeOffset LastWrite(string directory)
    {
        try
        {
            var newest = Directory.GetLastWriteTimeUtc(directory);

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var written = File.GetLastWriteTimeUtc(file);
                newest = written > newest ? written : newest;
            }

            return newest;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Unreadable is treated as just written: the sweep leaves it for next time.
            return DateTimeOffset.UtcNow;
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
            // purpose rather than retried here; SweepStaleWork removes it on the next start,
            // once nothing has written to it for an hour.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static CliResult Failed(CliFailure failure, string reason = "") =>
        new()
        {
            ExitCode = -1,
            StdOut = string.Empty,
            StdErr = string.Empty,
            Failure = failure,
            Reason = reason,
        };
}
