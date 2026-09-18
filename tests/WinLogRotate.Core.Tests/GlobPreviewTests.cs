using Shouldly;
using WinLogRotate.Cli.Commands;
using WinLogRotate.Cli.Output;
using WinLogRotate.Gui.Cli;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// The sentence under the files box says what a line matches, in words a new user can act on.
/// </summary>
/// <remarks>
/// The refusals drive the real verb through the real JSON sink, so the guard's own sentence is
/// what is asserted. A match count needs a Windows path the enumerator can walk, which this leg
/// has not, so those cases feed the verb's documented payload by hand.
/// </remarks>
public sealed class GlobPreviewTests
{
    private static CliResult Glob(string line)
    {
        var writer = new StringWriter();
        var sink = new JsonOutputSink(verbose: false, stream: false, streamTo: writer);
        var ctx = new CommandContext(sink, CommandTree.Build().Parse([.. GlobPreviewProjection.Arguments(line)]));

        var exit = GlobCommand.Run(ctx, line);

        return new CliResult { ExitCode = exit, StdOut = writer.ToString(), StdErr = "", Verb = "glob" };
    }

    /// <summary>The verb's documented payload, on one line as the sink writes it.</summary>
    private static CliResult Payload(int exitCode, int count, long bytes, string? resolved = null, string diagnostics = "[]") =>
        new()
        {
            ExitCode = exitCode,
            StdOut = "{\"schema\":1,\"product\":\"WinLogRotate\",\"version\":\"0.0.0\",\"verb\":\"glob\","
                     + $"\"ok\":{(exitCode == 0 ? "true" : "false")},\"exitCode\":{exitCode},"
                     + "\"result\":{\"pattern\":\"C:/logs/*.log\",\"anchor\":\"C:/logs\","
                     + (resolved is null ? "" : $"\"resolvedAnchor\":\"{resolved}\",")
                     + $"\"count\":{count},\"totalBytes\":{bytes},\"files\":[]}},\"diagnostics\":{diagnostics}}}",
            StdErr = "",
            Verb = "glob",
        };

    /// <summary>The verb takes no configuration directory, and a line sent with one is a parse error.</summary>
    [Fact]
    public void ThePreviewArgumentsParseWithoutAConfigDirectory()
    {
        CommandTree.Build().Parse([.. GlobPreviewProjection.Arguments("C:/x/*.log"), "--no-color"]).Errors.ShouldBeEmpty();
        CommandTree.Build().Parse([.. GlobPreviewProjection.Arguments("C:/x/*.log"), "--config-dir", "C:/c"]).Errors.ShouldNotBeEmpty();
        GlobPreviewProjection.Arguments("  C:/x/*.log ").ShouldBe(["glob", "C:/x/*.log", "--json"]);
    }

    [Fact]
    public void ARefusedLineIsSaidInTheGuardsOwnWords()
    {
        var preview = GlobPreviewProjection.From(Glob("C:/Windows/System32/*.log"));

        preview.Tone.ShouldBe(CheckTone.Error);
        preview.Sentence.ShouldContain("C:\\Windows", Case.Sensitive);
        preview.Sentence.ShouldContain("will not delete files from", Case.Sensitive);
        preview.Remedy.ShouldContain("allowdangerous", Case.Sensitive);
    }

    [Fact]
    public void ARelativeLineIsSaidToNeedAFullPath()
    {
        var preview = GlobPreviewProjection.From(Glob("logs/*.log"));

        preview.Tone.ShouldBe(CheckTone.Error);
        preview.Sentence.ShouldContain("not an absolute path", Case.Sensitive);
    }

    [Fact]
    public void AMatchIsCounted()
    {
        GlobPreviewProjection.From(Payload(0, 12, 357_000_000))
            .ShouldBe(new GlobPreview(CheckTone.Clean, "Matches 12 files, 340.5 MB.", "", 12, 357_000_000));

        GlobPreviewProjection.From(Payload(0, 1, 2_150)).Sentence.ShouldBe("Matches 1 file, 2.1 KB.");
    }

    [Fact]
    public void ALinkedFolderIsNamed() =>
        GlobPreviewProjection.From(Payload(0, 3, 10, resolved: "D:/real/logs")).Sentence
            .ShouldBe("Matches 3 files, 10 B. The folder is a link to D:/real/logs.");

    [Fact]
    public void NoMatchesIsAWarningThatNamesTheAllowNoMatchesSetting()
    {
        var preview = GlobPreviewProjection.From(Payload(0, 0, 0));

        preview.Tone.ShouldBe(CheckTone.Warning);
        preview.Sentence.ShouldContain("Matches no files", Case.Sensitive);
        preview.Sentence.ShouldContain("Allow no matches", Case.Sensitive);
    }

    [Fact]
    public void AFolderSkippedMidWalkIsAWarningWithTheCount()
    {
        var skipped = """[{"severity":"Error","code":"LR9004","message":"'C:/logs/sys' is a junction into C:\\Windows and was not followed.","remedy":"Remove the junction."}]""";

        var preview = GlobPreviewProjection.From(Payload(1, 4, 2048, diagnostics: skipped));

        preview.Tone.ShouldBe(CheckTone.Warning);
        preview.Sentence.ShouldBe("Matches 4 files, 2.0 KB, but a folder was skipped: 'C:/logs/sys' is a junction into C:\\Windows and was not followed.");
        preview.Count.ShouldBe(4);
    }

    [Fact]
    public void AnAbsentCliDegradesToOneLine()
    {
        var preview = GlobPreviewProjection.From(new CliResult
        {
            ExitCode = -1,
            StdOut = "",
            StdErr = "",
            Failure = CliFailure.NotFound,
        });

        preview.Tone.ShouldBe(CheckTone.Warning);
        preview.Sentence.ShouldStartWith("Could not preview: ");
    }

    [Fact]
    public void SeveralLinesAreOneSentence()
    {
        var a = GlobPreviewProjection.From(Payload(0, 2, 1024));
        var b = GlobPreviewProjection.From(Payload(0, 3, 1024));

        GlobPreviewProjection.Combine([a, b]).Sentence.ShouldBe("2 lines match 5 files, 2.0 KB.");

        var refused = GlobPreviewProjection.From(Glob("C:/Windows/System32/*.log"));
        var combined = GlobPreviewProjection.Combine([a, refused]);
        combined.Tone.ShouldBe(CheckTone.Error);
        combined.Sentence.ShouldStartWith("Line 2: ");

        GlobPreviewProjection.Combine([]).Sentence.ShouldBe("");
        GlobPreviewProjection.Combine([a]).ShouldBe(a);
    }
}
