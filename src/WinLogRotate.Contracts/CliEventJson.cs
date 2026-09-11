using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinLogRotate.Contracts;

/// <summary>
/// The one serializer for <see cref="CliEvent"/>, beside the type it serializes.
/// </summary>
/// <remarks>
/// <para>
/// Source-generated because a reflection-based <c>JsonSerializer</c> call throws at runtime in a
/// NativeAOT binary, on whichever path happens to reach it - and the journal is written by the
/// CLI on every single run, which makes it the least acceptable place in the product for a
/// failure that only appears in the field.
/// </para>
/// <para>
/// Here rather than in Core or Cli because there were two of these, both internal, both
/// describing the same wire type with the same options - one for the journal and one for the
/// stream. Two generators for one contract can drift, and neither was reachable from the GUI,
/// which is the party the contract exists for: it tailed the stream as raw text because it had
/// no way to read it as anything else.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(CliEvent))]
public sealed partial class CliEventJson : JsonSerializerContext;

/// <summary>
/// The same, for <see cref="CliDiagnostic"/>.
/// </summary>
/// <remarks>
/// Its own context rather than another type on the one above, so each stays named for what it
/// serializes. The GUI reads these one element at a time out of an envelope's diagnostics array:
/// it cannot deserialize the envelope itself, whose result types are registered only in the
/// CLI's internal context.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    WriteIndented = false)]
[JsonSerializable(typeof(CliDiagnostic))]
public sealed partial class CliDiagnosticJson : JsonSerializerContext;
