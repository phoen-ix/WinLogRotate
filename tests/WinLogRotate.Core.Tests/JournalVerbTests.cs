using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using WinLogRotate.Contracts;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// What <c>winlogrotate journal</c> tells an operator who asks what happened.
/// </summary>
/// <remarks>
/// Twice the truth, until this milestone. It printed every record - the plan half and the apply
/// half of each operation - while showing neither the phase nor the result, so the two arrived as
/// lines identical but for the timestamp, and then reported their number as the operation count.
/// </remarks>
public sealed class JournalVerbTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-journal-");
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 11, 3, 0, 0, TimeSpan.Zero));

    public JournalVerbTests() => JournalFixtures.Write(_dir.FullName, _clock);

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    private sealed class Recorder : Cli.Output.IOutputSink
    {
        public List<string> Lines { get; } = [];

        public bool Verbose => true;

        public IReadOnlyList<CliDiagnostic> Diagnostics => [];

        public void Diagnostic(CliDiagnostic d) { }

        public void Event(CliEvent e) { }

        public void Line(string text) => Lines.Add(text);

        public int Complete<T>(string verb, int exitCode, T? result) => exitCode;
    }

    /// <summary>The verb's text output, through the real command line and the real handler.</summary>
    private List<string> Text(params string[] extra)
    {
        string[] args = ["journal", "--config-dir", _dir.FullName, .. extra];
        var parse = Cli.Commands.CommandTree.Build().Parse(args);
        parse.Errors.ShouldBeEmpty(string.Join(' ', args));

        var sink = new Recorder();
        Cli.Commands.JournalCommand.Run(
            new Cli.Commands.CommandContext(sink, parse),
            since: null,
            job: null,
            configDir: _dir.FullName,
            all: extra.Contains("--all"));

        return sink.Lines;
    }

    /// <summary>The verb's envelope, written to a file exactly as a machine caller reads it.</summary>
    private JsonElement Envelope(params string[] extra)
    {
        var file = Path.Combine(_dir.FullName, $"out-{Guid.NewGuid():N}.ndjson");
        string[] args =
            ["journal", "--config-dir", _dir.FullName, "--json-stream", "--output", file,
             "--no-event-log", .. extra];

        var parse = Cli.Commands.CommandTree.Build().Parse(args);
        parse.Errors.ShouldBeEmpty(string.Join(' ', args));
        parse.Invoke();

        return File.ReadAllLines(file)
            .Where(l => l.Length > 0)
            .Select(l => JsonDocument.Parse(l).RootElement)
            .Last(e => e.TryGetProperty("schema", out _));
    }

    /// <summary>
    /// One line per operation, not one per record.
    /// </summary>
    /// <remarks>
    /// The fixture is one delete that happened, one file left alone, and one delete from an
    /// earlier run that was never applied - four records, three operations.
    /// </remarks>
    [Fact]
    public void OneLinePerOperation()
    {
        Text().Count.ShouldBe(3);
        Text("--all").Count.ShouldBe(4, "the record itself is unchanged");
    }

    /// <summary>
    /// The count reports what the verb reported.
    /// </summary>
    /// <remarks>
    /// It was <c>entries.Length</c> over the records, so an operator scripting against
    /// <c>result.count</c> got double - and could not tell, because the entries beside it were
    /// doubled in exactly the same way.
    /// </remarks>
    [Fact]
    public void TheCountIsOperationsNotRecords()
    {
        var result = Envelope().GetProperty("result");

        result.GetProperty("count").GetInt32().ShouldBe(3);
        result.GetProperty("entries").GetArrayLength().ShouldBe(3, "and agrees with what it returned");
    }

    /// <summary>
    /// Every line is the product's own sentence about the event.
    /// </summary>
    /// <remarks>
    /// This verb hand-rolled a format string for data <c>CliEventText</c> already renders for the
    /// console and for the GUI - a third spelling of one truth, which is the defect the previous
    /// milestone existed to remove. It also pins the two-space indent that serves as the gap
    /// after the timestamp, so that dependence is stated rather than assumed.
    /// </remarks>
    [Fact]
    public void TheLineIsTheProductsOneSentence()
    {
        var entries = Envelope().GetProperty("result").GetProperty("entries");
        var lines = Text();

        lines.Count.ShouldBe(entries.GetArrayLength());

        foreach (var (element, line) in entries.EnumerateArray().Zip(lines))
        {
            var e = JsonSerializer.Deserialize(element.GetRawText(), CliEventJson.Default.CliEvent)
                .ShouldNotBeNull();

            line.ShouldBe($"{e.Ts}{CliEventText.Describe(e)}");
        }
    }

    /// <summary>
    /// A run that died mid-operation is reported without having to ask.
    /// </summary>
    /// <remarks>
    /// The reason the collapse is a fold and not a filter. A plan half with no apply half after
    /// it is the evidence of a process killed holding a file open, and the predicate the live
    /// stream uses answers "not the last word" for exactly that record - so filtering by it would
    /// hide the one line this verb exists to surface.
    /// </remarks>
    [Fact]
    public void AnAbandonedOperationIsReportedWithoutAskingForIt()
    {
        Text().ShouldContain(l => l.Contains(@"C:\logs\app.log.9", StringComparison.Ordinal));
    }

    /// <summary>
    /// A file that was considered and left alone says so.
    /// </summary>
    /// <remarks>
    /// A skip is journaled as <c>Op.Plan</c> - the one action <c>PlanExecutor</c> does not map to
    /// a named operation - so the old format string printed the bare word "plan" in the column
    /// where a person looks for what was done.
    /// </remarks>
    [Fact]
    public void ASkippedFileIsNotReportedAsTheWordPlan()
    {
        var line = Text()
            .Where(l => l.Contains(@"C:\logs\app.log  ", StringComparison.Ordinal))
            .ShouldHaveSingleItem();

        line.ShouldContain("skip ");
        line.ShouldNotContain(" plan ");
    }

    /// <summary>
    /// A completed operation is reported by its outcome, not its intention.
    /// </summary>
    /// <remarks>
    /// The fixture's delete is refused by the guard at apply time, so the last word about it is a
    /// failure. Reporting the plan half instead would say a deletion was going to happen and stop
    /// there, which is the half of the record that is least true by the time anyone reads it.
    /// </remarks>
    [Fact]
    public void ACompletedOperationIsReportedByWhatBecameOfIt()
    {
        var line = Text()
            .Where(l => l.Contains(@"C:\logs\app.log.4", StringComparison.Ordinal))
            .ShouldHaveSingleItem();

        line.ShouldContain("failed to delete");
    }

    /// <summary>Nothing is lost: --all returns the record exactly as it was written.</summary>
    [Fact]
    public void AllShowsBothHalves()
    {
        var entries = Envelope("--all").GetProperty("result").GetProperty("entries");

        entries.GetArrayLength().ShouldBe(4);

        entries.EnumerateArray()
            .Count(e => e.GetProperty("src").GetString() == @"C:\logs\app.log.4")
            .ShouldBe(2, "both halves of the operation that was carried out");
    }
}
