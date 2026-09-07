using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using WinLogRotate.Core;

namespace WinLogRotate.Gui.Cli;

/// <summary>Why an invocation did not produce a result.</summary>
public enum CliFailure
{
    None,
    NotFound,
    UacDeclined,
    Timeout,
    Crashed,
}

/// <summary>What one invocation produced.</summary>
public sealed record CliResult
{
    public required int ExitCode { get; init; }
    public required string StdOut { get; init; }
    public required string StdErr { get; init; }
    public CliFailure Failure { get; init; }

    // Core.ExitCode is qualified because the property below shadows the type name inside
    // this record.
    public bool Ok => Failure == CliFailure.None && ExitCode == Core.ExitCode.Ok;

    /// <summary>Plain wording for an exit code, so a dialog never shows a bare number.</summary>
    public string Describe() => Failure switch
    {
        CliFailure.NotFound => "winlogrotate.exe could not be found.",
        CliFailure.UacDeclined => "Elevation was cancelled. Nothing was changed.",
        CliFailure.Timeout => "The operation took too long and was stopped.",
        CliFailure.Crashed => "The operation ended unexpectedly.",
        _ => ExitCode switch
        {
            Core.ExitCode.Ok => "Completed.",
            Core.ExitCode.Errors => "Completed, but some files could not be rotated.",
            Core.ExitCode.ConfigInvalid => "The configuration has errors, so nothing was attempted.",
            Core.ExitCode.LockHeld => "Another rotation is already running.",
            _ => $"Exited with code {ExitCode}.",
        },
    };
}

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

            return new CliResult
            {
                ExitCode = process.ExitCode,
                StdOut = await stdout.ConfigureAwait(false),
                StdErr = await stderr.ConfigureAwait(false),
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
    /// So the child writes newline-delimited JSON to a file in a directory only this user and
    /// administrators can read, and we tail it. The event pipeline downstream is then identical
    /// for elevated and unelevated runs.
    /// </para>
    /// </remarks>
    public async Task<CliResult> RunElevatedAsync(
        IReadOnlyList<string> arguments,
        Action<string>? onLine = null,
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

            var tail = TailAsync(eventFile, onLine, process, cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await tail.ConfigureAwait(false);

            return new CliResult
            {
                ExitCode = process.ExitCode,
                StdOut = File.Exists(eventFile) ? await File.ReadAllTextAsync(eventFile, cancellationToken).ConfigureAwait(false) : "",
                StdErr = string.Empty,
            };
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            // The user answered No to the UAC prompt. Not an error, and never a stack trace.
            return Failed(CliFailure.UacDeclined);
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

    private static async Task TailAsync(
        string path, Action<string>? onLine, Process process, CancellationToken cancellationToken)
    {
        if (onLine is null)
        {
            return;
        }

        var offset = 0L;

        while (!process.HasExited && !cancellationToken.IsCancellationRequested)
        {
            offset = ReadFrom(path, offset, onLine);
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        // One last pass: the child may have written its final lines between our last poll and
        // its exit, and those are usually the ones that say what happened.
        ReadFrom(path, offset, onLine);
    }

    private static long ReadFrom(string path, long offset, Action<string> onLine)
    {
        if (!File.Exists(path))
        {
            return offset;
        }

        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            stream.Seek(offset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);

            while (reader.ReadLine() is { } line)
            {
                if (line.Length > 0)
                {
                    onLine(line);
                }
            }

            return stream.Position;
        }
        catch (IOException)
        {
            // The child is mid-write. Try again on the next poll.
            return offset;
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
        new() { ExitCode = -1, StdOut = string.Empty, StdErr = string.Empty, Failure = failure };
}
