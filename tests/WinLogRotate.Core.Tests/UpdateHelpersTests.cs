using Shouldly;
using WinLogRotate.Contracts;
using WinLogRotate.Hosting.Install;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// Which GUI build an install carries, when the installer did not say.
/// </summary>
/// <remarks>
/// Every install made before <c>BuildVariant</c> was recorded has no record, and that is every
/// install the first in-app update runs on. The file on disk is the only evidence, and the
/// rule that reads it is pure so it can be asserted here rather than on a Windows runner.
/// </remarks>
public sealed class InstallRecordTests
{
    [Theory]
    [InlineData(44L * 1024 * 1024, "full")]
    [InlineData(20L * 1024 * 1024, "full")]
    [InlineData(1L * 1024 * 1024, "min")]
    [InlineData(0L, "min")]
    public void TheVariantIsInferredFromTheSizeOfTheGui(long bytes, string expected) =>
        InstallRecord.InferVariant(bytes).ShouldBe(expected);

    /// <summary>A GUI that is not there at all is treated as the small build.</summary>
    /// <remarks>
    /// Fetching the small installer for a machine that turns out to need the large one costs a
    /// runtime prompt; the other way round costs forty megabytes. Neither is a wrong install.
    /// </remarks>
    [Fact]
    public void AMissingGuiIsTreatedAsTheSmallBuild() =>
        InstallRecord.InferVariant(null).ShouldBe("min");

    [Theory]
    [InlineData("full", 1L, "full")]
    [InlineData("min", 44L * 1024 * 1024, "min")]
    public void ARecordedVariantBeatsTheInference(string recorded, long bytes, string expected) =>
        InstallRecord.ResolveVariant(recorded, bytes).ShouldBe(expected);

    /// <summary>A value nobody ships falls back to the evidence rather than being passed on.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("standard")]
    public void AnUnknownRecordFallsBackToTheInference(string? recorded) =>
        InstallRecord.ResolveVariant(recorded, 44L * 1024 * 1024).ShouldBe("full");
}

/// <summary>The asset names an update fetches are the ones the release workflow publishes.</summary>
public sealed class ReleaseAssetsTests
{
    [Theory]
    [InlineData("full", "WinLogRotate-Setup-0.18.0-full.exe")]
    [InlineData("min", "WinLogRotate-Setup-0.18.0-min.exe")]
    public void TheInstallerNameFollowsTheVariant(string variant, string expected) =>
        ReleaseAssets.InstallerName(variant, new Version(0, 18, 0)).ShouldBe(expected);

    /// <summary>Three parts, always: a four-part assembly version must not leak into a URL.</summary>
    [Fact]
    public void TheVersionIsSpelledWithThreeParts() =>
        ReleaseAssets.InstallerName("min", new Version(1, 2, 3, 4)).ShouldBe("WinLogRotate-Setup-1.2.3-min.exe");

    [Theory]
    [InlineData("")]
    [InlineData("standard")]
    [InlineData("Full")]
    public void AVariantNobodyShipsIsRefused(string variant) =>
        Should.Throw<ArgumentException>(() => ReleaseAssets.InstallerName(variant, new Version(0, 18, 0)));

    [Fact]
    public void TheDownloadPathIsTheTagThenTheName() =>
        ReleaseAssets.DownloadPath(new Version(0, 18, 0), "SHA256SUMS.txt").ShouldBe("v0.18.0/SHA256SUMS.txt");

    /// <summary>
    /// Pinned against the workflow that writes the files.
    /// </summary>
    /// <remarks>
    /// The names are not discovered at run time, so a rename in <c>ci.yml</c> would otherwise
    /// break every installed copy's update the day the next release went out.
    /// </remarks>
    [Fact]
    public void TheWorkflowPublishesTheseNames()
    {
        var workflow = File.ReadAllText(Path.Combine(RepoRoot.Find().FullName, ".github", "workflows", "ci.yml"));

        workflow.ShouldContain("WinLogRotate-Setup-$version$suffix.exe");
        workflow.ShouldContain("foreach ($suffix in '', '-full', '-min')");
        workflow.ShouldContain(ReleaseAssets.SumsFileName);
    }
}

/// <summary>Reading a release's SHA256SUMS.txt, as sha256sum and the release workflow write it.</summary>
public sealed class Sha256SumsTests
{
    private const string Digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void TheWorkflowsOwnFormatIsRead()
    {
        // Two spaces, lower-case hex, CRLF: what Get-FileHash | Set-Content produces on Windows.
        var text = $"{Digest}  WinLogRotate-Setup-0.18.0-full.exe\r\n{Digest}  WinLogRotate-Setup-0.18.0-min.exe\r\n";

        Sha256Sums.TryParse(text, out var sums, out var problem).ShouldBeTrue(problem);

        sums.Count.ShouldBe(2);
        sums.DigestOf("WinLogRotate-Setup-0.18.0-min.exe").ShouldBe(Digest);
    }

    [Fact]
    public void BinaryModeAndUpperCaseAreAccepted()
    {
        Sha256Sums.TryParse($"{Digest.ToUpperInvariant()} *setup.exe\n", out var sums, out _).ShouldBeTrue();

        sums.DigestOf("setup.exe").ShouldBe(Digest);
    }

    [Fact]
    public void AByteOrderMarkAndBlankLinesAreIgnored()
    {
        Sha256Sums.TryParse($"﻿{Digest}  a.exe\n\n   \n{Digest}  b.exe\n", out var sums, out _).ShouldBeTrue();

        sums.Count.ShouldBe(2);
    }

    [Fact]
    public void MatchesComparesTheComputedDigest()
    {
        Sha256Sums.TryParse($"{Digest}  setup.exe\n", out var sums, out _).ShouldBeTrue();

        sums.Matches("setup.exe", Convert.FromHexString(Digest)).ShouldBeTrue();
        sums.Matches("setup.exe", new byte[32]).ShouldBeFalse();
        sums.Matches("other.exe", Convert.FromHexString(Digest)).ShouldBeFalse("a file that is not listed never matches");
    }

    [Theory]
    [InlineData("not a digest  setup.exe\n", "line 1")]
    [InlineData("0123456789abcdef  setup.exe\n", "line 1")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0\n", "line 1")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\n", "names no file")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef *\n", "names no file")]
    [InlineData("", "lists nothing")]
    [InlineData("\n\n", "lists nothing")]
    public void AFileThatCannotBeReadCompletelyIsRefused(string text, string expected)
    {
        Sha256Sums.TryParse(text, out var sums, out var problem).ShouldBeFalse();

        sums.ShouldBeNull();
        problem.ShouldNotBeNull().ShouldContain(expected);
    }

    [Fact]
    public void ANameListedWithTwoDigestsIsRefused()
    {
        var other = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";

        Sha256Sums.TryParse($"{Digest}  setup.exe\n{other}  setup.exe\n", out _, out var problem).ShouldBeFalse();

        problem.ShouldNotBeNull().ShouldContain("listed twice");
    }

    [Fact]
    public void ANameListedTwiceWithTheSameDigestIsMerelyRedundant()
    {
        Sha256Sums.TryParse($"{Digest}  setup.exe\n{Digest}  setup.exe\n", out var sums, out _).ShouldBeTrue();

        sums.Count.ShouldBe(1);
    }
}

/// <summary>The command line an update hands the installer says everything the record knows.</summary>
public sealed class InstallerArgumentsTests
{
    [Fact]
    public void APerMachineInstallIsReRunForAllUsersWithItsHost() =>
        InstallerArguments.For(InstallScope.PerMachine, "none", restartGui: true)
            .ShouldBe(["/S", "/AllUsers", "/HOST=none", "/NORUNTIME", "/RESTART"]);

    [Fact]
    public void APerUserInstallIsReRunForTheCurrentUser() =>
        InstallerArguments.For(InstallScope.PerUser, "task", restartGui: false)
            .ShouldBe(["/S", "/CurrentUser", "/HOST=task", "/NORUNTIME"]);

    /// <summary>A prompt does not want the window back; only the window does.</summary>
    [Fact]
    public void RestartIsPassedOnlyWhenAsked() =>
        InstallerArguments.For(InstallScope.PerMachine, "task", restartGui: false).ShouldNotContain("/RESTART");

    [Fact]
    public void APortableCopyHasNoInstallerToReRun() =>
        Should.Throw<ArgumentException>(() => InstallerArguments.For(InstallScope.Portable, "task", false));

    /// <summary>The installer validates /HOST= and exits 2 on anything else; refuse before it does.</summary>
    [Theory]
    [InlineData("service")]
    [InlineData("Task")]
    [InlineData("")]
    public void AHostTheInstallerWouldRefuseIsRefusedHere(string hostKind) =>
        Should.Throw<ArgumentException>(() => InstallerArguments.For(InstallScope.PerMachine, hostKind, false));

    /// <summary>
    /// Every switch passed is one the installer script parses.
    /// </summary>
    /// <remarks>
    /// The script reads its switches by exact spelling through <c>GetOptions</c>, and an
    /// unrecognised one is silently ignored - which for <c>/RESTART</c> would mean the window
    /// never comes back, with nothing to say why.
    /// </remarks>
    [Fact]
    public void EverySwitchIsOneTheInstallerParses()
    {
        var nsi = File.ReadAllText(Path.Combine(RepoRoot.Find().FullName, "packaging", "winlogrotate.nsi"));

        foreach (var argument in InstallerArguments.For(InstallScope.PerMachine, "task", restartGui: true))
        {
            var switchName = argument.Split('=')[0];

            if (switchName == "/S")
            {
                continue;  // NSIS's own, not parsed by the script
            }

            nsi.ShouldContain($"\"{switchName}", customMessage: $"the installer script must parse {switchName}");
        }
    }
}

/// <summary>A download in progress reads as one, and a finished one as done.</summary>
public sealed class UpdateEventTextTests
{
    private static CliEvent Event(string? result) => new()
    {
        Ts = "2026-09-18T21:04:00+02:00",
        Run = "r",
        Operation = Op.Update,
        Phase = Phase.Apply,
        Result = result,
        Src = "WinLogRotate-Setup-0.18.0-full.exe",
        Reason = "12.0 MB of 47.1 MB",
    };

    [Fact]
    public void ProgressDoesNotClaimAResult() =>
        CliEventText.Describe(Event(null)).ShouldBe("  downloading WinLogRotate-Setup-0.18.0-full.exe  (12.0 MB of 47.1 MB)");

    [Fact]
    public void AFinishedDownloadSaysSo() =>
        CliEventText.Describe(Event(OpResult.Ok)).ShouldStartWith("  did download WinLogRotate-Setup-0.18.0-full.exe");

    [Fact]
    public void AFailedDownloadSaysSo() =>
        CliEventText.Describe(Event(OpResult.Failed)).ShouldStartWith("  failed to download");
}
