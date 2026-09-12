using System.Globalization;
using WinLogRotate.Cli.Output;
using WinLogRotate.Core;

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

    public static int Run(CommandContext ctx, string? duration, string? configDir)
    {
        var paths = InstallPaths.Resolve(configDir);
        var file = Path.Combine(paths.Root, PauseFileName);

        if (duration is null or "")
        {
            // Both outcomes are correct and both are exit 0, but only one of them changed
            // anything - and a null payload said neither. The console lines that did are a no-op
            // under --json.
            var wasPaused = File.Exists(file);

            if (wasPaused)
            {
                File.Delete(file);
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

        if (!TimeSpan.TryParse(duration, CultureInfo.InvariantCulture, out var span) || span <= TimeSpan.Zero)
        {
            return Refusals.CannotUse<PauseResult>(
                ctx, "host pause", duration, "a duration",
                "Use hh:mm:ss, for example 01:00:00 for one hour.");
        }

        var until = DateTimeOffset.UtcNow.Add(span);
        Directory.CreateDirectory(paths.Root);
        File.WriteAllText(file, until.ToString("O", CultureInfo.InvariantCulture));

        ctx.Output.Line($"Rotations paused until {until:u}. Run 'winlogrotate host pause' with no "
                      + "duration to resume early.");

        return ctx.Output.Complete("host pause", ExitCode.Ok, new PauseResult
        {
            PausedUntil = until.ToString("O", CultureInfo.InvariantCulture),
            Action = "paused",
        });
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
        catch (IOException)
        {
            // Unreadable means rotating, not stopping. A pause file we cannot read must never
            // become an indefinite outage.
            return null;
        }
    }
}
