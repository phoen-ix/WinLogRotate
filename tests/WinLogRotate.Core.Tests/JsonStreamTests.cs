using System.Text.Json;
using Shouldly;
using WinLogRotate.Core;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// What <c>--json-stream --output</c> actually puts in the file.
/// </summary>
/// <remarks>
/// <para>
/// Every test here builds its context with <c>CommandContext.From</c> and a real parsed command
/// line, never a hand-made sink. <c>From</c> is where the <c>--output</c> wiring lives, so a
/// test that constructed a recording sink directly would pass while the product stayed broken -
/// which is precisely the failure mode that let this ship. The absence of a test at this level
/// is the defect, not a symptom of it.
/// </para>
/// <para>
/// The GUI is the only caller of these two flags, and it runs the CLI elevated: a "runas" child
/// has no console its parent can read, so anything written to stdout is destroyed rather than
/// merely misplaced. The file is the whole channel.
/// </para>
/// </remarks>
public sealed class JsonStreamTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-stream-");

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    private string Config(string job)
    {
        File.WriteAllText(Path.Combine(_dir.FullName, "config.toml"), "schema = 1\n");
        var confd = Directory.CreateDirectory(Path.Combine(_dir.FullName, "conf.d"));
        File.WriteAllText(Path.Combine(confd.FullName, "a.toml"), job);
        return _dir.FullName;
    }

    private static string Job(string name, string paths) => $"""
        schema = 1
        [job]
        name      = "{name}"
        kind      = "manage"
        paths     = [{paths}]
        missingok = true
        """;

    /// <summary>Runs the verb exactly as the GUI's elevated path does, and returns the file.</summary>
    private string[] Stream(params string[] verb)
    {
        var file = Path.Combine(_dir.FullName, $"events-{Guid.NewGuid():N}.ndjson");

        string[] args = [.. verb, "--json-stream", "--output", file, "--no-event-log"];
        var parse = Cli.Commands.CommandTree.Build().Parse(args);
        parse.Errors.ShouldBeEmpty($"the GUI's own command line must parse: {string.Join(' ', args)}");

        parse.Invoke();

        return File.Exists(file) ? File.ReadAllLines(file) : [];
    }

    /// <summary>
    /// A run writes its result to the file it was told to write to.
    /// </summary>
    /// <remarks>
    /// It wrote the envelope to Console.Out while --output held a different writer, so the file
    /// the GUI tails stayed zero bytes and the result was lost with the child's hidden console.
    /// Reproduced against the built binary before this was written: 0 bytes in, the whole
    /// envelope on a stdout nobody could read.
    /// </remarks>
    [Fact]
    public void ARunWritesItsResultToTheOutputFile()
    {
        var lines = Stream("run", "--no-notify", "--config-dir", Config(Job("app", "\"C:/app/logs/*.log\"")));

        lines.ShouldNotBeEmpty("the file the GUI tails must not be empty");

        var envelope = lines
            .Select(l => JsonDocument.Parse(l).RootElement)
            .LastOrDefault(e => e.TryGetProperty("schema", out _));

        envelope.ValueKind.ShouldBe(JsonValueKind.Object, "the envelope must reach the file");
        envelope.GetProperty("verb").GetString().ShouldBe("run");
        envelope.GetProperty("exitCode").GetInt32().ShouldBe(ExitCode.Ok);
    }

    /// <summary>Every line in the file is a complete JSON document.</summary>
    /// <remarks>
    /// The GUI tails this file while it is being written, so a line that is not self-contained
    /// is one an operator sees torn in half.
    /// </remarks>
    [Fact]
    public void EveryLineIsParseable()
    {
        var lines = Stream("run", "--no-notify", "--config-dir", Config(Job("app", "\"C:/app/logs/*.log\"")));

        lines.ShouldNotBeEmpty();

        foreach (var line in lines.Where(l => l.Length > 0))
        {
            Should.NotThrow(() => JsonDocument.Parse(line), $"unparseable line: {line}");
        }
    }

    /// <summary>
    /// The verbs the GUI runs elevated all put something in the file.
    /// </summary>
    /// <remarks>
    /// host use and host repair emit no events and never will - they are not rotations - so the
    /// envelope is the only thing they can say. Two of the GUI's four elevated buttons pass no
    /// line callback at all and can never be helped by an event stream; this is what they read.
    /// </remarks>
    [Theory]
    [InlineData("host", "repair")]
    [InlineData("host", "status")]
    public void AnElevatedVerbWritesSomething(string verb, string sub)
    {
        var config = Config(Job("app", "\"C:/app/logs/*.log\""));

        Stream(verb, sub, "--config-dir", config)
            .ShouldNotBeEmpty($"'{verb} {sub}' told the GUI nothing at all");
    }

    /// <summary>Without --output the envelope still goes to stdout, as every script expects.</summary>
    /// <remarks>
    /// Where --output is absent the stream writer IS Console.Out, so routing the envelope
    /// through it is a no-op - which is what makes this change safe for the eleven --json
    /// callers in CI. Asserted rather than assumed.
    /// </remarks>
    [Fact]
    public void WithoutAnOutputFileTheEnvelopeStillGoesToStdout()
    {
        var config = Config(Job("app", "\"C:/app/logs/*.log\""));
        var parse = Cli.Commands.CommandTree.Build()
            .Parse(["doctor", "--json", "--no-event-log", "--config-dir", config]);

        var before = Console.Out;
        var captured = new StringWriter();

        try
        {
            Console.SetOut(captured);
            parse.Invoke();
        }
        finally
        {
            Console.SetOut(before);
        }

        var text = captured.ToString();
        text.ShouldNotBeEmpty();
        JsonDocument.Parse(text.Trim()).RootElement.GetProperty("verb").GetString().ShouldBe("doctor");
    }
}
