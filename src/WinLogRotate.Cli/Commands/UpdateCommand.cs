using WinLogRotate.Cli.Output;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Core.Journaling;
using WinLogRotate.Hosting;
using WinLogRotate.Hosting.Install;

namespace WinLogRotate.Cli.Commands;

/// <summary>
/// Reports whether a newer release exists, and installs one when asked.
/// </summary>
/// <remarks>
/// <para>
/// A query and an action, neither of them an agent. The scheduled task never checks for
/// updates - a log rotator that reaches the internet on its own schedule is not what anyone
/// asked for - so both exist to be called deliberately, by a person, by configuration
/// management, or by the console on its user's behalf:
/// <c>winlogrotate update check --json | jq -r .result.latest</c>.
/// </para>
/// <para>
/// This is the one file allowed to name the publisher: every URL the product reaches for a
/// release is composed here and handed to <see cref="GitHubReleases"/>, and nowhere else.
/// </para>
/// </remarks>
internal static class UpdateCommand
{
    internal const string LatestReleaseUrl = "https://github.com/phoen-ix/WinLogRotate/releases/latest";
    private const string DownloadRootUrl = "https://github.com/phoen-ix/WinLogRotate/releases/download/";

    private static UpdateEngine Engine() => new(
        new GitHubReleases(LatestReleaseUrl, DownloadRootUrl),
        new ShellLauncher(),
        IsDisabledByPolicy,
        Privilege.IsElevated,
        Path.GetTempPath());

    /// <summary>This executable's own install, found by the folder it runs from.</summary>
    private static InstallRecord DetectInstall() => InstallRecord.Detect(AppContext.BaseDirectory);

    public static async Task<int> CheckAsync(CommandContext ctx) =>
        await CheckAsync(ctx, Engine(), DetectInstall, ProductInfo.Version).ConfigureAwait(false);

    internal static async Task<int> CheckAsync(CommandContext ctx, UpdateEngine engine, Func<InstallRecord> install, string current)
    {
        const string verb = "update check";

        var check = await engine.CheckAsync(current).ConfigureAwait(false);

        if (check.Outcome == CheckOutcome.DisabledByPolicy)
        {
            ctx.Output.Line("Update checks are disabled by machine policy.");
            return ctx.Output.Complete(verb, ExitCode.Ok, DisabledByPolicy(current));
        }

        // The record is reported either way: the console reads it to decide whether pressing
        // Update will need elevation, or whether this copy can only be replaced by hand, and
        // that does not depend on there being an update.
        var record = install();

        ctx.Output.Line($"current  {current}");

        if (check.Latest is not { } latest)
        {
            CouldNotReachTheFeed(ctx);

            ctx.Output.Line("latest   unknown (could not check)");

            return ctx.Output.Complete(verb, ExitCode.Ok, new UpdateResult
            {
                Current = current,
                Latest = null,
                UpdateAvailable = false,
                Detail = "could not reach the feed",
                Scope = record.Scope.ToString(),
                Variant = record.BuildVariant,
            });
        }

        var available = check.Outcome == CheckOutcome.Available;

        ctx.Output.Line($"latest   {latest.ToString(3)}");
        ctx.Output.Line(check.Outcome switch
        {
            CheckOutcome.Available => "An update is available.",
            CheckOutcome.DevBuild => "This is a development build; releases are not offered to it.",
            _ => "Up to date.",
        });

        return ctx.Output.Complete(verb, ExitCode.Ok, new UpdateResult
        {
            Current = current,
            Latest = latest.ToString(3),
            UpdateAvailable = available,

            // The release page when there is something on it to fetch: the console offers it to
            // a portable copy, which can only be updated by hand.
            Detail = check.Outcome switch
            {
                CheckOutcome.Available => LatestReleaseUrl,
                CheckOutcome.DevBuild => "development build",
                _ => "up to date",
            },
            Scope = record.Scope.ToString(),
            Variant = record.BuildVariant,
        });
    }

    public static int Apply(CommandContext ctx, bool restartGui) =>
        ApplyAsync(ctx, restartGui, Engine(), DetectInstall, ProductInfo.Version).GetAwaiter().GetResult();

    /// <summary>
    /// Downloads the newest release, verifies it, and hands over to its installer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The steps are <see cref="UpdateEngine"/>'s; this turns its outcomes into lines, events,
    /// codes and an exit code. No administrator token is needed to run it: an all-users
    /// install's installer is started through the administrator prompt, and only the
    /// installer - not this verb, not the console - runs elevated.
    /// </para>
    /// <para>
    /// The hand-over is the end. The installer replaces this executable, so this process
    /// returns as soon as it has started it.
    /// </para>
    /// </remarks>
    internal static async Task<int> ApplyAsync(
        CommandContext ctx, bool restartGui, UpdateEngine engine, Func<InstallRecord> install, string current)
    {
        const string verb = "update apply";

        var check = await engine.CheckAsync(current).ConfigureAwait(false);

        if (check.Outcome == CheckOutcome.DisabledByPolicy)
        {
            ctx.Output.Line("Update checks are disabled by machine policy.");
            return ctx.Output.Complete(verb, ExitCode.Ok, DisabledByPolicy(current));
        }

        if (check.Latest is not { } latest)
        {
            CouldNotReachTheFeed(ctx);

            return ctx.Output.Complete(verb, ExitCode.Ok, new UpdateResult
            {
                Current = current,
                Latest = null,
                UpdateAvailable = false,
                Detail = "could not reach the feed",
            });
        }

        var record = install();

        ctx.Output.Line($"current  {current}");
        ctx.Output.Line($"latest   {latest.ToString(3)}");

        if (check.Outcome != CheckOutcome.Available)
        {
            var devBuild = check.Outcome == CheckOutcome.DevBuild;

            ctx.Output.Line(devBuild
                ? "This is a development build; releases are not installed over it."
                : $"{ProductInfo.Name} {current} is the newest release.");

            return ctx.Output.Complete(verb, ExitCode.Ok, new UpdateResult
            {
                Current = current,
                Latest = latest.ToString(3),
                UpdateAvailable = false,
                Detail = devBuild ? "development build" : "up to date",
                Scope = record.Scope.ToString(),
                Variant = record.BuildVariant,
            });
        }

        var asset = ReleaseAssets.InstallerName(record.BuildVariant, latest);
        var run = RunId.New(TimeProvider.System);
        var lastBucket = -1L;

        void Progress(long done, long? total)
        {
            // One event per five per cent, or per four megabytes when the size is unknown. The
            // text sink prints each one; a console watching a download wants to see it move
            // without seeing every packet.
            var bucket = total is > 0 ? done * 20 / total.Value : done / (4L * 1024 * 1024);

            if (bucket == lastBucket)
            {
                return;
            }

            lastBucket = bucket;

            ctx.Output.Event(new CliEvent
            {
                Ts = string.Empty,
                Run = run,
                Operation = Op.Update,
                Phase = Phase.Apply,
                Src = asset,
                BytesAfter = done,
                Reason = total is > 0 ? $"{UpdateEngine.Megabytes(done)} of {UpdateEngine.Megabytes(total.Value)}" : $"{UpdateEngine.Megabytes(done)} so far",
            });
        }

        if (record.Scope != InstallScope.Portable)
        {
            ctx.Output.Line($"install  {Describe(record)}");
        }

        var applied = await engine.ApplyAsync(latest, record, restartGui, Progress).ConfigureAwait(false);

        if (applied.Download is { } download)
        {
            if (download.Digest is null)
            {
                ctx.Output.Event(Outcome(run, asset, download.Bytes, OpResult.Failed, download.Problem));
            }
            else
            {
                ctx.Output.Event(Outcome(run, asset, download.Bytes, OpResult.Ok, $"verified against {ReleaseAssets.SumsFileName}"));
                ctx.Output.Line($"verified {asset} ({UpdateEngine.Megabytes(download.Bytes)}) against {ReleaseAssets.SumsFileName}");
            }
        }

        switch (applied.Outcome)
        {
            case ApplyOutcome.Installing:
                ctx.Output.Line("Handed over to the installer. It waits for any rotation in progress, closes the console if it is open, "
                    + "and replaces the files." + (restartGui ? " The console is started again afterwards." : ""));

                return ctx.Output.Complete(verb, ExitCode.Ok, new UpdateResult
                {
                    Current = current,
                    Latest = latest.ToString(3),
                    UpdateAvailable = true,
                    Detail = "installing",
                    Scope = record.Scope.ToString(),
                    Variant = record.BuildVariant,
                    Installing = true,
                    Installer = applied.Installer,
                });

            case ApplyOutcome.Portable:
                // A verb that declines to do what it is named after does not report success.
                return Refusals.WillNotAct<UpdateResult>(
                    ctx,
                    verb,
                    "this is a portable copy, which has no installer to re-run.",
                    $"Download the newest release from {LatestReleaseUrl} and unzip it over this one, "
                    + "or run its installer to have updates handled from now on.");

            case ApplyOutcome.DisabledByPolicy:
                ctx.Output.Line(applied.Message);
                return ctx.Output.Complete(verb, ExitCode.Ok, DisabledByPolicy(current));

            default:
                // Declined exits as a failure does. This CLI has no code for "the user said no" -
                // a secret prompt left with Escape exits 1 too - and the machine is still on the
                // old version, which is exactly what 1 tells 'update apply && next'. The detail
                // is what tells the console it was a choice rather than a fault.
                return NotInstalled(ctx, verb, current, latest, record, applied,
                    applied.Outcome == ApplyOutcome.Declined ? "declined" : "not installed");
        }
    }

    private static string Describe(InstallRecord record) =>
        $"{(record.Scope == InstallScope.PerMachine ? "all users" : "this user only")}, "
        + $"{(UpdateEngine.HostKindFor(record) == "task" ? "scheduled task" : "no run host")}, "
        + $"{record.BuildVariant} build";

    private static int NotInstalled(
        CommandContext ctx, string verb, string current, Version latest, InstallRecord record, UpdateApply applied, string detail)
    {
        // A declined administrator prompt is the user's choice, not a fault: a warning, so a
        // "No" at the prompt does not land in the Event Log as an error. Exit 1 either way.
        ctx.Output.Diagnostic(new CliDiagnostic
        {
            Severity = detail == "declined" ? Severity.Warning : Severity.Error,
            Code = DiagnosticCode.UpdateNotInstalled,
            Message = applied.Message,
            Remedy = applied.Remedy,
        });

        return ctx.Output.Complete(verb, ExitCode.Errors, new UpdateResult
        {
            Current = current,
            Latest = latest.ToString(3),
            UpdateAvailable = true,
            Detail = detail,
            Scope = record.Scope.ToString(),
            Variant = record.BuildVariant,
        });
    }

    private static CliEvent Outcome(string run, string asset, long bytes, string result, string? reason) => new()
    {
        Ts = string.Empty,
        Run = run,
        Operation = Op.Update,
        Phase = Phase.Apply,
        Result = result,
        Src = asset,
        BytesAfter = bytes,
        Reason = reason,
    };

    private static void CouldNotReachTheFeed(CommandContext ctx) =>
        // A failed check must read as "could not check", never as "no update" and never as
        // an error that a monitoring system would page someone about.
        ctx.Output.Diagnostic(new CliDiagnostic
        {
            Severity = Severity.Warning,
            Code = DiagnosticCode.UpdateCheckFailed,
            Message = "Could not reach the release feed.",
            Remedy = "This is not a failure of the rotation itself; nothing was changed.",
        });

    private static UpdateResult DisabledByPolicy(string current) => new()
    {
        Current = current,
        Latest = null,
        UpdateAvailable = false,
        Detail = "disabled by policy",
    };

    /// <summary>
    /// Enterprises ask for this on day one, and shipping it later means shipping it twice.
    /// </summary>
    private static bool IsDisabledByPolicy()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(Names.PolicyKey);
            return key?.GetValue("DisableUpdateCheck") is int and 1;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }
}
