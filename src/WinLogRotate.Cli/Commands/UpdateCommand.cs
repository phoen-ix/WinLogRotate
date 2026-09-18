using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
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
/// The version comes from GitHub's <c>releases/latest</c> redirect rather than its API, which
/// avoids the 60-per-hour unauthenticated rate limit that an entire office behind one NAT would
/// otherwise share. The download comes from the same release's fixed asset names, for the same
/// reason.
/// </para>
/// <para>
/// This is the one file allowed to name the publisher: every URL the product reaches for a
/// release is composed here and nowhere else.
/// </para>
/// </remarks>
internal static class UpdateCommand
{
    private const string LatestReleaseUrl = "https://github.com/phoen-ix/WinLogRotate/releases/latest";
    private const string DownloadRootUrl = "https://github.com/phoen-ix/WinLogRotate/releases/download/";

    /// <summary>Headers to last byte, for the whole installer. A slow line finishes forty
    /// megabytes inside this; a stalled one does not deserve to hold the process for longer.</summary>
    private static readonly TimeSpan DownloadBudget = TimeSpan.FromMinutes(15);

    /// <summary>What was downloaded earlier and never cleaned up is removed after this.</summary>
    private static readonly TimeSpan DownloadGrace = TimeSpan.FromDays(1);

    private const string DownloadDirectoryPrefix = "WinLogRotate-update-";

    /// <summary>
    /// The elevation question, as a seam.
    /// </summary>
    /// <remarks>
    /// The shape <c>JobCommand</c> and <c>PauseCommand</c> use: the real answer by default,
    /// replaced by a test that has no administrator token to offer.
    /// </remarks>
    internal static readonly Func<bool> Elevated = Privilege.IsElevated;

    public static async Task<int> CheckAsync(CommandContext ctx) =>
        await CheckAsync(ctx, new GitHubReleases(), InstallRecord.Read).ConfigureAwait(false);

    internal static async Task<int> CheckAsync(CommandContext ctx, IReleaseSource releases, Func<InstallRecord?> install)
    {
        const string verb = "update check";

        if (IsDisabledByPolicy())
        {
            ctx.Output.Line("Update checks are disabled by machine policy.");
            return ctx.Output.Complete(verb, ExitCode.Ok, DisabledByPolicy());
        }

        var latest = await releases.FindLatestAsync(CancellationToken.None).ConfigureAwait(false);

        // The record is reported either way: the console reads it to decide whether pressing
        // Update will need elevation, and that does not depend on there being an update.
        var record = install();

        if (latest is null)
        {
            CouldNotReachTheFeed(ctx);

            ctx.Output.Line($"current  {ProductInfo.Version}");
            ctx.Output.Line("latest   unknown (could not check)");

            return ctx.Output.Complete(verb, ExitCode.Ok, new UpdateResult
            {
                Current = ProductInfo.Version,
                Latest = null,
                UpdateAvailable = false,
                Detail = "could not reach the feed",
                Scope = record?.Scope.ToString(),
                Variant = record?.BuildVariant,
            });
        }

        var newer = ProductInfo.IsNewer(Version.Parse(ProductInfo.Version), latest);

        ctx.Output.Line($"current  {ProductInfo.Version}");
        ctx.Output.Line($"latest   {latest.ToString(3)}");
        ctx.Output.Line(newer ? "An update is available." : "Up to date.");

        return ctx.Output.Complete(verb, ExitCode.Ok, new UpdateResult
        {
            Current = ProductInfo.Version,
            Latest = latest.ToString(3),
            UpdateAvailable = newer,
            Detail = newer ? LatestReleaseUrl : "up to date",
            Scope = record?.Scope.ToString(),
            Variant = record?.BuildVariant,
        });
    }

    public static int Apply(CommandContext ctx, bool restartGui) =>
        ApplyAsync(ctx, restartGui, new GitHubReleases(), InstallRecord.Read, new ProcessLauncher(), elevated: null)
            .GetAwaiter().GetResult();

    /// <summary>
    /// Downloads the newest release, verifies it, and hands over to its installer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every step that can decline does so before the one after it costs anything: policy,
    /// then the feed, then whether this copy has an installer to re-run, then whether this
    /// token may run it, then the sums file, and only then forty megabytes. The download is
    /// hashed as it streams, never re-read from disk, and a digest that does not match the
    /// release's is deleted before anything else is said about it.
    /// </para>
    /// <para>
    /// The hand-over is the end. The installer waits for any rotation in progress, closes the
    /// console over its quit event, and replaces the executables - including this one - so this
    /// process must not still be running when it gets there. It starts the installer detached
    /// and returns; whether the install then succeeded is the installer's log's to say.
    /// </para>
    /// </remarks>
    internal static async Task<int> ApplyAsync(
        CommandContext ctx,
        bool restartGui,
        IReleaseSource releases,
        Func<InstallRecord?> install,
        IInstallerLauncher launcher,
        Func<bool>? elevated)
    {
        const string verb = "update apply";

        if (IsDisabledByPolicy())
        {
            ctx.Output.Line("Update checks are disabled by machine policy.");
            return ctx.Output.Complete(verb, ExitCode.Ok, DisabledByPolicy());
        }

        var latest = await releases.FindLatestAsync(CancellationToken.None).ConfigureAwait(false);

        if (latest is null)
        {
            CouldNotReachTheFeed(ctx);

            return ctx.Output.Complete(verb, ExitCode.Ok, new UpdateResult
            {
                Current = ProductInfo.Version,
                Latest = null,
                UpdateAvailable = false,
                Detail = "could not reach the feed",
            });
        }

        var record = install();

        ctx.Output.Line($"current  {ProductInfo.Version}");
        ctx.Output.Line($"latest   {latest.ToString(3)}");

        if (!ProductInfo.IsNewer(Version.Parse(ProductInfo.Version), latest))
        {
            ctx.Output.Line($"{ProductInfo.Name} {ProductInfo.Version} is the newest release.");

            return ctx.Output.Complete(verb, ExitCode.Ok, new UpdateResult
            {
                Current = ProductInfo.Version,
                Latest = latest.ToString(3),
                UpdateAvailable = false,
                Detail = "up to date",
                Scope = record?.Scope.ToString(),
                Variant = record?.BuildVariant,
            });
        }

        if (record is null)
        {
            // Not an error in the machine and not a defect: a copy that was unzipped has no
            // installer to re-run, and replacing a running executable by hand is not something
            // this verb should attempt on somebody's behalf.
            return Refusals.WillNotAct<UpdateResult>(
                ctx,
                verb,
                "this is a portable copy, which has no installer to re-run.",
                $"Download the newest release from {LatestReleaseUrl} and unzip it over this one, "
                + "or run its installer to have updates handled from now on.");
        }

        ctx.Output.Line($"install  {Describe(record)}");

        if (record.Scope == InstallScope.PerMachine && !(elevated ?? Elevated)())
        {
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Error,
                Code = DiagnosticCode.NeedsAdministrator,
                Message = "Updating an all-users installation needs administrator rights.",
                Remedy = "Run this from an elevated prompt, or press Update in the console, which asks for elevation.",
            });

            return ctx.Output.Complete<UpdateResult>(verb, ExitCode.Errors, null);
        }

        // The sums first, and the sums before anything is fetched that costs bandwidth. A
        // release whose files are still being uploaded - they appear one at a time - refuses
        // here, in a second, rather than after a download it would then have to discard.
        var sumsText = await releases.FetchTextAsync(latest, ReleaseAssets.SumsFileName, CancellationToken.None).ConfigureAwait(false);

        if (sumsText is null)
        {
            return NotInstalled(ctx, verb, latest, record,
                $"The release's {ReleaseAssets.SumsFileName} could not be downloaded.",
                "Try again in a few minutes. If the release was published moments ago, its files may still be uploading.");
        }

        if (!Sha256Sums.TryParse(sumsText, out var sums, out var problem))
        {
            return NotInstalled(ctx, verb, latest, record,
                $"The release's {ReleaseAssets.SumsFileName} could not be read: {problem}.",
                "Nothing was downloaded. Report this; the release's checksum file is malformed.");
        }

        var asset = ReleaseAssets.InstallerName(record.BuildVariant, latest);

        if (sums.DigestOf(asset) is null)
        {
            return NotInstalled(ctx, verb, latest, record,
                $"The release does not list {asset} in its checksums.",
                "Nothing was downloaded. Try again later; if it persists, the release is missing that installer.");
        }

        SweepOldDownloads();

        var directory = Path.Combine(Path.GetTempPath(), DownloadDirectoryPrefix + Guid.NewGuid().ToString("N")[..12]);
        var installer = Path.Combine(directory, asset);

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return NotInstalled(ctx, verb, latest, record,
                $"A folder for the download could not be created under {Path.GetTempPath()}: {e.Message}",
                "Free some space or fix the temporary folder's permissions, then try again.");
        }

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
                Reason = total is > 0 ? $"{Megabytes(done)} of {Megabytes(total.Value)}" : $"{Megabytes(done)} so far",
            });
        }

        var download = await releases.DownloadAsync(latest, asset, installer, Progress, CancellationToken.None).ConfigureAwait(false);

        if (download.Digest is null)
        {
            Discard(directory);

            ctx.Output.Event(Outcome(run, asset, download.Bytes, OpResult.Failed, download.Problem));

            return NotInstalled(ctx, verb, latest, record,
                $"{asset} could not be downloaded: {download.Problem}",
                "Nothing was changed. Try again; a download that keeps failing is a network or proxy question, not a rotation one.");
        }

        if (!sums.Matches(asset, download.Digest))
        {
            Discard(directory);

            ctx.Output.Event(Outcome(run, asset, download.Bytes, OpResult.Failed, "checksum mismatch"));

            return NotInstalled(ctx, verb, latest, record,
                "The download did not match the release's checksum and was discarded.",
                "Nothing was changed. Try again; if it happens twice, something between this machine and the release is altering downloads.");
        }

        ctx.Output.Event(Outcome(run, asset, download.Bytes, OpResult.Ok, $"verified against {ReleaseAssets.SumsFileName}"));
        ctx.Output.Line($"verified {asset} ({Megabytes(download.Bytes)}) against {ReleaseAssets.SumsFileName}");

        var arguments = InstallerArguments.For(record.Scope, HostKindFor(record), restartGui);

        if (launcher.Launch(installer, arguments) is { } failure)
        {
            Discard(directory);

            return NotInstalled(ctx, verb, latest, record,
                $"The installer could not be started: {failure}",
                "Nothing was changed. If a policy blocks programs from the temporary folder, download the installer from the release page and run it yourself.");
        }

        ctx.Output.Line("Handed over to the installer. It waits for any rotation in progress, closes the console if it is open, "
            + "and replaces the files." + (restartGui ? " The console is started again afterwards." : ""));

        return ctx.Output.Complete(verb, ExitCode.Ok, new UpdateResult
        {
            Current = ProductInfo.Version,
            Latest = latest.ToString(3),
            UpdateAvailable = true,
            Detail = "installing",
            Scope = record.Scope.ToString(),
            Variant = record.BuildVariant,
            Installing = true,
            Installer = installer,
        });
    }

    /// <summary>The run host to pass back. What was recorded, unless it is nothing the
    /// installer would accept - it validates the switch and exits without installing.</summary>
    internal static string HostKindFor(InstallRecord record) =>
        record.HostKind is "task" or "none" ? record.HostKind : "none";

    private static string Describe(InstallRecord record) =>
        $"{(record.Scope == InstallScope.PerMachine ? "all users" : "this user only")}, "
        + $"{(HostKindFor(record) == "task" ? "scheduled task" : "no run host")}, "
        + $"{record.BuildVariant} build";

    private static int NotInstalled(
        CommandContext ctx, string verb, Version latest, InstallRecord record, string message, string remedy)
    {
        ctx.Output.Diagnostic(new CliDiagnostic
        {
            Severity = Severity.Error,
            Code = DiagnosticCode.UpdateNotInstalled,
            Message = message,
            Remedy = remedy,
        });

        return ctx.Output.Complete(verb, ExitCode.Errors, new UpdateResult
        {
            Current = ProductInfo.Version,
            Latest = latest.ToString(3),
            UpdateAvailable = true,
            Detail = "not installed",
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

    private static UpdateResult DisabledByPolicy() => new()
    {
        Current = ProductInfo.Version,
        Latest = null,
        UpdateAvailable = false,
        Detail = "disabled by policy",
    };

    internal static string Megabytes(long bytes) =>
        string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0):0.0} MB");

    private static void Discard(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A download that could not be removed is swept on the next attempt, once it is a
            // day old. It is never run: nothing but this verb knows the folder, and this verb
            // has already declined it.
        }
    }

    /// <summary>
    /// Removes what earlier updates left in the temporary folder.
    /// </summary>
    /// <remarks>
    /// The installer cannot delete itself, so every successful update leaves one behind, and
    /// a failed one may leave a partial download. Anything nobody has written to for a day is
    /// nobody's any more; anything newer may be an installer that is still running.
    /// </remarks>
    private static void SweepOldDownloads()
    {
        try
        {
            var cutoff = DateTime.UtcNow - DownloadGrace;

            foreach (var directory in Directory.EnumerateDirectories(Path.GetTempPath(), DownloadDirectoryPrefix + "*"))
            {
                if (Directory.GetLastWriteTimeUtc(directory) < cutoff)
                {
                    Discard(directory);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A temporary folder that cannot be listed is not a reason to refuse an update.
        }
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

    /// <summary>
    /// A host a release download may be redirected to.
    /// </summary>
    /// <remarks>
    /// GitHub answers a release asset with a redirect to its object store, and that is the only
    /// redirect this product follows. A feed answering with somewhere else is not the feed.
    /// </remarks>
    internal static bool IsTrustedDownloadHost(string host) =>
        host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>The release feed, as GitHub serves it.</summary>
    private sealed class GitHubReleases : IReleaseSource
    {
        private const int MaxRedirects = 3;

        public async Task<Version?> FindLatestAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Do not follow the redirect: its Location header carries the tag, and stopping
                // there avoids downloading a release page we have no use for.
                using var client = NewClient(TimeSpan.FromSeconds(15));
                using var response = await client.GetAsync(LatestReleaseUrl, cancellationToken).ConfigureAwait(false);

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

        public async Task<string?> FetchTextAsync(Version release, string assetName, CancellationToken cancellationToken)
        {
            try
            {
                using var client = NewClient(TimeSpan.FromSeconds(30));
                using var response = await GetFollowingAsync(client, DownloadRootUrl + ReleaseAssets.DownloadPath(release, assetName), cancellationToken).ConfigureAwait(false);

                if (response is null || !response.IsSuccessStatusCode)
                {
                    return null;
                }

                return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
            {
                return null;
            }
        }

        public async Task<Download> DownloadAsync(
            Version release, string assetName, string toPath, Action<long, long?> progress, CancellationToken cancellationToken)
        {
            var done = 0L;

            try
            {
                using var client = NewClient(DownloadBudget);
                using var response = await GetFollowingAsync(client, DownloadRootUrl + ReleaseAssets.DownloadPath(release, assetName), cancellationToken).ConfigureAwait(false);

                if (response is null)
                {
                    return new Download(null, 0, "the feed redirected somewhere that is not the release's host");
                }

                if (!response.IsSuccessStatusCode)
                {
                    return new Download(null, 0, $"the server answered {(int)response.StatusCode} {response.ReasonPhrase}");
                }

                var total = response.Content.Headers.ContentLength;

                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var file = new FileStream(toPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);

                var buffer = new byte[1 << 16];
                int read;

                progress(0, total);

                while ((read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    done += read;
                    progress(done, total);
                }

                if (total is > 0 && done != total)
                {
                    return new Download(null, done, $"the connection closed after {Megabytes(done)} of {Megabytes(total.Value)}");
                }

                return new Download(hash.GetHashAndReset(), done, null);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
            {
                return new Download(null, done, e.Message);
            }
        }

        /// <summary>Follows GitHub's redirect to its object store, and nothing further afield.</summary>
        private static async Task<HttpResponseMessage?> GetFollowingAsync(HttpClient client, string url, CancellationToken cancellationToken)
        {
            var current = new Uri(url);

            for (var hop = 0; hop <= MaxRedirects; hop++)
            {
                var response = await client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

                if (response.StatusCode is not (HttpStatusCode.Found or HttpStatusCode.MovedPermanently
                    or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
                {
                    return response;
                }

                var location = response.Headers.Location;
                response.Dispose();

                if (location is null)
                {
                    return null;
                }

                current = location.IsAbsoluteUri ? location : new Uri(current, location);

                if (current.Scheme != Uri.UriSchemeHttps || !IsTrustedDownloadHost(current.Host))
                {
                    return null;
                }
            }

            return null;
        }

        private static HttpClient NewClient(TimeSpan timeout)
        {
            var handler = new HttpClientHandler { AllowAutoRedirect = false };
            var client = new HttpClient(handler) { Timeout = timeout };
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"WinLogRotate/{ProductInfo.Version}");
            return client;
        }
    }

    /// <summary>Starts the installer and does not wait for it.</summary>
    private sealed class ProcessLauncher : IInstallerLauncher
    {
        public string? Launch(string installer, IReadOnlyList<string> arguments)
        {
            // Through the shell and without pipes: nothing is read back from it, and this
            // process is one of the files it is about to replace. No "runas" - whoever runs
            // this verb has already answered the elevation question, or was refused above.
            var info = new ProcessStartInfo(installer)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(installer),
            };

            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            try
            {
                using var process = Process.Start(info);
                return process is null ? "the system did not start it" : null;
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                return e.Message;
            }
        }
    }
}

/// <summary>Where releases come from. The real one is GitHub; a test's is a dictionary.</summary>
internal interface IReleaseSource
{
    /// <summary>The newest release's version, or null when the feed could not be reached.</summary>
    Task<Version?> FindLatestAsync(CancellationToken cancellationToken);

    /// <summary>A small text asset of a release, or null when it could not be fetched.</summary>
    Task<string?> FetchTextAsync(Version release, string assetName, CancellationToken cancellationToken);

    /// <summary>Streams an asset to a file, hashing as it goes and reporting progress as
    /// (bytes so far, total if known).</summary>
    Task<Download> DownloadAsync(Version release, string assetName, string toPath, Action<long, long?> progress, CancellationToken cancellationToken);
}

/// <summary>What a download came to: its SHA-256 when it completed, else why it did not.</summary>
internal sealed record Download(byte[]? Digest, long Bytes, string? Problem);

/// <summary>Runs the downloaded installer. Returns why it could not, or null when it started.</summary>
internal interface IInstallerLauncher
{
    string? Launch(string installer, IReadOnlyList<string> arguments);
}
