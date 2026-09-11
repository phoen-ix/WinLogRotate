using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using WinLogRotate.Cli.Output;
using WinLogRotate.Contracts;
using WinLogRotate.Gui.Cli;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// What the GUI's History page makes of a journal.
/// </summary>
/// <remarks>
/// Every operation twice, until this milestone: the page added a row per record, so the plan half
/// and the apply half of each operation both appeared, differing only in the timestamp, and
/// "{n} operation(s)." underneath said the doubled number. A file deliberately left alone showed
/// the word "plan" in the column headed What.
/// </remarks>
public sealed class JournalHistoryTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-history-");
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 11, 3, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// The real verb's real envelope, over a journal the product wrote.
    /// </summary>
    /// <remarks>
    /// End to end on purpose: a hand-written envelope would assert only the shape its author
    /// believed in, and the shape is half of what is under test. This is byte-for-byte what the
    /// page receives, because the page runs the same verb.
    /// </remarks>
    private string Envelope(params string[] extra)
    {
        JournalFixtures.Write(_dir.FullName, _clock);

        var file = Path.Combine(_dir.FullName, $"out-{Guid.NewGuid():N}.ndjson");
        var parse = Cli.Commands.CommandTree.Build().Parse(
            ["journal", "--config-dir", _dir.FullName,
             "--json-stream", "--output", file, "--no-event-log", .. extra]);

        parse.Errors.ShouldBeEmpty();
        parse.Invoke();

        return File.ReadAllLines(file).Last(l => l.Contains("\"schema\"", StringComparison.Ordinal));
    }

    /// <summary>One envelope carrying one event, through the CLI's own serializer.</summary>
    private static string EnvelopeCarrying(CliEvent entry) =>
        JsonSerializer.Serialize(
            new CliEnvelope<JournalResult>
            {
                Schema = 1,
                Product = "winlogrotate",
                Version = "0.0.0",
                Verb = "journal",
                Ok = true,
                ExitCode = 0,
                Result = new JournalResult
                {
                    Directory = @"C:\ProgramData\WinLogRotate\journal",
                    Count = 1,
                    SkippedLines = 0,
                    Entries = [entry],
                },
                Diagnostics = [],
            },
            typeof(CliEnvelope<JournalResult>),
            Cli.CliJsonContext.Default);

    /// <summary>
    /// The page has an opinion about every journal operation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It shows file operations and filters bookkeeping. Milestone 17 gave emitters to
    /// guard.refuse, guard.override and nulfill - decisions rather than operations - and the
    /// filter did not know about them, so verdicts became rows in a grid whose own doc comment
    /// says it shows "what was actually compressed, moved or deleted".
    /// </para>
    /// <para>
    /// This was a text scan of HistoryPage.cs, because the GUI cannot be referenced by a test
    /// that runs on the Linux leg. The filter now lives in WinLogRotate.Gui.Model, which this
    /// project already references, so the scan's reason is gone and the rule can be asserted by
    /// running it. The expected set is still read from <c>Op</c> by reflection, so adding an
    /// operation still forces a decision here rather than silently landing in the grid.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheHistoryPageHasAnOpinionAboutEveryJournalOperation()
    {
        // What a history of file operations is for. A hook is shown alongside them: it ran as
        // part of a rotation, and "did the postrotate script fire?" is a question people come to
        // a history for.
        string[] shown =
        [
            Op.Compress, Op.Delete, Op.Rename, Op.CopyTruncate,
            Op.Copy, Op.Create, Op.CreateDir, Op.Plan, Op.Hook,
        ];

        var all = typeof(Op)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();

        all.Length.ShouldBeGreaterThan(10);

        foreach (var operation in all)
        {
            var view = JournalHistory.From(EnvelopeCarrying(new CliEvent
            {
                Ts = "2026-09-11T03:00:00.0000000+00:00",
                Run = "01RUN0000000000000000000000",
                Operation = operation,
                Phase = Phase.Apply,
                Result = OpResult.Ok,
                Job = "app",
                Src = @"C:\logs\app.log",
            }));

            view.Rows.Count.ShouldBe(
                shown.Contains(operation, StringComparer.Ordinal) ? 1 : 0,
                $"the History page has no opinion about '{operation}'");
        }
    }

    /// <summary>
    /// One row per operation, and the count agrees.
    /// </summary>
    /// <remarks>
    /// The fixture is one delete that was carried out, one file left alone, and one delete from
    /// an earlier run that was never applied - four records, three operations. The page said
    /// four.
    /// </remarks>
    [Fact]
    public void OneRowPerOperation()
    {
        var view = JournalHistory.From(Envelope());

        view.Rows.Count.ShouldBe(3);
        view.Unreadable.ShouldBeFalse();
    }

    /// <summary>
    /// A CLI that sends both halves is still counted once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reason the page collapses at all, now that the verb does it too. The GUI ships
    /// separately and is routinely paired with whatever winlogrotate.exe is installed - a partial
    /// upgrade, or a GUI run against an older per-machine install - and an older CLI returns the
    /// record as written. Doing it on both sides costs one pass, because the fold is idempotent.
    /// </para>
    /// <para>
    /// Asserted through --all, which is exactly the older CLI's output. An earlier version of
    /// this test used the default envelope, where the verb had already collapsed, so deleting the
    /// page's own collapse changed nothing and the test could not fail.
    /// </para>
    /// </remarks>
    [Fact]
    public void ACliThatSendsBothHalvesIsStillCountedOnce()
    {
        var raw = Envelope("--all");

        JsonDocument.Parse(raw).RootElement
            .GetProperty("result").GetProperty("entries").GetArrayLength()
            .ShouldBe(4, "the fixture must hand over the record as written");

        JournalHistory.From(raw).Rows.Count.ShouldBe(3);
    }

    /// <summary>
    /// A file that was considered and left alone says so.
    /// </summary>
    /// <remarks>
    /// A skip is journaled as <c>Op.Plan</c>, and the page put the operation name straight into
    /// the What column - so the answer to "what happened to this file" was the word "plan".
    /// </remarks>
    [Fact]
    public void ASkippedFileDoesNotSayPlan()
    {
        var row = JournalHistory.From(Envelope())
            .Rows.Where(r => r.File == @"C:\logs\app.log")
            .ShouldHaveSingleItem();

        row.What.ShouldBe("skip");
        row.Why.ShouldBe("newest file - the application is still writing it");
    }

    /// <summary>
    /// An operation is described by what became of it.
    /// </summary>
    /// <remarks>
    /// The same words the console uses, from the same function, so a screenshot of the grid and a
    /// paste of the terminal cannot disagree about one record.
    /// </remarks>
    [Fact]
    public void AnOperationIsDescribedByWhatBecameOfIt()
    {
        var rows = JournalHistory.From(Envelope()).Rows;

        rows.Where(r => r.File == @"C:\logs\app.log.4").ShouldHaveSingleItem()
            .What.ShouldBe("failed to delete");

        // Never applied, so the plan half is all there is - and saying "would delete" is exactly
        // right for a run that intended it and died.
        rows.Where(r => r.File == @"C:\logs\app.log.9").ShouldHaveSingleItem()
            .What.ShouldBe("would delete");
    }

    /// <summary>
    /// A failed operation carries its reason into the grid.
    /// </summary>
    /// <remarks>
    /// The page read only <c>reason</c>, which on a failure is the retention rule that condemned
    /// the file rather than the thing that went wrong - so a row could say an operation failed
    /// and leave the reader to go and find the journal for the sentence the GUI already had.
    /// </remarks>
    [Fact]
    public void AFailureSaysWhatWentWrong()
    {
        JournalHistory.From(Envelope())
            .Rows.Where(r => r.File == @"C:\logs\app.log.4").ShouldHaveSingleItem()
            .Why.ShouldContain(@"C:\Windows");
    }

    /// <summary>
    /// A response that makes no sense is reported, never thrown.
    /// </summary>
    /// <remarks>
    /// <c>GetProperty</c> throws <c>KeyNotFoundException</c> and <c>GetInt32</c> throws
    /// <c>InvalidOperationException</c>, and the page caught only <c>JsonException</c> - inside
    /// an <c>async void</c> handler, in a process that installs no unhandled-exception handler.
    /// An envelope missing a property took the window down.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("""{"result":{}}""")]
    [InlineData("""{"result":{"entries":{}}}""")]
    [InlineData("""{"result":{"entries":[],"skippedLines":"twelve"}}""")]
    [InlineData("""{"result":{"entries":[{"nonsense":1}],"skippedLines":0}}""")]
    public void AMalformedEnvelopeIsReportedNotThrown(string json)
    {
        var view = Should.NotThrow(() => JournalHistory.From(json));

        view.Rows.ShouldBeEmpty();
    }

    /// <summary>Torn lines the CLI counted are carried through rather than dropped.</summary>
    /// <remarks>
    /// "A previous run was probably terminated" is the one clue an operator gets that the record
    /// they are reading is incomplete.
    /// </remarks>
    [Fact]
    public void TheCountOfUnreadableLinesSurvives()
    {
        JournalHistory.From("""{"result":{"entries":[],"skippedLines":12}}""")
            .SkippedLines.ShouldBe(12);
    }
}
