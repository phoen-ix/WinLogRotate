using Shouldly;
using WinLogRotate.Gui.Cli;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>What the console remembers, read leniently and written completely.</summary>
public sealed class GuiPreferencesTests
{
    [Fact]
    public void TheDefaultIsManualAndNeverChecked()
    {
        GuiPreferences.Default.UpdateCheck.ShouldBe(UpdateCheckMode.Manual);
        GuiPreferences.Default.LastUpdateCheckUtc.ShouldBeNull();
    }

    [Fact]
    public void WhatIsWrittenIsReadBack()
    {
        var written = new GuiPreferences
        {
            UpdateCheck = UpdateCheckMode.Daily,
            LastUpdateCheckUtc = new DateTimeOffset(2026, 9, 18, 19, 4, 0, TimeSpan.Zero),
        };

        var read = GuiPreferences.Parse(written.ToJson());

        read.ShouldBe(written);
        written.ToJson().ShouldContain("\"updateCheck\": \"daily\"");
        written.ToJson().ShouldContain("\"lastUpdateCheckUtc\": \"2026-09-18T19:04:00Z\"");
    }

    [Fact]
    public void AStampIsStoredInUtcWhateverZoneItCameIn()
    {
        var local = new DateTimeOffset(2026, 9, 18, 21, 4, 0, TimeSpan.FromHours(2));

        GuiPreferences.Parse(new GuiPreferences { LastUpdateCheckUtc = local }.ToJson())
            .LastUpdateCheckUtc.ShouldBe(local.ToUniversalTime());
    }

    /// <summary>
    /// The default is never consent.
    /// </summary>
    /// <remarks>
    /// A damaged, hand-edited or newer file cannot turn the daily check on: only the one
    /// spelling this console writes does.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"updateCheck\": \"always\"}")]
    [InlineData("{\"updateCheck\": true}")]
    [InlineData("{\"updateCheck\": \"daily\", \"lastUpdateCheckUtc\": \"yesterday\"}")]
    public void AnythingUnrecognisedIsTheDefault(string json)
    {
        var read = GuiPreferences.Parse(json);

        read.UpdateCheck.ShouldBe(json.Contains("\"daily\"", StringComparison.Ordinal) ? UpdateCheckMode.Daily : UpdateCheckMode.Manual);
        read.LastUpdateCheckUtc.ShouldBeNull();
    }

    [Fact]
    public void DailyIsReadWithoutRegardToCase() =>
        GuiPreferences.Parse("{\"updateCheck\": \"Daily\"}").UpdateCheck.ShouldBe(UpdateCheckMode.Daily);

    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NeverCheckedIsDue() => GuiPreferences.IsCheckDue(null, Now).ShouldBeTrue();

    [Fact]
    public void ADayAgoIsDue() => GuiPreferences.IsCheckDue(Now.AddDays(-1), Now).ShouldBeTrue();

    [Fact]
    public void AnHourAgoIsNotDue() => GuiPreferences.IsCheckDue(Now.AddHours(-1), Now).ShouldBeFalse();

    /// <summary>A clock that went backwards must not silence the check until it catches up.</summary>
    [Fact]
    public void AStampInTheFutureIsDue() => GuiPreferences.IsCheckDue(Now.AddDays(3), Now).ShouldBeTrue();
}

/// <summary>Reading <c>update check --json</c> for the panel and the banner.</summary>
public sealed class UpdateStatusProjectionTests
{
    private static CliResult Ok(string payload) => new()
    {
        ExitCode = ExitCode.Ok,
        StdOut = $"{{\"verb\":\"update check\",\"ok\":true,\"exitCode\":0,\"result\":{payload},\"diagnostics\":[]}}",
        StdErr = string.Empty,
        Verb = "update check",
    };

    [Fact]
    public void ANewerReleaseIsOfferedWithItsScope()
    {
        var status = UpdateStatusProjection.From(
            Ok("{\"current\":\"0.17.0\",\"latest\":\"0.18.0\",\"updateAvailable\":true,\"detail\":\"https://example.invalid\",\"scope\":\"PerMachine\",\"variant\":\"full\"}"),
            "0.17.0");

        status.Tone.ShouldBe(CheckTone.Clean);
        status.Available.ShouldBeTrue();
        status.Latest.ShouldBe("0.18.0");
        status.NeedsElevation.ShouldBeTrue();
        status.Sentence.ShouldBe("WinLogRotate 0.18.0 is available (you have 0.17.0).");
    }

    [Fact]
    public void APerUserInstallDoesNotNeedElevation() =>
        UpdateStatusProjection.From(
            Ok("{\"current\":\"0.17.0\",\"latest\":\"0.18.0\",\"updateAvailable\":true,\"scope\":\"PerUser\"}"), "0.17.0")
            .NeedsElevation.ShouldBeFalse();

    [Fact]
    public void TheNewestReleaseSaysSo()
    {
        var status = UpdateStatusProjection.From(
            Ok("{\"current\":\"0.18.0\",\"latest\":\"0.18.0\",\"updateAvailable\":false,\"detail\":\"up to date\"}"), "0.18.0");

        status.Available.ShouldBeFalse();
        status.Sentence.ShouldBe("You have the newest release, 0.18.0.");
    }

    /// <summary>Exit 0 and a warning: the panel must not read that as "newest".</summary>
    [Fact]
    public void AnUnreachableFeedIsAWarningNotTheNewestRelease()
    {
        var status = UpdateStatusProjection.From(
            Ok("{\"current\":\"0.17.0\",\"updateAvailable\":false,\"detail\":\"could not reach the feed\"}"), "0.17.0");

        status.Tone.ShouldBe(CheckTone.Warning);
        status.Available.ShouldBeFalse();
        status.Sentence.ShouldBe(UpdateText.CouldNotCheck);
    }

    [Fact]
    public void APolicyBlockIsSaidPlainly() =>
        UpdateStatusProjection.From(
            Ok("{\"current\":\"0.17.0\",\"updateAvailable\":false,\"detail\":\"disabled by policy\"}"), "0.17.0")
            .Sentence.ShouldBe(UpdateText.DisabledByPolicy);

    [Fact]
    public void ACliThatCouldNotRunIsAnError()
    {
        var status = UpdateStatusProjection.From(
            new CliResult { ExitCode = -1, StdOut = "", StdErr = "", Failure = CliFailure.NotFound }, "0.17.0");

        status.Tone.ShouldBe(CheckTone.Error);
        status.Sentence.ShouldContain("could not be found");
    }

    [Fact]
    public void AnAnswerThatCannotBeReadIsAnErrorNotACrash()
    {
        var status = UpdateStatusProjection.From(
            new CliResult { ExitCode = ExitCode.Ok, StdOut = "{\"verb\":\"update check\"}", StdErr = "" }, "0.17.0");

        status.Tone.ShouldBe(CheckTone.Error);
        status.Sentence.ShouldContain("could not be read");
    }

    /// <summary>The spellings the projection reads are the ones the verb writes.</summary>
    [Fact]
    public void TheFieldsReadAreTheFieldsTheVerbWrites()
    {
        var snapshot = File.ReadAllText(Path.Combine(
            RepoRoot.Find().FullName, "tests", "WinLogRotate.Core.Tests", "envelope", "update.txt"));

        foreach (var field in new[] { "latest", "updateAvailable", "detail", "scope", "installing" })
        {
            snapshot.ShouldContain($"result.{field} :", customMessage: $"the projection reads {field}");
        }
    }
}

/// <summary>Reading what <c>update apply --json-stream</c> wrote.</summary>
public sealed class UpdateApplyProjectionTests
{
    private const string Progress =
        "{\"ts\":\"\",\"run\":\"r\",\"operation\":\"update\",\"phase\":\"apply\",\"src\":\"WinLogRotate-Setup-0.18.0-full.exe\",\"bytesAfter\":12582912,\"reason\":\"12.0 MB of 47.1 MB\"}";

    private const string Verdict =
        "{\"ts\":\"\",\"run\":\"r\",\"operation\":\"update\",\"phase\":\"apply\",\"result\":\"ok\",\"src\":\"WinLogRotate-Setup-0.18.0-full.exe\",\"reason\":\"verified\"}";

    private static CliResult Stream(int exit, params string[] lines) => new()
    {
        ExitCode = exit,
        StdOut = string.Join('\n', lines) + "\n",
        StdErr = string.Empty,
        Verb = "update apply",
    };

    [Fact]
    public void ProgressBecomesTheStatusLine() =>
        UpdateApplyProjection.ProgressLine(Progress).ShouldBe("Downloading\u2026 12.0 MB of 47.1 MB");

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData(Verdict)]
    [InlineData("{\"verb\":\"update apply\",\"ok\":true}")]
    public void OnlyProgressIsShownAsProgress(string line) =>
        UpdateApplyProjection.ProgressLine(line).ShouldBeNull();

    [Fact]
    public void AHandOverIsInstalling()
    {
        var outcome = UpdateApplyProjection.From(Stream(ExitCode.Ok, Progress, Verdict,
            "{\"schema\":1,\"verb\":\"update apply\",\"ok\":true,\"exitCode\":0,\"result\":{\"installing\":true,\"installer\":\"C:\\\\x.exe\"},\"diagnostics\":[]}"));

        outcome.Kind.ShouldBe(UpdateOutcomeKind.Installing);
        outcome.Message.ShouldBe(UpdateText.Installing);
    }

    [Fact]
    public void ARefusalSpeaksInTheVerbsOwnWords()
    {
        var outcome = UpdateApplyProjection.From(Stream(ExitCode.Errors,
            "{\"schema\":1,\"verb\":\"update apply\",\"ok\":false,\"exitCode\":1,\"result\":{\"installing\":false},"
            + "\"diagnostics\":[{\"severity\":\"Error\",\"code\":\"LR4006\",\"message\":\"The download did not match the release's checksum and was discarded.\",\"remedy\":\"Try again.\"}]}"));

        outcome.Kind.ShouldBe(UpdateOutcomeKind.Refused);
        outcome.Message.ShouldBe("The download did not match the release's checksum and was discarded.");
        outcome.Details.ShouldNotBeNull().ShouldContain("Try again.", customMessage: "the remedy travels in the details");
    }

    [Fact]
    public void DecliningTheUacPromptIsNotAnError()
    {
        var outcome = UpdateApplyProjection.From(new CliResult { ExitCode = -1, StdOut = "", StdErr = "", Failure = CliFailure.UacDeclined });

        outcome.Kind.ShouldBe(UpdateOutcomeKind.Cancelled);
        outcome.Message.ShouldBe("Elevation was cancelled. Nothing was changed.");
    }

    [Fact]
    public void AStreamWithNoEnvelopeIsAFailureNotACrash()
    {
        var outcome = UpdateApplyProjection.From(Stream(ExitCode.Ok, Progress));

        outcome.Kind.ShouldBe(UpdateOutcomeKind.Failed);
        outcome.Message.ShouldContain("did not say how it ended");
    }

    [Fact]
    public void TheStampIsInTheOperatorsZone()
    {
        var utc = new DateTimeOffset(2026, 9, 18, 19, 4, 0, TimeSpan.Zero);
        var vienna = TimeZoneInfo.CreateCustomTimeZone("x", TimeSpan.FromHours(2), "x", "x");

        UpdateApplyProjection.Stamp(utc, vienna).ShouldBe("Last checked 2026-09-18 21:04.");
    }
}

/// <summary>The words on the panel are for the person at the window.</summary>
public sealed class UpdateTextTests
{
    [Fact]
    public void TheSentencesSayNothingAPersonWouldHaveToLookUp()
    {
        string[] sentences =
        [
            UpdateText.OnlyWhenAsked, UpdateText.OnceADay, UpdateText.NeverChecked, UpdateText.Checking,
            UpdateText.DisabledByPolicy, UpdateText.CouldNotCheck, UpdateText.Installing, UpdateText.DidNotInstall,
            UpdateText.Available("0.18.0", "0.17.0"), UpdateText.Newest("0.17.0"), UpdateText.Banner("0.18.0"),
            UpdateText.InstalledButStillOpen("0.18.0"),
        ];

        foreach (var sentence in sentences)
        {
            sentence.ShouldNotContain("json", Case.Insensitive);
            sentence.ShouldNotContain("envelope", Case.Insensitive);
            sentence.ShouldNotContain("LR4", Case.Sensitive);
            sentence.ShouldNotContain("winlogrotate update", Case.Insensitive);
            sentence.ShouldNotContain(".exe", Case.Insensitive);
            sentence.ShouldNotContain("SHA", Case.Sensitive);
        }
    }

    [Fact]
    public void TheBannerSaysWhereToGo() =>
        UpdateText.Banner("0.18.0").ShouldBe("WinLogRotate 0.18.0 is available. Install it from Settings.");
}

/// <summary>The panel's buttons have room for their captions.</summary>
public sealed class UpdatesPanelLayoutTests
{
    [Fact]
    public void EveryButtonHasRoomForItsCaption()
    {
        foreach (var button in new[] { UpdatesPanel.CheckNow, UpdatesPanel.Update })
        {
            button.Width.ShouldBeGreaterThanOrEqualTo((button.Text.Length * 6) + 12, button.Text);
        }
    }

    /// <summary>A title, two radio rows and a button row, each about 24 tall, with padding.</summary>
    [Fact]
    public void ThePanelIsTallEnoughForItsFourRows() =>
        UpdatesPanel.Height.ShouldBeGreaterThanOrEqualTo((4 * 24) + (2 * UpdatesPanel.Padding));
}
