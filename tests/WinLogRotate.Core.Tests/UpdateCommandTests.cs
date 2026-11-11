using Shouldly;
using WinLogRotate.Cli.Commands;
using WinLogRotate.Cli.Output;
using WinLogRotate.Contracts;
using WinLogRotate.Hosting.Install;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// <c>update check</c> and <c>update apply</c> over <see cref="UpdateEngine"/>, with the
/// network, the registry and the installer replaced by fakes.
/// </summary>
/// <remarks>
/// <para>
/// Each step that can decline is asserted to decline before the next one costs anything, and
/// the success path is asserted down to the exact command line the installer is given - because
/// the installer validates its switches and exits without installing on one it does not know,
/// and nothing here would notice.
/// </para>
/// <para>
/// The refusal doctrine this verb used to be the example for still holds: a verb that declines
/// to do what it is named after does not report success. It used to decline always; now it
/// declines for a portable copy, and <c>update apply &amp;&amp; next</c> still must not run
/// <c>next</c> when nothing was installed.
/// </para>
/// </remarks>
public sealed class UpdateCommandTests : IDisposable
{
    private const string Digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary>A released build's version. The test assembly's own is the development one,
    /// which is never offered an update.</summary>
    private const string Current = "1.0.0";

    private static readonly Version Newer = new(99, 0, 0);

    private readonly string _temp = Directory.CreateTempSubdirectory("lr-update-").FullName;

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    private sealed class Sink : IOutputSink
    {
        private readonly List<CliDiagnostic> _diagnostics = [];

        public List<string> Lines { get; } = [];

        public List<CliEvent> Events { get; } = [];

        public object? Result { get; private set; }

        public string? Verb { get; private set; }

        public bool Verbose => false;

        public IReadOnlyList<CliDiagnostic> Diagnostics => _diagnostics;

        public void Diagnostic(CliDiagnostic diagnostic) => _diagnostics.Add(diagnostic);

        public void Event(CliEvent evt) => Events.Add(evt);

        public void Line(string text) => Lines.Add(text);

        public int Complete<T>(string verb, int exitCode, T? result)
        {
            Verb = verb;
            Result = result;
            return exitCode;
        }
    }

    /// <summary>A release feed made of a dictionary.</summary>
    private sealed class Releases : IReleaseSource
    {
        public Version? Latest { get; init; } = Newer;

        public Dictionary<string, string> Text { get; } = [];

        public byte[]? Digest { get; init; } = Convert.FromHexString(UpdateCommandTests.Digest);

        public long Bytes { get; init; } = 47L * 1024 * 1024;

        public string? Problem { get; init; }

        public List<string> Fetched { get; } = [];

        public string? DownloadedTo { get; private set; }

        public Task<Version?> FindLatestAsync(CancellationToken cancellationToken) => Task.FromResult(Latest);

        public Task<string?> FetchTextAsync(Version release, string assetName, CancellationToken cancellationToken)
        {
            Fetched.Add($"v{release.ToString(3)}/{assetName}");
            return Task.FromResult(Text.TryGetValue(assetName, out var text) ? text : null);
        }

        public Task<Download> DownloadAsync(Version release, string assetName, string toPath, Action<long, long?> progress, CancellationToken cancellationToken)
        {
            Fetched.Add($"v{release.ToString(3)}/{assetName}");
            DownloadedTo = toPath;

            // As the real one reports: at the start, along the way, and at the end.
            for (var i = 0; i <= 10; i++)
            {
                progress(Bytes * i / 10, Bytes);
            }

            return Task.FromResult(new Download(Digest, Bytes, Problem));
        }
    }

    private sealed class Launcher : IInstallerLauncher
    {
        public LaunchResult Result { get; init; } = LaunchResult.Ok;

        public string? Installer { get; private set; }

        public IReadOnlyList<string>? Arguments { get; private set; }

        public bool? Elevate { get; private set; }

        public LaunchResult Launch(string installer, IReadOnlyList<string> arguments, bool elevate)
        {
            Installer = installer;
            Arguments = arguments;
            Elevate = elevate;
            return Result;
        }
    }

    private static InstallRecord PerMachine(string hostKind = "task", string variant = "full") => new()
    {
        Scope = InstallScope.PerMachine,
        Location = @"C:\Program Files\WinLogRotate",
        HostKind = hostKind,
        BuildVariant = variant,
    };

    private static InstallRecord PerUser() => new()
    {
        Scope = InstallScope.PerUser,
        Location = @"C:\Users\me\AppData\Local\Programs\WinLogRotate",
        HostKind = "none",
        BuildVariant = "min",
    };

    private static Releases Complete(string variant = "full", string? digest = null)
    {
        var releases = new Releases();
        var name = ReleaseAssets.InstallerName(variant, Newer);
        releases.Text[ReleaseAssets.SumsFileName] = $"{digest ?? Digest}  {name}\r\n{Digest}  other.zip\r\n";
        return releases;
    }

    private (Sink Sink, int Exit) Apply(
        IReleaseSource releases,
        InstallRecord record,
        Launcher? launcher = null,
        bool elevated = true,
        bool restartGui = true,
        string current = Current,
        bool policy = false,
        bool runtime = true)
    {
        var sink = new Sink();
        var ctx = new CommandContext(sink, CommandTree.Build().Parse(["update", "apply"]));
        var engine = new UpdateEngine(releases, launcher ?? new Launcher(), () => policy, () => elevated, () => runtime, _temp);

        var exit = UpdateCommand
            .ApplyAsync(ctx, restartGui, engine, () => record, current)
            .GetAwaiter().GetResult();

        return (sink, exit);
    }

    private async Task<Sink> CheckAsync(IReleaseSource releases, InstallRecord record, string current = Current)
    {
        var sink = new Sink();
        var ctx = new CommandContext(sink, CommandTree.Build().Parse(["update", "check"]));
        var engine = new UpdateEngine(releases, new Launcher(), () => false, () => false, () => true, _temp);

        (await UpdateCommand.CheckAsync(ctx, engine, () => record, current)).ShouldBe(ExitCode.Ok);

        return sink;
    }

    private static UpdateResult Payload(Sink sink) => sink.Result.ShouldBeOfType<UpdateResult>();

    [Fact]
    public void TheHappyPathDownloadsVerifiesAndHandsOver()
    {
        var releases = Complete();
        var launcher = new Launcher();

        var (sink, exit) = Apply(releases, PerMachine(hostKind: "none"), launcher);

        exit.ShouldBe(ExitCode.Ok);
        sink.Diagnostics.ShouldBeEmpty();
        launcher.Elevate.ShouldBe(false, "this token is already elevated");

        var result = Payload(sink);
        result.Installing.ShouldBeTrue();
        result.UpdateAvailable.ShouldBeTrue();
        result.Latest.ShouldBe("99.0.0");
        result.Scope.ShouldBe("PerMachine");
        result.Variant.ShouldBe("full");
        result.Detail.ShouldBe("installing");
        result.Installer.ShouldBe(launcher.Installer);

        // The exact command line, every switch explicit: the recorded host, the recorded scope,
        // no runtime bootstrap, and the console brought back because it asked to be.
        launcher.Arguments.ShouldBe(["/S", "/AllUsers", "/HOST=none", "/NORUNTIME", "/RESTART"]);
        launcher.Installer.ShouldNotBeNull().ShouldEndWith("WinLogRotate-Setup-99.0.0-full.exe");
        Path.GetFileName(Path.GetDirectoryName(launcher.Installer)!).ShouldStartWith("WinLogRotate-update-");

        sink.Lines.ShouldContain(l => l.StartsWith("Handed over to the installer", StringComparison.Ordinal));
        sink.Lines.ShouldContain(l => l.Contains("started again", StringComparison.Ordinal));
    }

    [Fact]
    public void TheSumsAreFetchedBeforeTheInstaller()
    {
        var releases = Complete();

        Apply(releases, PerMachine());

        releases.Fetched.ShouldBe(["v99.0.0/SHA256SUMS.txt", "v99.0.0/WinLogRotate-Setup-99.0.0-full.exe"]);
    }

    [Fact]
    public void TheInstallerFetchedIsTheOneTheRecordNames()
    {
        var releases = Complete(variant: "min");

        var (sink, exit) = Apply(releases, PerUser());

        exit.ShouldBe(ExitCode.Ok);
        releases.Fetched.ShouldContain("v99.0.0/WinLogRotate-Setup-99.0.0-min.exe");
        Payload(sink).Variant.ShouldBe("min");
    }

    [Fact]
    public void APerUserInstallIsReRunUnelevatedForTheCurrentUser()
    {
        var launcher = new Launcher();

        var (_, exit) = Apply(Complete(variant: "min"), PerUser(), launcher, elevated: false, restartGui: false);

        exit.ShouldBe(ExitCode.Ok);
        launcher.Arguments.ShouldBe(["/S", "/CurrentUser", "/HOST=none", "/NORUNTIME"]);
        launcher.Elevate.ShouldBe(false, "a per-user installer asks nothing");
    }

    /// <summary>
    /// Progress is streamed as events, so the console can show the download moving.
    /// </summary>
    /// <remarks>
    /// Throttled to one per five per cent: the text sink prints each one, and a console watching
    /// a download wants to see it move without seeing every packet.
    /// </remarks>
    [Fact]
    public void ProgressIsReportedAsEventsAndEndsWithAVerdict()
    {
        var (sink, _) = Apply(Complete(), PerMachine());

        var progress = sink.Events.Where(e => e.Result is null).ToArray();
        progress.ShouldNotBeEmpty();
        progress.Length.ShouldBeLessThanOrEqualTo(21);
        progress.ShouldAllBe(e => e.Operation == Op.Update && e.Src == "WinLogRotate-Setup-99.0.0-full.exe");
        progress.Last().Reason.ShouldBe("47.0 MB of 47.0 MB");

        var verdict = sink.Events.Last();
        verdict.Result.ShouldBe(OpResult.Ok);
        verdict.Reason.ShouldNotBeNull().ShouldContain("SHA256SUMS.txt");
    }

    [Fact]
    public void NothingNewerIsNotAnError()
    {
        var launcher = new Launcher();

        var (sink, exit) = Apply(new Releases { Latest = new Version(0, 0, 0) }, PerMachine(), launcher);

        exit.ShouldBe(ExitCode.Ok);
        sink.Diagnostics.ShouldBeEmpty();
        Payload(sink).UpdateAvailable.ShouldBeFalse();
        Payload(sink).Installing.ShouldBeFalse();
        launcher.Installer.ShouldBeNull("nothing should have been downloaded or started");
        sink.Lines.ShouldContain(l => l.Contains("is the newest release", StringComparison.Ordinal));
    }

    /// <summary>A feed that cannot be reached is a warning at exit 0, exactly as it is for check.</summary>
    [Fact]
    public void AnUnreachableFeedIsAWarningNotAFailure()
    {
        var (sink, exit) = Apply(new Releases { Latest = null }, PerMachine());

        exit.ShouldBe(ExitCode.Ok);
        sink.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(DiagnosticCode.UpdateCheckFailed);
        sink.Diagnostics[0].Severity.ShouldBe(Severity.Warning);
        Payload(sink).Detail.ShouldBe("could not reach the feed");
    }

    /// <summary>
    /// A verb that declines to do what it is named after does not report success.
    /// </summary>
    /// <remarks>
    /// The doctrine this verb was the example for, back when it declined unconditionally: it
    /// returned exit 0 with <c>updateAvailable: false</c>, the exact shape of "I checked, you
    /// are up to date", and <c>update apply &amp;&amp; echo updated</c> printed "updated" on a
    /// machine that had not been. A portable copy is the case that remains.
    /// </remarks>
    [Fact]
    public void APortableCopyIsRefusedNotUpdated()
    {
        var launcher = new Launcher();

        var releases = Complete();

        var (sink, exit) = Apply(releases, InstallRecord.Portable("full"), launcher);

        exit.ShouldNotBe(ExitCode.Ok, "'update apply && next' must not run next");

        var refusal = sink.Diagnostics.ShouldHaveSingleItem();
        refusal.Severity.ShouldBe(Severity.Error);
        refusal.Code.ShouldBe(DiagnosticCode.NotSupportedHere);
        refusal.Message.ShouldContain("portable");
        refusal.Remedy.ShouldNotBeNull().ShouldContain("unzip");

        sink.Result.ShouldBeNull();
        launcher.Installer.ShouldBeNull();
        releases.Fetched.ShouldBeEmpty("nothing is fetched for a copy that cannot use it");
    }

    /// <summary>
    /// An all-users install is updated from an unelevated prompt: only the installer is elevated.
    /// </summary>
    /// <remarks>
    /// This used to be refused with <c>LR1001</c> before anything was fetched, and the console
    /// ran the whole verb elevated to get past it. Now the verb downloads as whoever ran it and
    /// starts the installer through the administrator prompt.
    /// </remarks>
    [Fact]
    public void APerMachineInstallStartsItsInstallerThroughThePrompt()
    {
        var launcher = new Launcher();

        var (sink, exit) = Apply(Complete(), PerMachine(), launcher, elevated: false);

        exit.ShouldBe(ExitCode.Ok);
        sink.Diagnostics.ShouldBeEmpty();
        launcher.Elevate.ShouldBe(true);
        launcher.Arguments.ShouldBe(["/S", "/AllUsers", "/HOST=task", "/NORUNTIME", "/RESTART"]);
        Payload(sink).Installing.ShouldBeTrue();
    }

    /// <summary>
    /// No to the prompt is not a fault, and not a success either.
    /// </summary>
    /// <remarks>
    /// Exit 1, because <c>update apply &amp;&amp; next</c> must not run <c>next</c> on a machine
    /// still on the old version; <c>declined</c>, because the console says "cancelled" for it
    /// rather than putting up an error.
    /// </remarks>
    [Fact]
    public void ADeclinedPromptIsReportedAsDeclined()
    {
        var launcher = new Launcher { Result = new LaunchResult(false, true, "the administrator prompt was declined") };

        var (sink, exit) = Apply(Complete(), PerMachine(), launcher, elevated: false);

        exit.ShouldBe(ExitCode.Errors);
        sink.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(DiagnosticCode.UpdateNotInstalled);
        sink.Diagnostics[0].Message.ShouldContain("declined");
        Payload(sink).Detail.ShouldBe("declined");
        Payload(sink).Installing.ShouldBeFalse();
        Directory.Exists(Path.GetDirectoryName(launcher.Installer!)).ShouldBeFalse("the download is discarded");
    }

    /// <summary>A local build reports the newest release and installs nothing over itself.</summary>
    [Fact]
    public async Task ADevelopmentBuildIsToldTheLatestAndOfferedNothing()
    {
        var sink = await CheckAsync(Complete(), PerUser(), current: ProductInfo.DevVersion);

        var result = Payload(sink);
        result.Latest.ShouldBe("99.0.0");
        result.UpdateAvailable.ShouldBeFalse();
        result.Detail.ShouldBe("development build");

        var releases = Complete();
        var launcher = new Launcher();

        var (applied, exit) = Apply(releases, PerUser(), launcher, current: ProductInfo.DevVersion);

        exit.ShouldBe(ExitCode.Ok);
        Payload(applied).Detail.ShouldBe("development build");
        releases.Fetched.ShouldBeEmpty();
        launcher.Installer.ShouldBeNull();
    }

    [Fact]
    public void APolicyBlockIsSaidAtExitZero()
    {
        var releases = Complete();

        var (sink, exit) = Apply(releases, PerUser(), policy: true);

        exit.ShouldBe(ExitCode.Ok);
        Payload(sink).Detail.ShouldBe("disabled by policy");
        releases.Fetched.ShouldBeEmpty();
    }

    [Fact]
    public void AMissingSumsFileRefusesBeforeDownloading()
    {
        var releases = new Releases();
        var launcher = new Launcher();

        var (sink, exit) = Apply(releases, PerMachine(), launcher);

        exit.ShouldBe(ExitCode.Errors);
        sink.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(DiagnosticCode.UpdateNotInstalled);
        sink.Diagnostics[0].Message.ShouldContain("SHA256SUMS.txt");
        releases.Fetched.ShouldBe(["v99.0.0/SHA256SUMS.txt"]);
        launcher.Installer.ShouldBeNull();
        Payload(sink).Detail.ShouldBe("not installed");
    }

    [Fact]
    public void AMalformedSumsFileIsRefused()
    {
        var releases = new Releases();
        releases.Text[ReleaseAssets.SumsFileName] = "this is not a sums file\n";

        var (sink, exit) = Apply(releases, PerMachine());

        exit.ShouldBe(ExitCode.Errors);
        sink.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(DiagnosticCode.UpdateNotInstalled);
        sink.Diagnostics[0].Message.ShouldContain("could not be read");
        releases.DownloadedTo.ShouldBeNull();
    }

    [Fact]
    public void AnInstallerTheReleaseDoesNotListIsNotDownloaded()
    {
        var releases = Complete(variant: "min");  // lists -min; the record says full

        var (sink, exit) = Apply(releases, PerMachine(variant: "full"));

        exit.ShouldBe(ExitCode.Errors);
        sink.Diagnostics.ShouldHaveSingleItem().Message.ShouldContain("does not list WinLogRotate-Setup-99.0.0-full.exe");
        releases.DownloadedTo.ShouldBeNull();
    }

    [Fact]
    public void AFailedDownloadIsReportedAndNothingIsStarted()
    {
        var releases = new Releases { Digest = null, Problem = "the connection closed after 12.0 MB of 47.0 MB" };
        releases.Text[ReleaseAssets.SumsFileName] = $"{Digest}  WinLogRotate-Setup-99.0.0-full.exe\n";
        var launcher = new Launcher();

        var (sink, exit) = Apply(releases, PerMachine(), launcher);

        exit.ShouldBe(ExitCode.Errors);
        sink.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(DiagnosticCode.UpdateNotInstalled);
        sink.Diagnostics[0].Message.ShouldContain("could not be downloaded: the connection closed");
        sink.Events.Last().Result.ShouldBe(OpResult.Failed);
        launcher.Installer.ShouldBeNull();
        Directory.Exists(Path.GetDirectoryName(releases.DownloadedTo!)).ShouldBeFalse("the folder is discarded");
    }

    /// <summary>The finding somebody should read the same day.</summary>
    [Fact]
    public void AChecksumMismatchDiscardsTheDownloadAndStartsNothing()
    {
        var releases = Complete(digest: "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");
        var launcher = new Launcher();

        var (sink, exit) = Apply(releases, PerMachine(), launcher);

        exit.ShouldBe(ExitCode.Errors);

        var refusal = sink.Diagnostics.ShouldHaveSingleItem();
        refusal.Severity.ShouldBe(Severity.Error);
        refusal.Code.ShouldBe(DiagnosticCode.UpdateNotInstalled);
        refusal.Message.ShouldBe("The download did not match the release's checksum and was discarded.");

        sink.Events.Last().Result.ShouldBe(OpResult.Failed);
        launcher.Installer.ShouldBeNull();
        Directory.Exists(Path.GetDirectoryName(releases.DownloadedTo!)).ShouldBeFalse();
    }

    /// <summary>A -min install without the Desktop Runtime its console needs: LR4006 with the
    /// way out, before anything is downloaded.</summary>
    [Fact]
    public void AMinInstallWithoutTheDesktopRuntimeIsRefusedWithTheDownloadPage()
    {
        var releases = Complete("min");
        var launcher = new Launcher();

        var (sink, exit) = Apply(releases, PerMachine(variant: "min"), launcher, runtime: false);

        exit.ShouldBe(ExitCode.Errors);
        var refusal = sink.Diagnostics.ShouldHaveSingleItem();
        refusal.Code.ShouldBe(DiagnosticCode.UpdateNotInstalled);
        refusal.Message.ShouldContain($".NET {Environment.Version.Major} Desktop Runtime");
        refusal.Remedy.ShouldNotBeNull().ShouldContain($"https://dotnet.microsoft.com/download/dotnet/{Environment.Version.Major}.0");
        refusal.Remedy.ShouldContain($"winget install Microsoft.DotNet.DesktopRuntime.{Environment.Version.Major}");
        releases.DownloadedTo.ShouldBeNull();
        launcher.Installer.ShouldBeNull();
    }

    [Fact]
    public void AnInstallerThatWillNotStartIsReported()
    {
        var launcher = new Launcher { Result = new LaunchResult(false, false, "Access is denied.") };

        var (sink, exit) = Apply(Complete(), PerMachine(), launcher);

        exit.ShouldBe(ExitCode.Errors);
        sink.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(DiagnosticCode.UpdateNotInstalled);
        sink.Diagnostics[0].Message.ShouldContain("could not be started: Access is denied.");
        Payload(sink).Installing.ShouldBeFalse();
    }

    /// <summary>The installer validates /HOST= and exits without installing on anything else.</summary>
    [Theory]
    [InlineData("task", "task")]
    [InlineData("none", "none")]
    [InlineData("service", "none")]
    [InlineData("", "none")]
    public void OnlyAHostTheInstallerAcceptsIsPassedBack(string recorded, string passed) =>
        UpdateEngine.HostKindFor(PerMachine(hostKind: recorded)).ShouldBe(passed);

    /// <summary>The sentences a person reads say nothing a person would have to look up.</summary>
    [Fact]
    public void TheSentencesAreForPeople()
    {
        var (sink, _) = Apply(Complete(), PerMachine());

        sink.Lines.ShouldContain("install  all users, scheduled task, full build");
        sink.Lines.ShouldContain(l => l.StartsWith("verified WinLogRotate-Setup-99.0.0-full.exe (47.0 MB)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CheckReportsTheInstallRecordForTheConsole()
    {
        var sink = await CheckAsync(Complete(), PerUser());

        var result = Payload(sink);
        result.UpdateAvailable.ShouldBeTrue();
        result.Scope.ShouldBe("PerUser");
        result.Variant.ShouldBe("min");
        result.Installing.ShouldBeFalse();
    }

    /// <summary>The console offers a portable copy the release page, which the check names.</summary>
    [Fact]
    public async Task CheckOnAPortableCopySaysSoAndNamesTheReleasePage()
    {
        var sink = await CheckAsync(Complete(), InstallRecord.Portable("min"));

        Payload(sink).Scope.ShouldBe("Portable");
        Payload(sink).UpdateAvailable.ShouldBeTrue();
        Payload(sink).Detail.ShouldBe(UpdateCommand.LatestReleaseUrl);
    }

    [Theory]
    [InlineData(0L, "0.0 MB")]
    [InlineData(1048576L, "1.0 MB")]
    [InlineData(49283072L, "47.0 MB")]
    public void SizesAreSpelledInMegabytesRegardlessOfLocale(long bytes, string expected) =>
        UpdateEngine.Megabytes(bytes).ShouldBe(expected);

    /// <summary>The option the console passes is one the tree declares and the verb reads.</summary>
    [Fact]
    public void RestartGuiIsAnOptionOfApply()
    {
        var parse = CommandTree.Build().Parse(["update", "apply", "--restart-gui"]);

        parse.Errors.ShouldBeEmpty();
    }
}
