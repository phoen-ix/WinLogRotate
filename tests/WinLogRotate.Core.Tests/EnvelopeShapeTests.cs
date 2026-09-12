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

    private static void ShouldMatchSnapshot<T>(CliEnvelope<T> envelope, string name) =>
        ShouldMatchSnapshot(
            JsonSerializer.Serialize(envelope, typeof(CliEnvelope<T>), Cli.CliJsonContext.Default),
            name);

    private static void ShouldMatchSnapshot(string json, string name)
    {
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

    /// <summary>
    /// The event's shape, which is not an envelope's and is published just as hard.
    /// </summary>
    /// <remarks>
    /// Every line of every journal on every machine is this shape, and the GUI now reads it back.
    /// It went unpinned while the envelopes were pinned because it is not an envelope - and
    /// because there were two serializers for it, symmetrical, so a change to both round-tripped
    /// cleanly through anything that wrote and then read. What it would not survive is a journal
    /// written by one version and read by the next.
    /// </remarks>
    [Fact]
    public void TheEventKeepsItsShape() =>
        ShouldMatchSnapshot(
            JsonSerializer.Serialize(
                new CliEvent
                {
                    Ts = "2026-09-11T03:00:00.0000000+00:00",
                    Run = "01K4RJ5S8Q0000000000000000",
                    Operation = Op.Compress,
                    Phase = Phase.Apply,
                    Result = OpResult.Ok,
                    Job = "app",
                    Src = @"C:\logs\app.log.1",
                    Dst = @"C:\logs\app.log.1.zip",
                    BytesBefore = 4096,
                    BytesAfter = 512,
                    Strategy = "rename",
                    Reason = "compress = zip",
                    Error = "a message",
                    Ms = 12,
                },
                CliEventJson.Default.CliEvent),
            "event");

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

    /// <summary>
    /// The version payload's shape, which the window now depends on at start.
    /// </summary>
    /// <remarks>
    /// Five envelopes had snapshots and this one did not - the one a GUI reads before it will do
    /// anything else, and the one whose whole purpose is to say what wire format the other side
    /// speaks. A version handshake whose own shape nothing guards is not much of a handshake.
    /// </remarks>
    [Fact]
    public void VersionKeepsItsShape() =>
        ShouldMatchSnapshot(
            Envelope("version", new VersionResult
            {
                Product = "WinLogRotate",
                Version = "0.0.0",
                Schema = 1,
                Runtime = ".NET 10.0.0",
                Architecture = "X64",
                Elevated = true,
            }),
            "version");

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
                    EventLog = "writable",
                    EventLogExpected = true,
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

                // Populated, not empty. EnvelopeShapeTests records an empty array as "(empty)"
                // rather than by element type, so an empty fixture would pin a shape that says
                // nothing about what the field carries.
                UnreadableFiles = [@"C:\ProgramData\WinLogRotate\journal\journal-2026-09-10.ndjson.zip"],
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

    // ---- the rest of the published surface ------------------------------------------------
    //
    // Seven verbs were pinned and about twenty were not, and this file's own remarks say why
    // that matters: "Nothing noticed for two milestones, because nothing asserted what the
    // envelope looked like." CI itself parses thirteen of these envelopes.
    //
    // Every fixture populates every nullable and every list, deliberately. Shape() records an
    // empty array as "(empty)" and omits a null under the CLI's WhenWritingNull policy, so a
    // fixture that left them out would pin a shape that says nothing about what the field holds -
    // which is the failure mode this file exists to prevent, at one remove.

    [Fact]
    public void GlobKeepsItsShape() =>
        ShouldMatchSnapshot(
            Envelope("glob", new GlobResult
            {
                Pattern = @"C:\inetpub\logs\LogFiles\W3SVC[0-9]\*.log",
                Anchor = @"C:\inetpub\logs\LogFiles",
                ResolvedAnchor = @"D:\iis\LogFiles",
                Count = 2,
                TotalBytes = 40960,
                Files = [@"C:\inetpub\logs\LogFiles\W3SVC1\u_ex260912.log"],
            }),
            "glob");

    [Fact]
    public void HostKeepsItsShape() =>
        ShouldMatchSnapshot(
            Envelope("host status", new HostResult
            {
                Host = "Task",
                ConfigRoot = @"C:\ProgramData\WinLogRotate",
                Scope = "PerMachine",
            }),
            "host");

    [Fact]
    public void HostPathKeepsItsShape() =>
        ShouldMatchSnapshot(
            Envelope("host path", new PathResult
            {
                Directory = @"C:\Program Files\WinLogRotate",
                Scope = "Machine",
                Action = "added",
            }),
            "host-path");

    [Fact]
    public void HostPauseKeepsItsShape() =>
        ShouldMatchSnapshot(
            Envelope("host pause", new PauseResult
            {
                PausedUntil = "2026-09-12T21:00:00.0000000+00:00",
                Action = "paused",
            }),
            "host-pause");

    [Fact]
    public void HostExportKeepsItsShape() =>
        ShouldMatchSnapshot(
            Envelope("host export-task", new ExportTaskResult { Xml = "<Task />" }),
            "host-export-task");

    [Fact]
    public void ImportKeepsItsShape() =>
        ShouldMatchSnapshot(
            Envelope("import", new ImportResult
            {
                Source = "/etc/logrotate.conf",
                OutputDirectory = @"C:\ProgramData\WinLogRotate\conf.d",
                Jobs = 3,
                NeedingReview = 1,
                Files = [@"C:\ProgramData\WinLogRotate\conf.d\nginx.toml"],
            }),
            "import");

    [Fact]
    public void ScanKeepsItsShape() =>
        ShouldMatchSnapshot(
            Envelope("scan", new ScanResult
            {
                Findings =
                [
                    new ScanFinding
                    {
                        Producer = "IIS",
                        Directory = @"C:\inetpub\logs\LogFiles\W3SVC1",
                        Pattern = "u_ex*.log",
                        SelfRotates = true,
                        SelfDeletes = false,
                        SuggestedKind = "manage",
                        Note = "IIS rolls these daily and never removes them.",
                        FileCount = 90,
                        TotalBytes = 1073741824,
                    },
                ],
            }),
            "scan");

    [Fact]
    public void ProbeKeepsItsShape() =>
        ShouldMatchSnapshot(
            Envelope("probe", new ProbeResultDto
            {
                Path = @"C:\logs\app.log",
                Verdict = "Locked",
                Explanation = "The file is open by another process with no sharing.",
                Suggested = "copytruncate",
                BlockingError = 32,
                BlockingErrorText = "The process cannot access the file because it is being used by another process.",
                Unlocked = false,
            }),
            "probe");

    [Fact]
    public void UpdateKeepsItsShape() =>
        ShouldMatchSnapshot(
            Envelope("update check", new UpdateResult
            {
                Current = "0.12.1",
                Latest = "0.13.0",
                UpdateAvailable = true,
                Detail = "https://example.invalid/releases/latest",
            }),
            "update");

    [Fact]
    public void SecretKeepsItsShape() =>
        ShouldMatchSnapshot(
            Envelope("secret set", new SecretResult
            {
                Verb = "set-secret",
                Path = @"C:\ProgramData\WinLogRotate\secrets.dat",
                Names = ["smtp-password"],
                Length = 16,
                EntropyRehardened = false,
            }),
            "secret");

    [Fact]
    public void SecretListKeepsItsShape() =>
        ShouldMatchSnapshot(
            Envelope("secret list", new SecretListResult
            {
                Path = @"C:\ProgramData\WinLogRotate\secrets.dat",
                Protection = "Dpapi",
                FileProtection = "Hardened",
                Secrets =
                [
                    new SecretEntryDto
                    {
                        Name = "smtp-password",
                        Created = new DateTimeOffset(2026, 9, 1, 3, 0, 0, TimeSpan.Zero),
                        Updated = new DateTimeOffset(2026, 9, 11, 3, 0, 0, TimeSpan.Zero),
                        SetBy = "CONTOSO\\dana",
                        Status = "Readable",
                    },
                ],
            }),
            "secret-list");

    [Fact]
    public void NotifyShowKeepsItsShape() =>
        ShouldMatchSnapshot(
            Envelope("notify show", new NotifyShowResult
            {
                Enabled = true,
                On = NotifyOn.Change,
                Threshold = Severity.Warning,
                RemindAfter = "1.00:00:00",
                Budget = "00:00:30",
                Retries = 2,
                WouldSend = true,
                Proxy = "machine default",
                CertificatePin = "AA:BB:CC",
                Targets =
                [
                    new NotifyTargetDto
                    {
                        Target = "eventlog:",
                        Display = "eventlog:",
                        Scheme = "eventlog",
                        Usable = true,
                        Problem = "needs Windows",
                    },
                ],
                Providers =
                [
                    new NotifyProviderDto
                    {
                        Name = "email.relay",
                        Kind = NotifyProviderKind.Email,
                        Enabled = true,
                        Target = "ops@example.invalid",
                        Credential = "@secret:smtp-password",
                        CredentialFree = false,
                    },
                ],
            }),
            "notify-show");

    [Fact]
    public void NotifyStatusKeepsItsShape() =>
        ShouldMatchSnapshot(
            Envelope("notify status", new NotifyStatusResult
            {
                Path = @"C:\ProgramData\WinLogRotate\notify.json",
                Jobs =
                [
                    new NotifyJobStatusDto
                    {
                        Job = "iis",
                        Outcome = NotifyOutcome.Failing,
                        NotifiedAt = new DateTimeOffset(2026, 9, 11, 3, 0, 0, TimeSpan.Zero),
                        FailingSince = new DateTimeOffset(2026, 9, 9, 3, 0, 0, TimeSpan.Zero),
                    },
                ],
                Channels =
                [
                    new NotifyChannelStatusDto
                    {
                        Channel = "email.relay",
                        State = BreakerVerdict.Open,
                        ConsecutiveFailures = 5,
                        SkipRunsRemaining = 3,
                        LastError = "the relay refused the connection",
                        LastAttempt = new DateTimeOffset(2026, 9, 11, 3, 0, 0, TimeSpan.Zero),
                    },
                ],
                Reset = ["email.relay"],
            }),
            "notify-status");

    [Fact]
    public void NotifyTestKeepsItsShape() =>
        ShouldMatchSnapshot(
            Envelope("notify test", new NotifyTestResult
            {
                Sent = 1,
                Failed = 1,
                Channels =
                [
                    new NotifyTestChannelDto
                    {
                        Channel = "email.relay",
                        Display = "email.relay (ops@example.invalid)",
                        Ok = false,
                        Milliseconds = 412,
                        Status = 535,
                        Error = "the relay refused the credential",
                        WouldBeSkipped = true,
                    },
                ],
            }),
            "notify-test");

    /// <summary>
    /// The envelope a verb with no payload publishes, which is most of the refusals.
    /// </summary>
    /// <remarks>
    /// <c>EmptyResult</c> is what <c>CommandContext.Guarded</c> falls back to and what a parse
    /// error now answers with, so it is on more paths than any single verb.
    /// </remarks>
    [Fact]
    public void AnEmptyResultKeepsItsShape() =>
        ShouldMatchSnapshot(
            new CliEnvelope<EmptyResult>
            {
                Schema = 1,
                Product = "WinLogRotate",
                Version = "0.0.0",
                Verb = "run",
                Ok = false,
                ExitCode = ExitCode.ConfigInvalid,
                Result = null,
                Diagnostics =
                [
                    new CliDiagnostic
                    {
                        Severity = Severity.Error,
                        Code = DiagnosticCode.ArgumentUnusable,
                        Message = "Unrecognized command or argument '--dry-runn'.",
                        Remedy = "Run 'winlogrotate run --help' for usage.",
                    },
                ],
            },
            "empty");

    /// <summary>
    /// Every result type this product can publish has its shape pinned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Seven verbs were pinned and about twenty were not - including <c>secret</c>, <c>probe</c>
    /// and <c>glob</c>, all three of which CI itself parses. A snapshot that exists for some of
    /// the surface is a snapshot that catches a change to some of the surface, and this file's
    /// own remarks explain what that costs: "Nothing noticed for two milestones, because nothing
    /// asserted what the envelope looked like."
    /// </para>
    /// <para>
    /// Derived from <c>CliJsonContext</c> by reflection rather than from a list kept by hand,
    /// because a list kept by hand is one more thing to forget alongside the snapshot itself.
    /// Registering a result type there is already mandatory - <c>JsonOutputSink.Complete</c>
    /// throws without it - so the registration is the one place a new verb cannot skip.
    /// </para>
    /// <para>
    /// Coverage is checked property by property against the snapshots rather than by counting
    /// files: a snapshot whose fixture left a field null or a list empty pins a shape that says
    /// nothing about that field, which is the same gap one level down.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryPublishedResultTypeHasItsShapePinned()
    {
        var snapshots = Directory
            .EnumerateFiles(
                Path.Combine(RepoRoot.Find().FullName, "tests", "WinLogRotate.Core.Tests", "envelope"),
                "*.txt")
            .Select(File.ReadAllText)
            .ToArray();

        var missing = new List<string>();

        foreach (var payload in PublishedResultTypes())
        {
            // camelCase, matching JsonSourceGenerationOptions on CliJsonContext.
            var fields = payload
                .GetProperties()
                .Select(p => $"result.{char.ToLowerInvariant(p.Name[0])}{p.Name[1..]}")
                .ToArray();

            // A type with no properties at all publishes no `result` key under the CLI's
            // WhenWritingNull policy. EmptyResult is the only one, and empty.txt pins exactly
            // that absence.
            if (fields.Length == 0)
            {
                continue;
            }

            // Three spellings, because Shape() records a leaf as "x : Kind", an array as "x[]",
            // and a nested object only through its own leaves - "result.notify.enabled". Looking
            // for the bare name alone reported DoctorResult.Notify as unpinned when it is
            // pinned six levels down, which is the kind of false red that gets a rule deleted.
            var covered = snapshots.Any(shape =>
                fields.All(f => shape.Contains(f + " ", StringComparison.Ordinal)
                                || shape.Contains(f + "[]", StringComparison.Ordinal)
                                || shape.Contains(f + ".", StringComparison.Ordinal)));

            if (!covered)
            {
                missing.Add(payload.Name);
            }
        }

        missing.ShouldBeEmpty(
            "a result type this product publishes has no pinned shape - add a fixture to "
            + "EnvelopeShapeTests, populating every nullable and every list");
    }

    /// <summary>Every <c>T</c> for which <c>CliEnvelope&lt;T&gt;</c> is registered.</summary>
    private static IEnumerable<Type> PublishedResultTypes() =>
        typeof(Cli.CliJsonContext)
            .GetProperties()
            .Select(p => p.PropertyType)
            .Where(t => t.IsGenericType
                        && t.GetGenericTypeDefinition().Name.StartsWith("JsonTypeInfo", StringComparison.Ordinal))
            .Select(t => t.GetGenericArguments()[0])
            .Where(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(CliEnvelope<>))
            .Select(t => t.GetGenericArguments()[0])
            .Distinct();
}
