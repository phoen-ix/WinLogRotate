using System.Text.Json;
using System.Text.Json.Serialization;
using WinLogRotate.Cli.Output;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Notify;
using WinLogRotate.Core.State;
using WinLogRotate.Hosting.Hosts;
using WinLogRotate.Hosting.Security;

namespace WinLogRotate.Cli;

/// <summary>
/// Source-generated serialization for everything crossing the CLI boundary.
/// <para>
/// This is not an optimization, it is a requirement: a reflection-based
/// <c>JsonSerializer.Serialize&lt;T&gt;</c> throws at runtime in a NativeAOT binary, and it
/// does so on whichever code path happens to hit it - so the failure surfaces in production
/// rather than in a test. Every envelope instantiation must be listed here; the JSON sink
/// throws a clear error at development time if one is missing.
/// </para>
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    WriteIndented = false)]
[JsonSerializable(typeof(CliEnvelope<EmptyResult>))]
[JsonSerializable(typeof(CliEnvelope<VersionResult>))]
[JsonSerializable(typeof(CliEnvelope<GlobResult>))]
[JsonSerializable(typeof(CliEnvelope<JournalResult>))]
[JsonSerializable(typeof(CliEnvelope<ConfigCheckResult>))]
[JsonSerializable(typeof(CliEnvelope<ConfigShowResult>))]
[JsonSerializable(typeof(CliEnvelope<RunResult>))]
[JsonSerializable(typeof(CliEnvelope<DoctorResult>))]
[JsonSerializable(typeof(CliEnvelope<HostResult>))]
[JsonSerializable(typeof(CliEnvelope<ProbeResultDto>))]
[JsonSerializable(typeof(CliEnvelope<ImportResult>))]
[JsonSerializable(typeof(CliEnvelope<ExportTaskResult>))]
[JsonSerializable(typeof(CliEnvelope<ScanResult>))]
[JsonSerializable(typeof(CliEnvelope<PauseResult>))]
[JsonSerializable(typeof(CliEnvelope<UpdateResult>))]
[JsonSerializable(typeof(CliEnvelope<SecretListResult>))]
[JsonSerializable(typeof(CliEnvelope<SecretResult>))]
[JsonSerializable(typeof(CliEnvelope<NotifyShowResult>))]
[JsonSerializable(typeof(CliEnvelope<NotifyStatusResult>))]
[JsonSerializable(typeof(CliEnvelope<NotifyTestResult>))]
[JsonSerializable(typeof(CliDiagnostic))]
internal sealed partial class CliJsonContext : JsonSerializerContext;
