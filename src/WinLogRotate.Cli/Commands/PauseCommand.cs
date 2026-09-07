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
            if (File.Exists(file))
            {
                File.Delete(file);
                ctx.Output.Line("Rotations resumed.");
            }
            else
            {
                ctx.Output.Line("Rotations are not paused.");
            }

            return ctx.Output.Complete<PauseResult>("host pause", ExitCode.Ok, null);
        }

        if (!TimeSpan.TryParse(duration, CultureInfo.InvariantCulture, out var span) || span <= TimeSpan.Zero)
        {
            ctx.Output.Line($"winlogrotate: could not read '{duration}' as a duration.");
            ctx.Output.Line("Use hh:mm:ss, for example 01:00:00 for one hour.");
            return ctx.Output.Complete<PauseResult>("host pause", ExitCode.ConfigInvalid, null);
        }

        var until = DateTimeOffset.UtcNow.Add(span);
        Directory.CreateDirectory(paths.Root);
        File.WriteAllText(file, until.ToString("O", CultureInfo.InvariantCulture));

        ctx.Output.Line($"Rotations paused until {until:u}. Run 'winlogrotate host pause' with no "
                      + "duration to resume early.");

        return ctx.Output.Complete("host pause", ExitCode.Ok, new PauseResult
        {
            PausedUntil = until.ToString("O", CultureInfo.InvariantCulture),
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
