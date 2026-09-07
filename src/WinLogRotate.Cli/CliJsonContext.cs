using System.Text.Json;
using System.Text.Json.Serialization;
using WinLogRotate.Cli.Output;
using WinLogRotate.Contracts;

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
    WriteIndented = false)]
[JsonSerializable(typeof(CliEnvelope<EmptyResult>))]
[JsonSerializable(typeof(CliEnvelope<VersionResult>))]
[JsonSerializable(typeof(CliEnvelope<GlobResult>))]
[JsonSerializable(typeof(CliEnvelope<JournalResult>))]
[JsonSerializable(typeof(CliEnvelope<ConfigCheckResult>))]
[JsonSerializable(typeof(CliEnvelope<ConfigShowResult>))]
[JsonSerializable(typeof(CliEnvelope<RunResult>))]
[JsonSerializable(typeof(CliEnvelope<DoctorResult>))]
[JsonSerializable(typeof(CliEvent))]
[JsonSerializable(typeof(CliDiagnostic))]
internal sealed partial class CliJsonContext : JsonSerializerContext;
