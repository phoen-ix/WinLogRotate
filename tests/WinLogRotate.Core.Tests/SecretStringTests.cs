using System.Text.Json;
using System.Text.Json.Serialization;
using Shouldly;
using WinLogRotate.Core.Secrets;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// Every one of these is a real way a credential escapes: an interpolated string in a log line,
/// a record printed by a --verbose dump, an exception message, a JSON envelope. They are tested
/// individually because "it redacts" is not a property anyone can verify by reading the code.
/// </summary>
public sealed class SecretStringTests
{
    private const string Password = "hunter2-correct-horse";

    [Fact]
    public void ToStringIsAsterisks()
    {
        SecretString.From(Password).ToString().ShouldBe("***");
    }

    [Fact]
    public void AnAbsentSecretPrintsNothingRatherThanAsterisks()
    {
        // Otherwise every "no password configured" line reads as though one were set.
        SecretString.None.ToString().ShouldBe(string.Empty);
        SecretString.None.HasValue.ShouldBeFalse();
    }

    [Fact]
    public void StringInterpolationIsRedacted()
    {
        var s = SecretString.From(Password);
        $"password={s}".ShouldBe("password=***");
    }

    [Fact]
    public void StringFormatIsRedacted()
    {
        string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}", SecretString.From(Password))
            .ShouldBe("***");
    }

    [Fact]
    public void StringConcatIsRedacted()
    {
        ("pw: " + SecretString.From(Password)).ShouldBe("pw: ***");
    }

    [Fact]
    public void AnExceptionMessageBuiltFromOneIsRedacted()
    {
        var e = new InvalidOperationException($"could not authenticate with {SecretString.From(Password)}");

        e.Message.ShouldNotContain(Password);
        e.Message.ShouldContain("***");
    }

    [Fact]
    public void ARecordContainingOneRedactsItsGeneratedToString()
    {
        // The sneaky one. A --verbose dump or a debugger-adjacent log line prints a whole
        // settings record, and the compiler-generated ToString walks every member.
        var settings = new FakeSmtp("relay.corp.example", SecretString.From(Password));

        var text = settings.ToString();
        text.ShouldNotContain(Password);
        text.ShouldContain("***");
        text.ShouldContain("relay.corp.example");
    }

    [Fact]
    public void SerializingOneEmitsAsterisks()
    {
        var json = JsonSerializer.Serialize(
            new FakeSmtpDto { Host = "relay", Password = SecretString.From(Password) },
            FakeJsonContext.Default.FakeSmtpDto);

        json.ShouldNotContain(Password);
        json.ShouldContain("***");
    }

    [Fact]
    public void AnAbsentSecretSerializesAsNull_NotAsAMaskedOne()
    {
        var json = JsonSerializer.Serialize(
            new FakeSmtpDto { Host = "relay", Password = SecretString.None },
            FakeJsonContext.Default.FakeSmtpDto);

        json.ShouldNotContain("***");
    }

    [Fact]
    public void DeserializingOneIsRefused()
    {
        // Accepting a secret from JSON would create a path by which one arrives from a file
        // somebody pasted into a ticket.
        Should.Throw<NotSupportedException>(() => JsonSerializer.Deserialize(
            """{"host":"relay","password":"hunter2"}""", FakeJsonContext.Default.FakeSmtpDto));
    }

    [Fact]
    public void TheHashIsNotAFunctionOfTheValue()
    {
        // A hash of a low-entropy password confirms an offline guess. Anything that ends up in
        // a dictionary, a structural record hash or a heap snapshot must not carry one.
        SecretString.From("a").GetHashCode().ShouldBe(SecretString.From("b").GetHashCode());
        SecretString.None.GetHashCode().ShouldNotBe(SecretString.From("a").GetHashCode());
    }

    [Fact]
    public void EqualityStillWorksSoAChangeCanBeDetected()
    {
        SecretString.From(Password).ShouldBe(SecretString.From(Password));
        SecretString.From(Password).ShouldNotBe(SecretString.From("different"));
        (SecretString.From("a") == SecretString.From("a")).ShouldBeTrue();
        (SecretString.From("a") != SecretString.From("b")).ShouldBeTrue();
    }

    [Fact]
    public void RevealReturnsTheValue()
    {
        SecretString.From(Password).Reveal().ShouldBe(Password);
        SecretString.None.Reveal().ShouldBe(string.Empty);
    }

    [Fact]
    public void LengthIsAvailableButNothingElseIs()
    {
        SecretString.From(Password).Length.ShouldBe(Password.Length);
        SecretString.None.Length.ShouldBe(0);
    }

    [Fact]
    public void ThereIsNoConversionOperatorToString()
    {
        // One conversion operator would undo every other test in this file, because each leak
        // path above goes through a conversion first. Checked by reflection rather than against
        // the source text, which would also match the comment saying it is absent.
        var conversions = typeof(SecretString)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(m => m.Name is "op_Implicit" or "op_Explicit")
            .Where(m => m.ReturnType == typeof(string))
            .Select(m => m.Name)
            .ToArray();

        conversions.ShouldBeEmpty();
    }

    private sealed record FakeSmtp(string Host, SecretString Password);
}

/// <summary>A stand-in for a real settings DTO, to exercise the source generator.</summary>
public sealed record FakeSmtpDto
{
    public required string Host { get; init; }
    public SecretString Password { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(FakeSmtpDto))]
internal sealed partial class FakeJsonContext : JsonSerializerContext;
