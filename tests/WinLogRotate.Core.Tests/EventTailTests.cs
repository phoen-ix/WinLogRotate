using System.Reflection;
using System.Text;
using System.Text.Json;
using Shouldly;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Gui.Cli;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// Following a file that is still being written.
/// </summary>
/// <remarks>
/// <para>
/// The GUI reads an elevated child's output by tailing a file, because a "runas" child cannot
/// have its pipes redirected. Thirty lines did that, and three of the behaviours below were
/// wrong in them - none of it reachable by a test, because a project referencing the GUI
/// inherits <c>Microsoft.WindowsDesktop.App</c> and does not run on the Linux leg at all. The
/// code moved to a plain library for exactly this file's sake.
/// </para>
/// <para>
/// Bytes are written here, not lines, and the reads are interleaved with the writes. A test that
/// wrote whole lines and then read them would pass against every version of this code, including
/// the one that shipped.
/// </para>
/// </remarks>
public sealed class EventTailTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-tail-");

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    private string _path = string.Empty;

    private string Path_ => _path.Length > 0
        ? _path
        : _path = System.IO.Path.Combine(_dir.FullName, "events.ndjson");

    private void Append(string text) => Append(Encoding.UTF8.GetBytes(text));

    private void Append(byte[] bytes)
    {
        using var stream = new FileStream(
            Path_, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        stream.Write(bytes);
    }

    /// <summary>
    /// A line exactly as the CLI would write it.
    /// </summary>
    /// <remarks>
    /// Through the product's own serializer, never hand-written JSON. The first attempt at this
    /// fixture spelled the line by hand and put <c>C:\logs\app.log</c> in it unescaped, which is
    /// not JSON at all - so every assertion about what the tail renders would have been an
    /// assertion about what it does with a line the CLI cannot produce.
    /// </remarks>
    private static string Event(string src) => JsonSerializer.Serialize(
        new CliEvent
        {
            Ts = "2026-09-11T03:00:00.0000000+00:00",
            Run = "R",
            Operation = Op.Delete,
            Phase = Phase.Apply,
            Result = OpResult.Ok,
            Job = "app",
            Src = src,
            Reason = "rotate 2",
        },
        CliEventJson.Default.CliEvent);

    /// <summary>
    /// A line that is only half written is not reported as a whole one.
    /// </summary>
    /// <remarks>
    /// <c>ReadLine</c> treats end-of-file as a terminator, so a poll landing mid-write handed the
    /// operator <c>{"ts":"2026-09</c> and committed past it; the remainder arrived on the next
    /// poll as a second line that parses as nothing. At a 100 ms poll over a busy rotation this
    /// is not an edge case, it is the common case.
    /// </remarks>
    [Fact]
    public void APartlyWrittenLineIsNotReported()
    {
        var whole = Event(@"C:\logs\app.log.3");
        Append(whole[..40]);

        var seen = new List<string>();
        var offset = EventTail.ReadFrom(Path_, 0, seen.Add);

        seen.ShouldBeEmpty("half a line is not a line");
        offset.ShouldBe(0, "and the half must still be there to read next time");

        Append(whole[40..] + "\n");
        EventTail.ReadFrom(Path_, offset, seen.Add);

        seen.ShouldHaveSingleItem().ShouldBe(whole);
    }

    /// <summary>
    /// A UTF-8 sequence split by a poll survives it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A fresh <c>StreamReader</c> per poll resets the decoder, so a sequence straddling the
    /// boundary decoded to U+FFFD - and the offset advanced past it, making the corruption
    /// permanent rather than momentary.
    /// </para>
    /// <para>
    /// Asserted against the reader with a plain line, not a serialized event, because
    /// <c>System.Text.Json</c>'s default encoder escapes non-ASCII: the CLI writes
    /// <c>gr\u00F6\u00DFe.log</c>, so no line it produces today contains a multi-byte sequence
    /// at all. That is an encoder's default and not a property of the file, which is declared
    /// UTF-8 - so this guards the reader, and is honest about not reproducing a field failure.
    /// </para>
    /// </remarks>
    [Fact]
    public void AMultiByteCharacterSplitAcrossPollsIsNotCorrupted()
    {
        var whole = @"C:\Protokolle\größe.log";
        var bytes = Encoding.UTF8.GetBytes(whole + "\n");

        // Mid-sequence: between the two bytes that encode U+00F6.
        var split = Array.IndexOf(bytes, (byte)0xC3) + 1;
        split.ShouldBeGreaterThan(0, "the fixture must actually contain a multi-byte character");

        Append(bytes[..split]);

        var seen = new List<string>();
        var offset = EventTail.ReadFrom(Path_, 0, seen.Add);

        Append(bytes[split..]);
        EventTail.ReadFrom(Path_, offset, seen.Add);

        seen.ShouldHaveSingleItem().ShouldBe(whole);
        seen[0].ShouldNotContain("\uFFFD");
    }

    /// <summary>
    /// Nothing is reported twice when a read fails part way.
    /// </summary>
    /// <remarks>
    /// Lines were handed to the caller as they were read and the offset returned unchanged on
    /// an IOException, so a failure after the tenth line re-delivered all ten. The pane would
    /// show a rotation doing the same work over and over, which is the single most alarming
    /// thing this product could tell an operator it had done.
    /// </remarks>
    [Fact]
    public void AFailedReadRepeatsNothing()
    {
        Append(Event(@"C:\logs\a.log") + "\n" + Event(@"C:\logs\b.log") + "\n");

        var seen = new List<string>();
        var offset = EventTail.ReadFrom(Path_, 0, seen.Add);
        seen.Count.ShouldBe(2);

        // The file is gone - deleted by the sweep, or by the child's own cleanup.
        File.Delete(Path_);

        EventTail.ReadFrom(Path_, offset, seen.Add).ShouldBe(offset);
        seen.Count.ShouldBe(2, "a read that could not happen delivered nothing");
    }

    /// <summary>Reading a file that does not exist yet is not an error.</summary>
    /// <remarks>The GUI starts tailing before the elevated child has been through the UAC
    /// prompt, so this is the normal first few polls, not a failure.</remarks>
    [Fact]
    public void AFileThatDoesNotExistYetReadsAsNothing()
    {
        var seen = new List<string>();
        EventTail.ReadFrom(Path_, 0, seen.Add).ShouldBe(0);
        seen.ShouldBeEmpty();
    }

    /// <summary>
    /// What the caller is handed is a sentence, not the JSON it came from.
    /// </summary>
    /// <remarks>
    /// The whole visible point of the milestone: the pane showed raw NDJSON because the tail had
    /// no way to read what it was carrying.
    /// </remarks>
    [Fact]
    public async Task TheCallerIsHandedSentences()
    {
        Append(Event(@"C:\logs\app.log.3") + "\n");

        var seen = new List<string>();
        var exited = false;

        var following = EventTail.FollowAsync(
            Path_, seen.Add, () => exited, TestContext.Current.CancellationToken,
            TimeSpan.FromMilliseconds(5));

        Append(Event(@"C:\logs\app.log.4") + "\n");
        await Task.Delay(60, TestContext.Current.CancellationToken);
        exited = true;
        await following;

        seen.Count.ShouldBe(2);
        seen[0].ShouldBe(@"  [app] did delete C:\logs\app.log.3  (rotate 2)");
        seen[1].ShouldBe(@"  [app] did delete C:\logs\app.log.4  (rotate 2)");
    }

    /// <summary>The envelope shares the file and is not a line of progress.</summary>
    /// <remarks>
    /// It reaches the file because the result had to reach the GUI somehow, and an elevated
    /// child's stdout is destroyed with its hidden console. It is read from the file afterwards,
    /// not from the pane.
    /// </remarks>
    [Fact]
    public void TheEnvelopeIsNotReportedAsProgress()
    {
        Append("""{"schema":1,"product":"WinLogRotate","verb":"run","ok":true,"exitCode":0}""" + "\n");

        var seen = new List<string>();
        var offset = EventTail.ReadFrom(Path_, 0, seen.Add);

        // Consumed - the tail must not stall on it - but not shown.
        offset.ShouldBeGreaterThan(0);

        var shown = seen.Select(CliEventText.Describe).Where(s => s is not null).ToList();
        shown.ShouldBeEmpty();
    }

    // ---- the wording ----------------------------------------------------------------------

    /// <summary>
    /// Every exit code this product can return has a sentence.
    /// </summary>
    /// <remarks>
    /// Nine places show this text and nothing asserted any of it. The fallback arm - "Exited with
    /// code 4" - is not a bug in itself; it is what an operator sees when somebody adds an exit
    /// code and forgets this method, which is the failure being prevented.
    /// </remarks>
    [Fact]
    public void EveryExitCodeIsWorded()
    {
        var codes = typeof(ExitCode)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(int))
            .Select(f => (f.Name, Value: (int)f.GetRawConstantValue()!))
            .ToList();

        codes.Count.ShouldBeGreaterThan(3, "the reflection must actually find the constants");

        foreach (var (name, value) in codes)
        {
            var described = new CliResult { ExitCode = value, StdOut = "", StdErr = "" }.Describe();

            described.ShouldNotContain(
                value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Case.Sensitive,
                $"ExitCode.{name} falls through to the bare-number arm");
        }
    }

    /// <summary>And so does every way an invocation can fail before it produces one.</summary>
    [Theory]
    [InlineData(CliFailure.NotFound)]
    [InlineData(CliFailure.UacDeclined)]
    [InlineData(CliFailure.Timeout)]
    public void EveryFailureIsWorded(CliFailure failure)
    {
        var described = new CliResult
        {
            ExitCode = -1,
            StdOut = "",
            StdErr = "",
            Failure = failure,
        }.Describe();

        described.ShouldNotContain("-1", Case.Sensitive, $"{failure} has no wording of its own");
        described.ShouldEndWith(".");
    }
}
