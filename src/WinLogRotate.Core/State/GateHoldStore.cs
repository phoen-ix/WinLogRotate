using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinLogRotate.Core.State;

/// <summary>When the rotation gate was first found held, so a span can be judged.</summary>
internal sealed record GateHoldDocument
{
    public DateTimeOffset? FirstRefusedAt { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(GateHoldDocument))]
internal sealed partial class GateHoldJsonContext : JsonSerializerContext;

/// <summary>
/// The one fact a refused run has to remember: when it was first refused.
/// </summary>
/// <remarks>
/// <para>
/// A run that is refused does nothing and exits, so nothing about the refusal survives it. One
/// refusal is normal - an overlapping manual run - and a thousand consecutive ones is a machine
/// on which nothing rotates. Those are the same event until somebody writes down the first.
/// </para>
/// <para>
/// It lives in <c>run\</c>, under the data root, which the repair and the installer now harden
/// explicitly. That is load-bearing: the account this record exists to detect is a local one,
/// and if it can create or rewrite the file it can hold the first refusal forward for ever and
/// the judgement never fires. <see cref="Read"/> takes <c>trusted</c> rather than deciding for
/// itself, because whether the surface is hardened is a Windows question and this is not.
/// </para>
/// <para>
/// A write that fails is not reported. The precedent is the journal: a rotation must not stop
/// because its diary is full, and this is a run that already did nothing.
/// </para>
/// </remarks>
public static class GateHoldStore
{
    private static string FileIn(string runDirectory) => Path.Combine(runDirectory, "gate.json");

    /// <summary>When the gate was first refused, or null if that is not established.</summary>
    /// <param name="trusted">
    /// False when the directory holding the record can be written by somebody who is not an
    /// administrator - in which case the record is evidence about them, written by them.
    /// </param>
    public static DateTimeOffset? Read(string runDirectory, bool trusted)
    {
        if (!trusted)
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(FileIn(runDirectory));

            return JsonSerializer
                .Deserialize(json, GateHoldJsonContext.Default.GateHoldDocument)?.FirstRefusedAt;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Records this refusal as the first one, if no earlier one is on record.</summary>
    public static void Record(string runDirectory, DateTimeOffset now, bool trusted)
    {
        if (Read(runDirectory, trusted) is not null)
        {
            return;
        }

        Write(runDirectory, new GateHoldDocument { FirstRefusedAt = now });
    }

    /// <summary>Forgets the refusals, because the gate was taken.</summary>
    /// <remarks>
    /// Called on both acquiring outcomes, the abandoned one included: a run that had to break a
    /// dead holder's lock still rotated, so the span it would otherwise carry forward is over.
    /// </remarks>
    public static void Clear(string runDirectory)
    {
        // Nothing recorded, nothing to forget - and on the overwhelmingly common path this
        // keeps a successful run from creating the directory and the file at all.
        if (!File.Exists(FileIn(runDirectory)))
        {
            return;
        }

        Write(runDirectory, new GateHoldDocument { FirstRefusedAt = null });
    }

    private static void Write(string runDirectory, GateHoldDocument document)
    {
        try
        {
            AtomicJson.Write(
                FileIn(runDirectory),
                JsonSerializer.Serialize(document, GateHoldJsonContext.Default.GateHoldDocument));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Deliberately silent, and deliberately not a diagnostic. This is the bookkeeping of
            // a run that did nothing; reporting a failure to write it would be reporting a
            // second problem to somebody who has not been told about the first.
        }
    }
}
