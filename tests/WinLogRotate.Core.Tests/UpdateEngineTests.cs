using System.Security.Cryptography;
using System.Text;
using Shouldly;
using WinLogRotate.Cli.Commands;
using WinLogRotate.Hosting.Install;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// The update steps themselves, with a release feed made of a dictionary and a launcher that
/// only records what it was asked to start.
/// </summary>
public sealed class UpdateEngineTests : IDisposable
{
    private static readonly Version Latest = new(0, 2, 0);
    private static readonly byte[] Installer = Encoding.UTF8.GetBytes("pretend this is an NSIS installer");

    private static readonly InstallRecord PerUser = new()
    {
        Scope = InstallScope.PerUser,
        Location = "/somewhere",
        HostKind = "none",
        BuildVariant = "full",
    };

    private static readonly InstallRecord PerMachine = PerUser with { Scope = InstallScope.PerMachine, HostKind = "task", BuildVariant = "min" };

    private readonly string _temp = Directory.CreateTempSubdirectory("lr-engine-").FullName;
    private readonly FakeReleases _releases = new();
    private readonly FakeLauncher _launcher = new();
    private bool _policy;
    private bool _elevated;

    public UpdateEngineTests()
    {
        _releases.Latest = Latest;
        _releases.Files["WinLogRotate-Setup-0.2.0-full.exe"] = Installer;
        _releases.Files["WinLogRotate-Setup-0.2.0-min.exe"] = Installer;
        _releases.Files[ReleaseAssets.SumsFileName] = Encoding.UTF8.GetBytes(
            $"{Hex(Installer)}  WinLogRotate-Setup-0.2.0-full.exe\r\n{Hex(Installer)}  WinLogRotate-Setup-0.2.0-min.exe\r\n");
    }

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private UpdateEngine Engine() => new(_releases, _launcher, () => _policy, () => _elevated, _temp);

    private static string Hex(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private string[] UpdateFolders() => Directory.GetDirectories(_temp, UpdateEngine.DownloadDirectoryPrefix + "*");

    [Theory]
    [InlineData("0.1.0", CheckOutcome.Available)]
    [InlineData("0.2.0", CheckOutcome.UpToDate)]
    [InlineData("0.3.0", CheckOutcome.UpToDate)]
    [InlineData(ProductInfo.DevVersion, CheckOutcome.DevBuild)]
    public async Task ACheckComparesAgainstTheLatestRelease(string current, CheckOutcome expected)
    {
        var result = await Engine().CheckAsync(current, Ct);

        result.Outcome.ShouldBe(expected);
        result.Latest.ShouldBe(Latest);
    }

    [Fact]
    public async Task NoAnswerFromGitHubIsUnreachable()
    {
        _releases.Latest = null;

        (await Engine().CheckAsync("0.1.0", Ct)).Outcome.ShouldBe(CheckOutcome.Unreachable);
    }

    [Fact]
    public async Task ThePolicyStopsEverythingBeforeAnythingGoesOnline()
    {
        _policy = true;

        (await Engine().CheckAsync("0.1.0", Ct)).Outcome.ShouldBe(CheckOutcome.DisabledByPolicy);
        (await Engine().ApplyAsync(Latest, PerUser, restartGui: true, cancellationToken: Ct)).Outcome.ShouldBe(ApplyOutcome.DisabledByPolicy);
        _releases.Requests.ShouldBe(0);
    }

    [Fact]
    public async Task APerUserInstallRunsTheVerifiedInstallerSilentlyWithoutElevation()
    {
        var result = await Engine().ApplyAsync(Latest, PerUser, restartGui: true, cancellationToken: Ct);

        result.Outcome.ShouldBe(ApplyOutcome.Installing);
        var launch = _launcher.Launches.ShouldHaveSingleItem();
        Path.GetFileName(launch.Installer).ShouldBe("WinLogRotate-Setup-0.2.0-full.exe");
        launch.Contents.ShouldBe(Installer);
        launch.Arguments.ShouldBe(["/S", "/CurrentUser", "/HOST=none", "/NORUNTIME", "/RESTART"]);
        launch.Elevate.ShouldBeFalse();
        result.Installer.ShouldBe(launch.Installer);
    }

    [Fact]
    public async Task APerMachineInstallAsksForElevationUnlessItHasIt()
    {
        await Engine().ApplyAsync(Latest, PerMachine, restartGui: false, cancellationToken: Ct);
        _elevated = true;
        await Engine().ApplyAsync(Latest, PerMachine, restartGui: false, cancellationToken: Ct);

        _launcher.Launches.Count.ShouldBe(2);
        Path.GetFileName(_launcher.Launches[0].Installer).ShouldBe("WinLogRotate-Setup-0.2.0-min.exe");
        _launcher.Launches[0].Arguments.ShouldBe(["/S", "/AllUsers", "/HOST=task", "/NORUNTIME"]);
        _launcher.Launches[0].Elevate.ShouldBeTrue();
        _launcher.Launches[1].Elevate.ShouldBeFalse();
    }

    [Fact]
    public async Task APortableCopyIsNeverUpdatedThroughAnInstaller()
    {
        var result = await Engine().ApplyAsync(Latest, InstallRecord.Portable("full"), restartGui: true, cancellationToken: Ct);

        result.Outcome.ShouldBe(ApplyOutcome.Portable);
        _launcher.Launches.ShouldBeEmpty();
        _releases.Requests.ShouldBe(0);
    }

    [Fact]
    public async Task ADownloadThatDoesNotMatchIsDiscardedAndNeverRun()
    {
        _releases.Files["WinLogRotate-Setup-0.2.0-full.exe"] = Encoding.UTF8.GetBytes("tampered");

        var result = await Engine().ApplyAsync(Latest, PerUser, restartGui: true, cancellationToken: Ct);

        result.Outcome.ShouldBe(ApplyOutcome.Failed);
        result.Message.ShouldContain("checksum");
        result.Download.ShouldNotBeNull().Problem.ShouldBe("checksum mismatch");
        _launcher.Launches.ShouldBeEmpty();
        UpdateFolders().ShouldBeEmpty();
    }

    [Fact]
    public async Task MissingChecksumsRefuseBeforeTheInstallerIsDownloaded()
    {
        _releases.Files.Remove(ReleaseAssets.SumsFileName);

        var result = await Engine().ApplyAsync(Latest, PerUser, restartGui: true, cancellationToken: Ct);

        result.Outcome.ShouldBe(ApplyOutcome.Failed);
        result.Download.ShouldBeNull();
        _releases.Requests.ShouldBe(1);
        _launcher.Launches.ShouldBeEmpty();
    }

    [Fact]
    public async Task AMalformedChecksumFileRefuses()
    {
        _releases.Files[ReleaseAssets.SumsFileName] = Encoding.UTF8.GetBytes("<html>rate limited</html>");

        var result = await Engine().ApplyAsync(Latest, PerUser, restartGui: true, cancellationToken: Ct);

        result.Outcome.ShouldBe(ApplyOutcome.Failed);
        _launcher.Launches.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnInstallerTheChecksumsDoNotListIsNotDownloaded()
    {
        _releases.Files[ReleaseAssets.SumsFileName] = Encoding.UTF8.GetBytes($"{Hex(Installer)}  WinLogRotate-Setup-0.2.0-min.exe\n");

        var result = await Engine().ApplyAsync(Latest, PerUser, restartGui: true, cancellationToken: Ct);

        result.Outcome.ShouldBe(ApplyOutcome.Failed);
        result.Message.ShouldContain("does not list");
        _releases.Requests.ShouldBe(1);
    }

    [Fact]
    public async Task ADeclinedPromptChangesNothing()
    {
        _launcher.Result = new LaunchResult(false, true, "declined");

        var result = await Engine().ApplyAsync(Latest, PerMachine, restartGui: true, cancellationToken: Ct);

        result.Outcome.ShouldBe(ApplyOutcome.Declined);
        UpdateFolders().ShouldBeEmpty();
    }

    [Fact]
    public async Task AnInstallerThatWillNotStartIsReported()
    {
        _launcher.Result = new LaunchResult(false, false, "blocked by policy");

        var result = await Engine().ApplyAsync(Latest, PerUser, restartGui: true, cancellationToken: Ct);

        result.Outcome.ShouldBe(ApplyOutcome.Failed);
        result.Message.ShouldContain("blocked by policy");
        UpdateFolders().ShouldBeEmpty();
    }

    [Fact]
    public void OnlyOldDownloadsAreSwept()
    {
        var old = Directory.CreateDirectory(Path.Combine(_temp, UpdateEngine.DownloadDirectoryPrefix + "old"));
        var fresh = Directory.CreateDirectory(Path.Combine(_temp, UpdateEngine.DownloadDirectoryPrefix + "fresh"));
        var unrelated = Directory.CreateDirectory(Path.Combine(_temp, "SomethingElse-update-old"));
        var now = DateTime.UtcNow;
        Directory.SetLastWriteTimeUtc(old.FullName, now.AddDays(-2));
        Directory.SetLastWriteTimeUtc(unrelated.FullName, now.AddDays(-2));

        UpdateEngine.SweepOldDownloads(_temp, now);

        Directory.Exists(old.FullName).ShouldBeFalse();
        Directory.Exists(fresh.FullName).ShouldBeTrue();
        Directory.Exists(unrelated.FullName).ShouldBeTrue();
    }

    /// <summary>A fictional repository: the real owner is named in one file only.</summary>
    private const string Feed = "https://github.com/example/Product/releases/latest";

    [Theory]
    [InlineData("https://github.com/example/Product/releases/tag/v0.1.0", "0.1.0")]
    [InlineData("https://github.com/example/Product/releases/tag/v12.3.45", "12.3.45")]
    [InlineData("https://github.com/example/Product/releases/tag/0.2.1", "0.2.1")]
    [InlineData("https://github.com/Example/product/releases/tag/v1.0.0", "1.0.0")]
    [InlineData("/example/Product/releases/tag/v1.0.0", "1.0.0")]
    public void TheLatestRedirectNamesTheRelease(string location, string expected) =>
        UpdateEngine.VersionFromTagUrl(location, Feed).ShouldBe(Version.Parse(expected));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://github.com/example/Product/releases")]
    [InlineData("https://github.com/login?return_to=x")]
    [InlineData("https://github.com/example/Product/releases/tag/v0.8")]
    [InlineData("https://github.com/example/Product/releases/tag/v1.2.3.4")]
    [InlineData("https://github.com/example/Product/releases/tag/nightly")]
    [InlineData("https://github.com/someone-else/Product/releases/tag/v1.0.0")]
    [InlineData("https://github.com/example/Other/releases/tag/v1.0.0")]
    [InlineData("http://github.com/example/Product/releases/tag/v1.0.0")]
    [InlineData("https://evil.example/example/Product/releases/tag/v1.0.0")]
    public void AnythingElseIsNotAnAnswer(string? location) =>
        UpdateEngine.VersionFromTagUrl(location, Feed).ShouldBeNull();

    /// <summary>The feed the product polls names its own tags in the shape this reads.</summary>
    [Fact]
    public void TheRealFeedsTagsAreRead() =>
        UpdateEngine.VersionFromTagUrl(UpdateCommand.LatestReleaseUrl.Replace("/latest", "/tag/v1.2.3", StringComparison.Ordinal), UpdateCommand.LatestReleaseUrl)
            .ShouldBe(new Version(1, 2, 3));

    [Theory]
    [InlineData("github.com", true)]
    [InlineData("objects.githubusercontent.com", true)]
    [InlineData("release-assets.githubusercontent.com", true)]
    [InlineData("codeload.github.com", true)]
    [InlineData("github.com.evil.example", false)]
    [InlineData("evilgithub.com", false)]
    [InlineData("githubusercontent.com.evil.example", false)]
    public void DownloadsOnlyFollowGitHub(string host, bool trusted) =>
        UpdateEngine.IsTrustedDownloadHost(host).ShouldBe(trusted);

    private sealed class FakeReleases : IReleaseSource
    {
        public Version? Latest { get; set; }

        public Dictionary<string, byte[]> Files { get; } = [];

        public int Requests { get; private set; }

        public Task<Version?> FindLatestAsync(CancellationToken cancellationToken) => Task.FromResult(Latest);

        public Task<string?> FetchTextAsync(Version release, string assetName, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(Files.TryGetValue(assetName, out var data) ? Encoding.UTF8.GetString(data) : null);
        }

        public async Task<Download> DownloadAsync(
            Version release, string assetName, string toPath, Action<long, long?> progress, CancellationToken cancellationToken)
        {
            Requests++;

            if (!Files.TryGetValue(assetName, out var data))
            {
                return new Download(null, 0, "404");
            }

            await File.WriteAllBytesAsync(toPath, data, cancellationToken);
            progress(data.Length, data.Length);
            return new Download(SHA256.HashData(data), data.Length, null);
        }
    }

    private sealed class FakeLauncher : IInstallerLauncher
    {
        public List<(string Installer, IReadOnlyList<string> Arguments, bool Elevate, byte[] Contents)> Launches { get; } = [];

        public LaunchResult Result { get; set; } = LaunchResult.Ok;

        public LaunchResult Launch(string installer, IReadOnlyList<string> arguments, bool elevate)
        {
            // Read now: a declined or failed launch has its folder discarded straight after.
            Launches.Add((installer, arguments, elevate, File.ReadAllBytes(installer)));
            return Result;
        }
    }
}
