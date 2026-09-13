using System.Globalization;
using WinLogRotate.Cli.Output;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Core.Configuration;

namespace WinLogRotate.Cli.Commands;

/// <summary>
/// Suspends rotations without unregistering the run host.
/// </summary>
/// <remarks>
/// A file with an expiry, rather than disabling the scheduled task. Three reasons: it is
/// auditable, it needs no privileged Win32 from the installer, and it expires on its own - so a
/// maintenance window that someone forgets to close does not silently stop rotating forever,
/// which is exactly how disks fill.
/// </remarks>
internal static class PauseCommand
{
    public const string PauseFileName = "pause";

    /// <summary>
    /// The longest pause accepted. A pause is a maintenance window, not a decommissioning.
    /// </summary>
    /// <remarks>
    /// Also what keeps the expiry arithmetic inside <see cref="DateTimeOffset"/>'s range: an
    /// absurd span used to overflow it and end the verb at exit 4.
    /// </remarks>
    public static readonly TimeSpan Longest = TimeSpan.FromDays(366);

    /// <summary>What decides whether this process is elevated. A seam for the tests.</summary>
    private static readonly Func<bool> Elevated = Privilege.IsElevated;

    public static int Run(CommandContext ctx, string? duration, string? configDir) =>
        Run(ctx, duration, InstallPaths.Resolve(configDir), elevated: null);

    /// <summary>The same, for a caller that already knows where the installation lives.</summary>
    internal static int Run(CommandContext ctx, string? duration, InstallPaths paths, Func<bool>? elevated)
    {
        var file = Path.Combine(paths.Root, PauseFileName);

        // The words just typed are judged before anything else, so a mistyped duration is LR1007
        // whoever typed it - and not a demand for administrator rights in order to be told so.
        TimeSpan? pause = null;

        if (duration is not (null or ""))
        {
            // The grammar --run-deadline and every duration key in the configuration use, so 2h
            // means the same thing everywhere it can be typed. This took hh:mm:ss alone.
            if (!ConfigBinder.TryParseDuration(duration, out var span) || span <= TimeSpan.Zero)
            {
                return Refusals.CannotUse<PauseResult>(
                    ctx, "host pause", duration, "a duration",
                    "Write it as 30m, 2h, 7d, or as 01:00:00.");
            }

            if (span > Longest)
            {
                return Refusals.CannotUse<PauseResult>(
                    ctx, "host pause", duration, "a pause of at most a year",
                    "A pause is for a maintenance window. To stop rotating for good, run 'winlogrotate host use none'.");
            }

            pause = span;
        }

        // The shape `job set` and `host use` use. A per-machine data root is writable by
        // administrators only, and an unelevated write there escaped as an
        // UnauthorizedAccessException that the guard reported as LR1006 - "a defect in the
        // product" - about a machine refusing correctly.
        if (paths.Scope == InstallScope.PerMachine && !(elevated ?? Elevated)())
        {
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Error,
                Code = DiagnosticCode.NeedsAdministrator,
                Message = "Pausing or resuming rotations on a per-machine installation needs administrator rights.",
                Remedy = "Run this from an elevated prompt.",
            });

            return ctx.Output.Complete<PauseResult>("host pause", ExitCode.Errors, null);
        }

        if (pause is null)
        {
            // Both outcomes are correct and both are exit 0, but only one of them changed
            // anything - and a null payload said neither. The console lines that did are a no-op
            // under --json.
            var wasPaused = File.Exists(file);

            if (wasPaused)
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    return Unwritable(ctx, file, e);
                }

                ctx.Output.Line("Rotations resumed.");
            }
            else
            {
                ctx.Output.Line("Rotations are not paused.");
            }

            return ctx.Output.Complete("host pause", ExitCode.Ok, new PauseResult
            {
                PausedUntil = null,
                Action = wasPaused ? "resumed" : "unchanged",
            });
        }

        var until = DateTimeOffset.UtcNow.Add(pause.Value);

        try
        {
            Directory.CreateDirectory(paths.Root);
            File.WriteAllText(file, until.ToString("O", CultureInfo.InvariantCulture));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Unwritable(ctx, file, e);
        }

        ctx.Output.Line($"Rotations paused until {until:u}. Run 'winlogrotate host pause' with no "
                      + "duration to resume early.");

        return ctx.Output.Complete("host pause", ExitCode.Ok, new PauseResult
        {
            PausedUntil = until.ToString("O", CultureInfo.InvariantCulture),
            Action = "paused",
        });
    }

    /// <summary>The pause file could not be written or removed; nothing changed.</summary>
    private static int Unwritable(CommandContext ctx, string file, Exception e)
    {
        ctx.Output.Diagnostic(new CliDiagnostic
        {
            Severity = Severity.Error,
            Code = DiagnosticCode.ConfigUnwritable,
            Message = $"The pause file could not be written: {e.GetType().Name}: {e.Message}",
            Path = file,
            Remedy = "Check free space and the permissions on the data directory. Nothing was changed.",
        });

        return ctx.Output.Complete<PauseResult>("host pause", ExitCode.Errors, null);
    }

    /// <summary>Reads the pause file, treating an expired or unreadable one as "not paused".</summary>
    public static DateTimeOffset? PausedUntil(InstallPaths paths)
    {
        var file = Path.Combine(paths.Root, PauseFileName);
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            var text = File.ReadAllText(file).Trim();
            if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var until))
            {
                return null;
            }

            // Expired: clean up so it cannot linger.
            if (until <= DateTimeOffset.UtcNow)
            {
                File.Delete(file);
                return null;
            }

            return until;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Unreadable means rotating, not stopping. A pause file we cannot read must never
            // become an indefinite outage - and one whose expired self cannot be deleted must
            // not become one either. Only IOException was named here, so a read-only attribute
            // on the file, or an ACL, made every scheduled run exit 4 until somebody noticed.
            return null;
        }
    }
}
