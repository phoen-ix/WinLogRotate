using System.Text;
using System.Text.Json;
using Shouldly;
using WinLogRotate.Cli.Output;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Notify;
using WinLogRotate.Core.State;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// The shape of every envelope this product publishes, pinned to a checked-in snapshot.
/// </summary>
/// <remarks>
/// <para>
/// <c>ConfigShowResult.Jobs</c> is a list of <c>EffectiveJob</c>: the domain record is the
/// public contract. So milestone 16 added a computed <c>GuardScope</c> property for the guard's
/// convenience and silently published it, emitting the same two values a second time under an
/// internal type name. Nothing noticed for two milestones, because nothing asserted what the
/// envelope looked like.
/// </para>
/// <para>
/// Field names and value kinds only, never values. <c>Directory.Build.props</c> sets Version to
/// 0.0.0-dev locally while CI stamps a real one, so a value-level snapshot would be green in one
/// place and red in the other - and a test that has to be true in only one environment is worse
/// than none.
/// </para>
/// <para>
/// A failure here is not necessarily a bug. It means the published contract changed, and the fix
/// is either to undo that or to accept it by updating the snapshot deliberately - which is the
/// whole point, because the alternative is what happened to guardScope.
/// </para>
/// </remarks>
public sealed class EnvelopeShapeTests
{
    /// <summary>Every leaf path in the document, with the JSON kind a consumer would find there.</summary>
    /// <remarks>
    /// An array is described by its first element, prefixed <c>[]</c>: the contract is what one
    /// element looks like, and recording every element would make the snapshot depend on how many
    /// the fixture happened to build.
    /// </remarks>
    private static string Shape(JsonElement element, string path = "")
    {
        var lines = new List<string>();

        void Walk(JsonElement e, string at)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var p in e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                    {
                        Walk(p.Value, at.Length == 0 ? p.Name : $"{at}.{p.Name}");
                    }

                    break;

                case JsonValueKind.Array:
                    var first = e.EnumerateArray().FirstOrDefault();
                    if (first.ValueKind == JsonValueKind.Undefined)
                    {
                        lines.Add($"{at}[] : (empty)");
                        break;
                    }

                    Walk(first, $"{at}[]");
                    break;

                default:
                    // True and False are distinct JsonValueKinds, so recording them verbatim
                    // would make this snapshot sensitive to a fixture's values and not only its
                    // shape - a default flipping from true to false would read as a contract
                    // change. Null is collapsed for the same reason: WhenWritingNull means a
                    // nullable field is absent rather than null, so seeing one at all is the
                    // fixture's doing.
                    lines.Add($"{at} : {e.ValueKind switch
                    {
                        JsonValueKind.True or JsonValueKind.False => "Boolean",
                        _ => e.ValueKind.ToString(),
                    }}");
                    break;
            }
        }

        Walk(element, path);
        return string.Join("\n", lines) + "\n";
    }

    private static void ShouldMatchSnapshot<T>(CliEnvelope<T> envelope, string name)
    {
        var json = JsonSerializer.Serialize(
            envelope, typeof(CliEnvelope<T>), Cli.CliJsonContext.Default);

        var actual = Shape(JsonDocument.Parse(json).RootElement);

        var file = Path.Combine(
            RepoRoot.Find().FullName, "tests", "WinLogRotate.Core.Tests", "envelope", $"{name}.txt");

        // Written on first run so a new envelope is one step, not two - but never overwritten,
        // because silently rewriting the thing being asserted is how a snapshot test becomes a
        // test of nothing.
        if (!File.Exists(file))
        {
            File.WriteAllText(file, actual);
            Assert.Fail($"Wrote a new snapshot for {name}. Review it and run again.");
        }

        actual.Replace("\r\n", "\n", StringComparison.Ordinal)
            .ShouldBe(
                File.ReadAllText(file).Replace("\r\n", "\n", StringComparison.Ordinal),
                $"the published shape of {name} changed - undo it, or accept it by updating "
                + $"tests/WinLogRotate.Core.Tests/envelope/{name}.txt");
    }

    private static CliEnvelope<T> Envelope<T>(string verb, T result) => new()
    {
        Schema = 1,
        Product = "winlogrotate",
        Version = "0.0.0",
        Verb = verb,
        Ok = true,
        ExitCode = 0,
        Result = result,
        Diagnostics =
        [
            new CliDiagnostic
            {
                Severity = Severity.Warning,
                Code = DiagnosticCode.JobSkipped,
                Message = "a message",
                Path = @"C:\logs\app.log",
                Line = 3,
                Column = 1,
                Job = "app",
                Remedy = "a remedy",
                NativeError = 32,
            },
        ],
    };

    [Fact]
    public void ConfigShowKeepsItsShape() =>
        ShouldMatchSnapshot(
            Envelope("config show", new ConfigShowResult
            {
                Root = @"C:\ProgramData\WinLogRotate",
                Jobs = [Fixtures.Job()],
            }),
            "config-show");

    [Fact]
    public void ConfigCheckKeepsItsShape() =>
        ShouldMatchSnapshot(
            Envelope("config check", new ConfigCheckResult
            {
                Root = @"C:\ProgramData\WinLogRotate",
                Jobs = 2,
                Errors = 1,
                Warnings = 1,
                Diagnostics =
                [
                    new ConfigDiagnosticDto
                    {
                        Severity = Severity.Error,
                        Code = DiagnosticCode.DangerousPathRefused,
                        Message = "a message",
                        File = @"C:\ProgramData\WinLogRotate\conf.d\a.toml",
                        Line = 4,
                        Column = 1,
                        Remedy = "a remedy",
                    },
                ],
            }),
            "config-check");

    [Fact]
    public void RunKeepsItsShape() =>
        ShouldMatchSnapshot(
            Envelope("run", new RunResult
            {
                RunId = "01JQTEST",
                DryRun = false,
                JobsConsidered = 3,
                JobsRun = 2,
                Completed = 5,
                Failed = 0,
                BytesFreed = 4096,
                Errors = ["an error"],
                SkippedJobs = ["iis-logs"],
            }),
            "run");

    [Fact]
    public void DoctorKeepsItsShape() =>
        ShouldMatchSnapshot(
            Envelope("doctor", new DoctorResult
            {
                Version = "0.0.0",
                Scope = InstallScope.PerMachine,
                Root = @"C:\ProgramData\WinLogRotate",
                ConfigExists = true,
                JobsDirectoryExists = true,
                Elevated = true,
                AclVerdict = Hosting.Security.AclVerdict.Hardened,
                HooksAllowed = true,
                AclFix = "icacls ...",
                RunHost = Hosting.Hosts.RunHostKind.Task,
                RunHostDetail = "daily at 03:00",
                Notify = new NotifyDoctorDto
                {
                    Enabled = true,
                    Targets = 1,
                    StoredCredentials = 1,
                    SuppressedChannels = 0,
                    CertificatePinned = false,
                },
            }),
            "doctor");

    [Fact]
    public void JournalKeepsItsShape() =>
        ShouldMatchSnapshot(
            Envelope("journal", new JournalResult
            {
                Directory = @"C:\ProgramData\WinLogRotate\journal",
                Count = 1,
                Entries =
                [
                    new CliEvent
                    {
                        Ts = "2026-09-11T03:00:00Z",
                        Run = "01JQTEST",
                        Operation = Op.Compress,
                        Phase = Phase.Apply,
                        Result = OpResult.Ok,
                        Job = "app",
                        Src = @"C:\logs\app.log.1",
                        Dst = @"C:\logs\app.log.1.zip",
                        BytesBefore = 4096,
                        BytesAfter = 1024,
                        Strategy = "rename",
                        Reason = "compress = zip",
                        Error = null,
                        Ms = 12,
                    },
                ],
                SkippedLines = 0,
            }),
            "journal");
}
