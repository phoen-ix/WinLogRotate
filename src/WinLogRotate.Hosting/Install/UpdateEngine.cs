using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using WinLogRotate.Core;

namespace WinLogRotate.Hosting.Install;

public enum CheckOutcome
{
    UpToDate,
    Available,

    /// <summary>A local build (<see cref="ProductInfo.DevVersion"/>): the latest release is
    /// reported, never offered.</summary>
    DevBuild,

    /// <summary>GitHub did not answer with a release - offline, a proxy, or no release yet.</summary>
    Unreachable,

    DisabledByPolicy,
}

public sealed record UpdateCheck(CheckOutcome Outcome, string Current, Version? Latest);

public enum ApplyOutcome
{
    /// <summary>The installer is running. It closes the console, replaces the files, and
    /// starts the console again if asked to.</summary>
    Installing,

    /// <summary>Not an installed copy: there is no installer to re-run.</summary>
    Portable,

    /// <summary>The administrator prompt an all-users install needs was declined.</summary>
    Declined,

    Failed,
    DisabledByPolicy,
}

/// <summary>
/// How an update ended, in words for a person: what happened, and what to do about it.
/// </summary>
public sealed record UpdateApply(ApplyOutcome Outcome, string Message, string? Remedy = null)
{
    /// <summary>The installer's download, when one was made: its digest when it matched the
    /// release's sums, else why it was not used.</summary>
    public Download? Download { get; init; }

    /// <summary>The verified installer that was handed over.</summary>
    public string? Installer { get; init; }
}

/// <summary>Where releases come from. The real one is GitHub; a test's is a dictionary.</summary>
public interface IReleaseSource
{
    /// <summary>The newest release's version, or null when the feed could not be reached.</summary>
    Task<Version?> FindLatestAsync(CancellationToken cancellationToken);

    /// <summary>A small text asset of a release, or null when it could not be fetched.</summary>
    Task<string?> FetchTextAsync(Version release, string assetName, CancellationToken cancellationToken);

    /// <summary>Streams an asset to a file, hashing as it goes and reporting progress as
    /// (bytes so far, total if known).</summary>
    Task<Download> DownloadAsync(
        Version release, string assetName, string toPath, Action<long, long?> progress, CancellationToken cancellationToken);
}

/// <summary>What a download came to: its SHA-256 when it completed, else why it did not.</summary>
public sealed record Download(byte[]? Digest, long Bytes, string? Problem);

/// <summary>Runs the downloaded installer, through the administrator prompt when asked to.</summary>
public interface IInstallerLauncher
{
    LaunchResult Launch(string installer, IReadOnlyList<string> arguments, bool elevate);
}

public sealed record LaunchResult(bool Started, bool Declined, string? Problem)
{
    public static LaunchResult Ok { get; } = new(true, false, null);
}

/// <summary>
/// Reports whether a newer release exists, and installs one when asked.
/// </summary>
/// <remarks>
/// <para>
/// Outcomes, not diagnostics: <c>UpdateCommand</c> is the one place that turns them into codes
/// and exit codes, so the LR codes keep meaning what they say. The release feed's address is
/// handed in by that command too, which is the one file allowed to name the publisher.
/// </para>
/// <para>
/// Every step that can decline does so before the one after it costs anything: policy, then
/// whether this copy has an installer to re-run, then whether a <c>-min</c> console would have
/// the Desktop Runtime it needs, then the sums file, and only then forty megabytes. The download is hashed as it streams, never re-read from disk, and a digest that
/// does not match the release's is deleted before anything else is said about it.
/// </para>
/// <para>
/// The hand-over is the end. The installer waits for any rotation in progress, closes the
/// console over its quit event, and replaces the executables - including the caller's - so the
/// caller must not still be running when it gets there. It starts the installer detached and
/// returns; whether the install then succeeded is the installer's log's to say.
/// </para>
/// </remarks>
public sealed class UpdateEngine(
    IReleaseSource releases,
    IInstallerLauncher launcher,
    Func<bool> disabledByPolicy,
    Func<bool> elevated,
    Func<bool> desktopRuntimeInstalled,
    string tempRoot)
{
    /// <summary>Downloads go to <c>%TEMP%\{prefix}{12 hex}\</c>. The uninstaller sweeps them too.</summary>
    public const string DownloadDirectoryPrefix = "WinLogRotate-update-";

    /// <summary>What was downloaded earlier and never cleaned up is removed after this.</summary>
    public static readonly TimeSpan DownloadGrace = TimeSpan.FromDays(1);

    public async Task<UpdateCheck> CheckAsync(string current, CancellationToken cancellationToken = default)
    {
        if (disabledByPolicy())
        {
            return new UpdateCheck(CheckOutcome.DisabledByPolicy, current, null);
        }

        var latest = await releases.FindLatestAsync(cancellationToken).ConfigureAwait(false);

        if (latest is null)
        {
            return new UpdateCheck(CheckOutcome.Unreachable, current, null);
        }

        if (current == ProductInfo.DevVersion)
        {
            return new UpdateCheck(CheckOutcome.DevBuild, current, latest);
        }

        return new UpdateCheck(
            ProductInfo.IsNewer(current, latest) ? CheckOutcome.Available : CheckOutcome.UpToDate,
            current,
            latest);
    }

    /// <summary>
    /// Downloads <paramref name="latest"/>'s installer for this record, verifies it, and hands over.
    /// </summary>
    public async Task<UpdateApply> ApplyAsync(
        Version latest,
        InstallRecord record,
        bool restartGui,
        Action<long, long?>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (disabledByPolicy())
        {
            return new UpdateApply(ApplyOutcome.DisabledByPolicy, "Update checks are disabled by machine policy.");
        }

        if (record.Scope == InstallScope.Portable)
        {
            // Not an error in the machine and not a defect: a copy that was unzipped has no
            // installer to re-run, and replacing a running executable by hand is not something
            // to attempt on somebody's behalf.
            return new UpdateApply(ApplyOutcome.Portable, "This is a portable copy, which has no installer to re-run.");
        }

        // The -min installer is run with /NORUNTIME, so it never fetches the runtime itself:
        // without it the engine would update and keep rotating, but the console would not open.
        // Say so here, with the way out, before anything is downloaded.
        if (record.BuildVariant == "min" && !desktopRuntimeInstalled())
        {
            return Failed(
                $"The -min build's console needs the .NET {DesktopRuntime.Major} Desktop Runtime (x64), and it isn't installed on this PC, so nothing was downloaded.",
                $"Install it (winget install {DesktopRuntime.WingetId}, or from {DesktopRuntime.DownloadPage}), then update again; "
                + "or run the new -min installer by hand, which offers to fetch it.");
        }

        // The sums first, and the sums before anything is fetched that costs bandwidth. A
        // release whose files are still being uploaded - they appear one at a time - refuses
        // here, in a second, rather than after a download it would then have to discard.
        var sumsText = await releases.FetchTextAsync(latest, ReleaseAssets.SumsFileName, cancellationToken).ConfigureAwait(false);

        if (sumsText is null)
        {
            return Failed(
                $"The release's {ReleaseAssets.SumsFileName} could not be downloaded.",
                "Try again in a few minutes. If the release was published moments ago, its files may still be uploading.");
        }

        if (!Sha256Sums.TryParse(sumsText, out var sums, out var problem))
        {
            return Failed(
                $"The release's {ReleaseAssets.SumsFileName} could not be read: {problem}.",
                "Nothing was downloaded. Report this; the release's checksum file is malformed.");
        }

        var asset = ReleaseAssets.InstallerName(record.BuildVariant, latest);

        if (sums.DigestOf(asset) is null)
        {
            return Failed(
                $"The release does not list {asset} in its checksums.",
                "Nothing was downloaded. Try again later; if it persists, the release is missing that installer.");
        }

        SweepOldDownloads(tempRoot, DateTime.UtcNow);

        var directory = Path.Combine(tempRoot, DownloadDirectoryPrefix + Guid.NewGuid().ToString("N")[..12]);
        var installer = Path.Combine(directory, asset);

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Failed(
                $"A folder for the download could not be created under {tempRoot}: {e.Message}",
                "Free some space or fix the temporary folder's permissions, then try again.");
        }

        var download = await releases.DownloadAsync(latest, asset, installer, progress ?? ((_, _) => { }), cancellationToken)
            .ConfigureAwait(false);

        if (download.Digest is null)
        {
            Discard(directory);

            return Failed(
                $"{asset} could not be downloaded: {download.Problem}",
                "Nothing was changed. Try again; a download that keeps failing is a network or proxy question, not a rotation one.",
                download);
        }

        if (!sums.Matches(asset, download.Digest))
        {
            Discard(directory);

            return Failed(
                "The download did not match the release's checksum and was discarded.",
                "Nothing was changed. Try again; if it happens twice, something between this machine and the release is altering downloads.",
                download with { Digest = null, Problem = "checksum mismatch" });
        }

        // The prompt only where it is needed: an all-users install this process cannot write
        // to. A per-user installer is run as whoever asked, and asks nothing.
        var elevate = record.Scope == InstallScope.PerMachine && !elevated();
        var launch = launcher.Launch(installer, InstallerArguments.For(record.Scope, HostKindFor(record), restartGui), elevate);

        if (launch.Declined)
        {
            Discard(directory);

            return new UpdateApply(
                ApplyOutcome.Declined,
                "The administrator prompt was declined, so nothing was changed.",
                $"{ProductInfo.Name} is installed for all users on this machine, so updating it needs administrator rights. "
                + "Try again and answer Yes, or run the update from an elevated prompt.")
            { Download = download };
        }

        if (!launch.Started)
        {
            Discard(directory);

            return Failed(
                $"The installer could not be started: {launch.Problem}",
                "Nothing was changed. If a policy blocks programs from the temporary folder, download the installer from the release page and run it yourself.",
                download);
        }

        return new UpdateApply(ApplyOutcome.Installing, $"Installing {ProductInfo.Name} {latest.ToString(3)}.")
        {
            Download = download,
            Installer = installer,
        };
    }

    /// <summary>The run host to pass back. What was recorded, unless it is nothing the
    /// installer would accept - it validates the switch and exits without installing.</summary>
    public static string HostKindFor(InstallRecord record) =>
        record.HostKind is "task" or "none" ? record.HostKind : "none";

    /// <summary>
    /// Removes what earlier updates left in the temporary folder.
    /// </summary>
    /// <remarks>
    /// The installer cannot delete itself, so every successful update leaves one behind, and
    /// a failed one may leave a partial download. Anything nobody has written to for a day is
    /// nobody's any more; anything newer may be an installer that is still running.
    /// </remarks>
    public static void SweepOldDownloads(string tempRoot, DateTime nowUtc)
    {
        try
        {
            var cutoff = nowUtc - DownloadGrace;

            foreach (var directory in Directory.EnumerateDirectories(tempRoot, DownloadDirectoryPrefix + "*"))
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

    public static string Megabytes(long bytes) =>
        string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0):0.0} MB");

    private static UpdateApply Failed(string message, string remedy, Download? download = null) =>
        new(ApplyOutcome.Failed, message, remedy) { Download = download };

    private static void Discard(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A download that could not be removed is swept on a later attempt, once it is a
            // day old. It is never run: nothing but this engine knows the folder, and it has
            // already declined it.
        }
    }

    /// <summary>
    /// Which release a <c>releases/latest</c> redirect names:
    /// <c>https://github.com/{owner}/{repo}/releases/tag/v1.2.3</c> is 1.2.3.
    /// </summary>
    /// <remarks>
    /// Anything shaped differently is not an answer: a repository with no release yet redirects
    /// to <c>/releases</c>, a sign-in wall to <c>/login</c>, and a tag of another repository is
    /// not this product's release. A two-part tag (<c>v0.8</c>) is refused too, because every
    /// asset name and download path is spelled with three.
    /// </remarks>
    internal static Version? VersionFromTagUrl(string? location, string latestReleaseUrl)
    {
        if (string.IsNullOrWhiteSpace(location)
            || !Uri.TryCreate(latestReleaseUrl, UriKind.Absolute, out var request)
            || !Uri.TryCreate(request, location, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var repository = request.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (repository.Length < 2
            || parts.Length != 5
            || !parts[0].Equals(repository[0], StringComparison.OrdinalIgnoreCase)
            || !parts[1].Equals(repository[1], StringComparison.OrdinalIgnoreCase)
            || !parts[2].Equals("releases", StringComparison.Ordinal)
            || !parts[3].Equals("tag", StringComparison.Ordinal))
        {
            return null;
        }

        var tag = Uri.UnescapeDataString(parts[4]).TrimStart('v', 'V');

        return Version.TryParse(tag, out var version) && version.Build >= 0 && version.Revision < 0 ? version : null;
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
}

/// <summary>
/// The release feed, as GitHub serves it.
/// </summary>
/// <remarks>
/// The version comes from the <c>releases/latest</c> redirect rather than the API, which avoids
/// the 60-per-hour unauthenticated rate limit that an entire office behind one NAT would
/// otherwise share. The download comes from the same release's fixed asset names, for the same
/// reason. Both addresses are the caller's, which is the one file allowed to name the publisher.
/// </remarks>
public sealed class GitHubReleases(string latestReleaseUrl, string downloadRootUrl) : IReleaseSource
{
    private const int MaxRedirects = 3;

    /// <summary>Headers to last byte, for the whole installer. A slow line finishes forty
    /// megabytes inside this; a stalled one does not deserve to hold the process for longer.</summary>
    private static readonly TimeSpan DownloadBudget = TimeSpan.FromMinutes(15);

    public async Task<Version?> FindLatestAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Do not follow the redirect: its Location header carries the tag, and stopping
            // there avoids downloading a release page we have no use for.
            using var client = NewClient(TimeSpan.FromSeconds(15));
            using var response = await client.GetAsync(latestReleaseUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            return (int)response.StatusCode is >= 300 and <= 399
                ? UpdateEngine.VersionFromTagUrl(response.Headers.Location?.OriginalString, latestReleaseUrl)
                : null;
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
            using var response = await GetFollowingAsync(
                client, downloadRootUrl + ReleaseAssets.DownloadPath(release, assetName), cancellationToken).ConfigureAwait(false);

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

        // HttpClient.Timeout stops counting once the headers are in under ResponseHeadersRead,
        // so the body gets its own budget.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(DownloadBudget);

        try
        {
            using var client = NewClient(Timeout.InfiniteTimeSpan);
            using var response = await GetFollowingAsync(
                client, downloadRootUrl + ReleaseAssets.DownloadPath(release, assetName), budget.Token).ConfigureAwait(false);

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
            await using var body = await response.Content.ReadAsStreamAsync(budget.Token).ConfigureAwait(false);
            await using var file = new FileStream(toPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);

            var buffer = new byte[1 << 16];
            int read;

            progress(0, total);

            while ((read = await body.ReadAsync(buffer, budget.Token).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), budget.Token).ConfigureAwait(false);
                done += read;
                progress(done, total);
            }

            if (total is > 0 && done != total)
            {
                return new Download(null, done,
                    $"the connection closed after {UpdateEngine.Megabytes(done)} of {UpdateEngine.Megabytes(total.Value)}");
            }

            return new Download(hash.GetHashAndReset(), done, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new Download(null, done, $"it took longer than {(int)DownloadBudget.TotalMinutes} minutes");
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or UnauthorizedAccessException)
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

            if (current.Scheme != Uri.UriSchemeHttps || !UpdateEngine.IsTrustedDownloadHost(current.Host))
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
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"{ProductInfo.Name}/{ProductInfo.Version}");
        return client;
    }
}

/// <summary>Starts the installer and does not wait for it.</summary>
public sealed class ShellLauncher : IInstallerLauncher
{
    private const int ErrorCancelled = 1223;

    public LaunchResult Launch(string installer, IReadOnlyList<string> arguments, bool elevate)
    {
        // Through the shell and without pipes: nothing is read back from it, and the caller is
        // one of the files it is about to replace. "runas" only for an all-users install this
        // process cannot write to. The installer itself asks for no more than the user's own
        // rights, so a per-user update raises no prompt at all - not even on an administrator's
        // account, where an installer asking for the highest rights once prompted for nothing.
        var info = new ProcessStartInfo(installer)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(installer),
            Verb = elevate ? "runas" : "",
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(info);
            return process is null ? new LaunchResult(false, false, "the system did not start it") : LaunchResult.Ok;
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == ErrorCancelled)
        {
            return new LaunchResult(false, true, "the administrator prompt was declined");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new LaunchResult(false, false, e.Message);
        }
    }
}
