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
        var job = Fixtures.Job();

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


    /// <summary>
    /// doctor's verdicts are enum names, including "we did not check".
    /// </summary>
    /// <remarks>
    /// aclVerdict held either an enum name or the sentence "not checked (Windows only)" - one
    /// field on the wire carrying two kinds of value, in the place a security verdict is
    /// reported, and the GUI compared against both. NotApplicable says the same thing in the
    /// same vocabulary as everything else, and is distinct from Unknown, which means the check
    /// ran and could not tell.
    /// </remarks>
    [Fact]
    public void DoctorsVerdictsAreEnumNames()
    {
        var json = Serialize(Envelope(new Cli.Output.DoctorResult
        {
            Version = "0.0.0",
            Scope = InstallScope.PerMachine,
            Root = @"C:\ProgramData\WinLogRotate",
            ConfigExists = true,
            JobsDirectoryExists = true,
            Elevated = false,
            AclVerdict = Hosting.Security.AclVerdict.NotApplicable,
            HooksAllowed = false,
            RunHost = Hosting.Hosts.RunHostKind.Task,
            RunHostDetail = "daily at 03:00",
            Notify = new Cli.Output.NotifyDoctorDto
            {
                Enabled = false,
                Targets = 0,
                StoredCredentials = 0,
                SuppressedChannels = 0,
                CertificatePinned = false,
            },
        }));

        var result = json.GetProperty("result");

        result.GetProperty("scope").GetString().ShouldBe("PerMachine");
        result.GetProperty("runHost").GetString().ShouldBe("Task");
        result.GetProperty("aclVerdict").GetString().ShouldBe("NotApplicable");

        // The sentence it replaced. SettingsPage compared against it by hand, so a partial
        // migration would leave the GUI silently never warning.
        result.GetProperty("aclVerdict").GetString().ShouldNotBeNull()
            .ShouldNotContain("Windows only");
    }

    /// <summary>
    /// A target's scheme stays a lowercase string, deliberately.
    /// </summary>
    /// <remarks>
    /// It is the one field here that is not an enum: a NotifyProviderKind for a named provider,
    /// a HookScheme for a literal target, and "?" for one that would not parse. Retyping it
    /// would have forced two vocabularies and a sentinel into one enum. The neighbouring kind
    /// IS an enum and is a name, so the two now differ in casing - that difference is the
    /// signal, not an oversight.
    /// </remarks>
    [Fact]
    public void ATargetsSchemeIsNotAnEnum()
    {
        var shown = typeof(Cli.Output.NotifyTargetDto).GetProperty("Scheme")!;
        shown.PropertyType.ShouldBe(typeof(string));

        typeof(Cli.Output.NotifyProviderDto).GetProperty("Kind")!
            .PropertyType.ShouldBe(typeof(Core.Notify.NotifyProviderKind));
    }

    /// <summary>
    /// config check reports one severity vocabulary, not two.
    /// </summary>
    /// <remarks>
    /// ConfigDiagnosticDto.Severity was a string built with .ToString().ToLowerInvariant(), so
    /// one document carried "Error" at the top level and "error" one object deeper - the same
    /// concept in two casings, a few bytes apart. The shape test cannot see this: both are
    /// Strings. Only the casing distinguishes them, so the casing is what this asserts.
    /// </remarks>
    [Fact]
    public void ConfigCheckReportsOneSeverityVocabulary()
    {
        var json = Serialize(Envelope(new Cli.Output.ConfigCheckResult
        {
            Root = @"C:\ProgramData\WinLogRotate",
            Jobs = 1,
            Errors = 1,
            Warnings = 0,
            Diagnostics =
            [
                new Cli.Output.ConfigDiagnosticDto
                {
                    Severity = Severity.Error,
                    Code = DiagnosticCode.DangerousPathRefused,
                    Message = "a message",
                    File = "a.toml",
                },
            ],
        }) with
        {
            Diagnostics =
            [
                new CliDiagnostic
                {
                    Severity = Severity.Error,
                    Code = DiagnosticCode.DangerousPathRefused,
                    Message = "a message",
                },
            ],
        });

        var outer = json.GetProperty("diagnostics")[0].GetProperty("severity").GetString();
        var inner = json.GetProperty("result").GetProperty("diagnostics")[0]
            .GetProperty("severity").GetString();

        outer.ShouldBe("Error");
        inner.ShouldBe("Error");
        inner.ShouldBe(outer, "one document must not carry two spellings of one value");
    }
}
