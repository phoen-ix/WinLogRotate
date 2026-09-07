using System.Net;
using WinLogRotate.Cli.Output;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Hosting;

namespace WinLogRotate.Cli.Commands;

/// <summary>
/// Reports whether a newer release exists.
/// </summary>
/// <remarks>
/// <para>
/// A query, not an agent. The scheduled task never checks for updates - a log rotator that
/// reaches the internet on its own schedule is not what anyone asked for - so this exists to be
/// called deliberately, by a person or by configuration management:
/// <c>winlogrotate update check --json | jq -r .result.latest</c>.
/// </para>
/// <para>
/// The version comes from GitHub's <c>releases/latest</c> redirect rather than its API, which
/// avoids the 60-per-hour unauthenticated rate limit that an entire office behind one NAT would
/// otherwise share.
/// </para>
/// </remarks>
internal static class UpdateCommand
{
    private const string LatestReleaseUrl = "https://github.com/phoen-ix/winlogrotate/releases/latest";

    public static async Task<int> CheckAsync(CommandContext ctx)
    {
        if (IsDisabledByPolicy())
        {
            ctx.Output.Line("Update checks are disabled by machine policy.");
            return ctx.Output.Complete("update check", ExitCode.Ok, new UpdateResult
            {
                Current = ProductInfo.Version,
                Latest = null,
                UpdateAvailable = false,
                Detail = "disabled by policy",
            });
        }

        var latest = await FindLatestAsync().ConfigureAwait(false);

        if (latest is null)
        {
            // A failed check must read as "could not check", never as "no update" and never as
            // an error that a monitoring system would page someone about.
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Warning,
                Code = DiagnosticCode.ConfigUnreadable,
                Message = "Could not reach the release feed.",
                Remedy = "This is not a failure of the rotation itself; nothing was changed.",
            });

            ctx.Output.Line($"current  {ProductInfo.Version}");
            ctx.Output.Line("latest   unknown (could not check)");

            return ctx.Output.Complete("update check", ExitCode.Ok, new UpdateResult
            {
                Current = ProductInfo.Version,
                Latest = null,
                UpdateAvailable = false,
                Detail = "could not reach the feed",
            });
        }

        var current = Version.Parse(ProductInfo.Version);
        var newer = ProductInfo.IsNewer(current, latest);

        ctx.Output.Line($"current  {ProductInfo.Version}");
        ctx.Output.Line($"latest   {latest.ToString(3)}");
        ctx.Output.Line(newer ? "An update is available." : "Up to date.");

        return ctx.Output.Complete("update check", ExitCode.Ok, new UpdateResult
        {
            Current = ProductInfo.Version,
            Latest = latest.ToString(3),
            UpdateAvailable = newer,
            Detail = newer ? LatestReleaseUrl : "up to date",
        });
    }

    public static int Apply(CommandContext ctx)
    {
        // Deliberately not implemented as a self-replace. Updating a per-machine install means
        // writing to Program Files, which needs a UAC prompt that nobody is present to answer
        // at three in the morning - so the honest ceiling on a server is "tell me", and saying
        // so beats offering a switch that quietly declines.
        ctx.Output.Line("Automatic installation is not available.");
        ctx.Output.Line($"Download the installer from {LatestReleaseUrl} and run it; it upgrades in place,");
        ctx.Output.Line("keeps your configuration, and waits for any rotation in progress to finish.");

        return ctx.Output.Complete("update apply", ExitCode.Ok, new UpdateResult
        {
            Current = ProductInfo.Version,
            Latest = null,
            UpdateAvailable = false,
            Detail = LatestReleaseUrl,
        });
    }

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
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static async Task<Version?> FindLatestAsync()
    {
        try
        {
            // Do not follow the redirect: its Location header carries the tag, and stopping
            // there avoids downloading a release page we have no use for.
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"WinLogRotate/{ProductInfo.Version}");

            using var response = await client.GetAsync(LatestReleaseUrl).ConfigureAwait(false);

            if (response.StatusCode is not (HttpStatusCode.Found or HttpStatusCode.MovedPermanently))
            {
                return null;
            }

            var location = response.Headers.Location?.ToString();
            var tag = location?.Split('/').LastOrDefault()?.TrimStart('v');

            return Version.TryParse(tag, out var version) ? version : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }
}
