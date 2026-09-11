using System.Text.Json;
using Shouldly;
using WinLogRotate.Contracts;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.State;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// Every enum the CLI puts on the wire is a name, not an ordinal.
/// </summary>
/// <remarks>
/// <para>
/// <c>config show --json</c> emitted <c>"kind": 1</c> and <c>config check --json</c> emitted
/// <c>"severity": 2</c>. The product had already learned why that is wrong and written the
/// reason down - <c>ProbeVerdict</c>, <c>NulFillVerdict</c> and their siblings carry a string
/// converter because "stored as ordinals, appending a member anywhere but the end silently
/// reinterprets every state file already on disk" - and applied it only to the state file. The
/// wire is the documented scripting surface and the thing the GUI parses, and it never got it.
/// </para>
/// <para>
/// PascalCase, matching the members as written, the state file's existing string enums and
/// docs/diagnostics.md's Type column. <c>PropertyNamingPolicy</c> governs property names and
/// does not reach enum values, which is worth knowing before someone assumes camelCase here.
/// </para>
/// </remarks>
public sealed class EnvelopeEnumTests
{
    private static JsonElement Serialize<T>(CliEnvelope<T> envelope) =>
        JsonDocument.Parse(
            JsonSerializer.Serialize(envelope, typeof(CliEnvelope<T>), Cli.CliJsonContext.Default))
            .RootElement;

    private static CliEnvelope<T> Envelope<T>(T result) => new()
    {
        Schema = 1,
        Product = "winlogrotate",
        Version = "0.0.0",
        Verb = "test",
        Ok = true,
        ExitCode = 0,
        Result = result,
    };

    /// <summary>A diagnostic's severity is a name.</summary>
    /// <remarks>
    /// The one an operator reads most, and the one CI had to hardcode: installer-smoke asserted
    /// <c>severity -ne 3</c> with a comment translating it, because comparing against
    /// 'Critical' would silently never have matched.
    /// </remarks>
    [Theory]
    [InlineData(Severity.Info, "Info")]
    [InlineData(Severity.Warning, "Warning")]
    [InlineData(Severity.Error, "Error")]
    [InlineData(Severity.Critical, "Critical")]
    public void ADiagnosticsSeverityIsAName(Severity severity, string expected)
    {
        var json = Serialize(Envelope(new Cli.Output.EmptyResult()) with
        {
            Diagnostics =
            [
                new CliDiagnostic { Severity = severity, Code = DiagnosticCode.JobSkipped, Message = "x" },
            ],
        });

        json.GetProperty("diagnostics")[0].GetProperty("severity")
            .GetString().ShouldBe(expected);
    }

    /// <summary>
    /// A job's enums are names, and the GUI's own reader works against them.
    /// </summary>
    /// <remarks>
    /// Asserted the way JobsPage reads it - GetString() on the element - because that is the
    /// call that threw. JsonElement.GetString() on a Number raises InvalidOperationException,
    /// which its catch (JsonException) does not cover, on an async void handler, on the first
    /// tab the shell selects. The GUI died on startup on any machine with a configured job.
    /// </remarks>
    [Fact]
    public void AJobsEnumsAreNamesTheGuiCanRead()
    {
        var job = TestJob();

        var json = Serialize(Envelope(new Cli.Output.ConfigShowResult
        {
            Root = @"C:\ProgramData\WinLogRotate",
            Jobs = [job],
        }));

        var first = json.GetProperty("result").GetProperty("jobs")[0];

        first.GetProperty("kind").GetString().ShouldBe("Manage");
        first.GetProperty("schedule").GetString().ShouldBe("Daily");
        first.GetProperty("compressType").GetString().ShouldBe("Zip");
        first.GetProperty("lockStrategy").GetString().ShouldBe("Rename");

        // The comparison JobsPage makes on the value it just read.
        first.GetProperty("kind").GetString()
            .ShouldBe("Manage", StringCompareShould.IgnoreCase);
    }

    /// <summary>
    /// The state file's format does not move.
    /// </summary>
    /// <remarks>
    /// Its enums were already names, through per-type converters on a different serializer
    /// context. Those win over a context-wide setting and are reached by a different context
    /// entirely, so the two never meet - but "the on-disk format is unchanged" is worth an
    /// assertion rather than an argument, because getting it wrong re-baselines every log on
    /// every machine that upgrades.
    /// </remarks>
    [Fact]
    public void TheStateFilesFormatIsUnchanged()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wlr-state-{Guid.NewGuid():N}.json");

        try
        {
            var store = StateStore.Load(path, out _);
            store.Set(@"C:\logs\app.log", new PathState
            {
                Path = @"C:\logs\app.log",
                Probe = ProbeVerdict.CopyTruncate,
                ProbedAs = ProbeIdentity.Elevated,
                NulFill = NulFillVerdict.Confirmed,
            });
            store.Save(TimeProvider.System);

            var written = JsonDocument.Parse(File.ReadAllText(path))
                .RootElement.GetProperty("paths").EnumerateObject().First().Value;

            written.GetProperty("probe").GetString().ShouldBe("CopyTruncate");
            written.GetProperty("probedAs").GetString().ShouldBe("Elevated");
            written.GetProperty("nulFill").GetString().ShouldBe("Confirmed");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static EffectiveJob TestJob() => new()
    {
        Name = "app",
        Kind = JobKind.Manage,
        Paths = [@"C:\logs\app.log"],
        Enabled = true,
        Schedule = Schedule.Daily,
        Weekday = 0,
        MonthDay = 0,
        Rotate = 4,
        Start = 1,
        MaxAge = null,
        MinAge = null,
        MinSize = null,
        MaxSize = null,
        SizeThreshold = 1 << 20,
        Compress = true,
        CompressType = CompressType.Zip,
        DelayCompress = false,
        DateExt = false,
        DateFormat = "-yyyyMMdd",
        MissingOk = true,
        NotIfEmpty = false,
        OldDir = null,
        CreateOldDir = false,
        LockStrategy = LockStrategy.Rename,
        LiveFiles = 1,
        MaxFiles = 1000,
        RetryCount = 5,
        RetryIntervalMs = 100,
        PreRotate = [],
        PostRotate = [],
        HookTimeout = TimeSpan.FromSeconds(60),
        AllowDangerous = [],
    };
}
