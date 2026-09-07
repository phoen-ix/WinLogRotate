using System.Text.Json.Serialization;
using WinLogRotate.Contracts;

namespace WinLogRotate.Core.Journaling;

/// <summary>
/// Source-generated serialization for journal entries. Reflection-based serialization throws
/// in a NativeAOT binary, and the journal is written by the CLI on every single run - so this
/// is the least acceptable place in the product for a runtime-only failure.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(CliEvent))]
internal sealed partial class JournalJsonContext : JsonSerializerContext;
