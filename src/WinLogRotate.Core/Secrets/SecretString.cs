using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinLogRotate.Core.Secrets;

/// <summary>
/// A string that does not print itself.
/// </summary>
/// <remarks>
/// <para>
/// Redaction that lives at each call site fails at the call site somebody forgets, and the one
/// they forget is the one that ships. So it lives here instead, at the boundary: an
/// interpolated string, a <c>record</c>'s generated <c>ToString</c>, <c>string.Format</c>, a
/// log line and an exception message all reach <see cref="ToString"/>, and all of them get
/// three asterisks.
/// </para>
/// <para>
/// Getting the value out requires calling <see cref="Reveal"/>, which is deliberately ugly and
/// deliberately greppable. An architecture test enumerates the files allowed to call it.
/// </para>
/// </remarks>
[JsonConverter(typeof(SecretStringJsonConverter))]
public readonly struct SecretString : IEquatable<SecretString>
{
    private readonly string? _value;

    private SecretString(string value) => _value = value;

    public static SecretString From(string value) => new(value);

    /// <summary>No secret at all - not an empty one.</summary>
    public static SecretString None => default;

    public bool HasValue => _value is not null;

    /// <summary>
    /// Character count, for <c>secret test</c>.
    /// </summary>
    /// <remarks>
    /// A length and nothing else. No hash and not even a prefix: a few bytes of SHA-256 over a
    /// low-entropy password is enough to confirm an offline guess. The length is here because
    /// the reason that verb exists is spotting the trailing newline a shell appended.
    /// </remarks>
    public int Length => _value?.Length ?? 0;

    /// <summary>
    /// The actual value. Call sites are allowlisted by an architecture test.
    /// </summary>
    public string Reveal() => _value ?? string.Empty;

    /// <summary>Three asterisks, or nothing at all. Never the value.</summary>
    public override string ToString() => _value is null ? string.Empty : "***";

    /// <summary>
    /// Ordinal comparison, written out rather than inherited.
    /// </summary>
    /// <remarks>
    /// A struct's default <see cref="ValueType.Equals(object)"/> compares by reflection, which
    /// on a type whose whole purpose is not leaking its contents is not a comparison anyone
    /// should be relying on by accident.
    /// </remarks>
    public bool Equals(SecretString other) =>
        _value is null
            ? other._value is null
            : other._value is not null && string.Equals(_value, other._value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is SecretString s && Equals(s);

    /// <summary>
    /// Deliberately not a function of the value.
    /// </summary>
    /// <remarks>
    /// Hashing it would put a crackable function of a low-entropy password into every
    /// dictionary dump, every structural hash of a record containing one, and every heap
    /// snapshot that survives a crash. Two secrets landing in the same bucket costs nothing
    /// here; there is never more than a handful.
    /// </remarks>
    public override int GetHashCode() => _value is null ? 0 : 1;

    public static bool operator ==(SecretString left, SecretString right) => left.Equals(right);

    public static bool operator !=(SecretString left, SecretString right) => !left.Equals(right);

    // Deliberately absent: implicit operator string. Adding one would undo every line above,
    // because every leak path listed in the remarks goes through an implicit conversion first.
}

/// <summary>
/// Serializes a secret as <c>"***"</c>, and refuses to deserialize one at all.
/// </summary>
/// <remarks>
/// The attribute is on the type, so the source generator honours it and no envelope has to opt
/// in. Reading is unsupported on purpose: accepting a secret from JSON would create a path by
/// which one arrives from a file somebody pasted into a ticket, which is the leak this whole
/// design exists to close.
/// </remarks>
public sealed class SecretStringJsonConverter : JsonConverter<SecretString>
{
    public override void Write(Utf8JsonWriter writer, SecretString value, JsonSerializerOptions options)
    {
        if (value.HasValue)
        {
            writer.WriteStringValue("***");
        }
        else
        {
            writer.WriteNullValue();
        }
    }

    public override SecretString Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException(
            "A secret is never read from JSON. Use the secret store, or read it from stdin.");
}
