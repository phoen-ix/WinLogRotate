using Shouldly;
using WinLogRotate.Contracts;
using WinLogRotate.Core.Configuration;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// One table saying what a <c>[job]</c> key is called and what it holds.
/// </summary>
/// <remarks>
/// <para>
/// The binder already knew the names. What it did not carry is the <i>type</i>, and a writer
/// without it must guess from the text - which writes <c>rotate = "30"</c>, a string the binder
/// rejects, or <c>dateformat = 20240101</c>, a bare number for a key that must be a string.
/// </para>
/// <para>
/// The list is key-driven and never property-driven. TOML <c>notifempty</c> binds to
/// <c>NotIfEmpty</c> and <c>retryinterval</c> to <c>RetryIntervalMs</c>, so a surface derived
/// from the CLR properties would offer a key no file may contain.
/// </para>
/// </remarks>
public sealed class JobSchemaTests
{
    /// <summary>
    /// Every key the binder knows has a row here, and nothing here is unknown to the binder.
    /// </summary>
    /// <remarks>
    /// Asserted both ways. One direction alone is satisfied by a schema that is a superset, which
    /// would offer a key nothing reads.
    /// </remarks>
    [Fact]
    public void TheSchemaAndTheBinderKnowTheSameKeys()
    {
        var schema = JobSchema.Keys.Select(k => k.Key).Order(StringComparer.Ordinal).ToArray();

        // The binder's list is derived from the schema, so this is the derivation itself under
        // test. It is here rather than trusted because the derivation is one `.Select` away from
        // being a filter somebody added for a reason that made sense at the time.
        ConfigBinder.JobKeys.Order(StringComparer.Ordinal).ShouldBe(schema);

        JobSchema.Keys.Count.ShouldBe(38, "the schema is the whole of the [job] surface");
    }

    /// <summary>
    /// Every key can be written and read back as what was asked for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The test that would have caught the outage. For each key it parses the row's own sample,
    /// writes it into a file, and binds the result - asserting the diagnostic bag is empty. A key
    /// whose type is wrong in the schema produces "must be true or false" or "must be a whole
    /// number" here, which is what a caller would otherwise discover by stopping the machine.
    /// </para>
    /// <para>
    /// One key per file, deliberately. Seven of the thirty-eight write the same <c>Schedule</c>
    /// field and the binder takes the last by document order, so writing them all into one table
    /// would test precedence rather than reachability.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryKeyCanBeWrittenAndReadBack()
    {
        var unreachable = new List<string>();

        foreach (var row in JobSchema.Keys)
        {
            if (!JobSchema.TryParse(row.Key, row.Sample, out var value, out var problem))
            {
                unreachable.Add($"{row.Key}: its own sample '{row.Sample}' is {problem}");
                continue;
            }

            var file = TomlFile.Parse(
                "schema = 1\n\n[job]\nname = \"sample-job\"\npaths = [\"C:/logs/*.log\"]\n",
                "sample-job.toml");

            if (!TomlEditor.TrySet(file, ["job"], row.Key, value, out _, out var detail))
            {
                unreachable.Add($"{row.Key}: could not be written - {detail}");
                continue;
            }

            var bag = new DiagnosticBag();
            var job = ConfigBinder.BindJob(TomlFile.Parse(file.ToString(), "sample-job.toml"), bag);

            var errors = bag.Items.Where(d => d.Severity >= Severity.Error).ToArray();

            if (errors.Length > 0)
            {
                unreachable.Add($"{row.Key}: {errors[0].Message}");
            }
            else if (job is null)
            {
                unreachable.Add($"{row.Key}: the job did not bind at all");
            }
        }

        unreachable.ShouldBeEmpty(
            "every [job] key must be writable in the type the binder reads it back as");
    }

    /// <summary>A value of the wrong shape is refused, rather than written and rejected later.</summary>
    /// <remarks>
    /// Refusing at the command line means the operator is told at the moment they typed it. The
    /// alternative is a file that parses, a binder that errors, and - because a type error is not
    /// file-scoped - a machine where nothing rotates.
    /// </remarks>
    [Theory]
    [InlineData("rotate", "soon")]
    [InlineData("enabled", "yes")]
    [InlineData("compresstype", "bzip2")]
    [InlineData("hook_timeout", "a while")]
    [InlineData("maxsize", "big")]
    public void AValueOfTheWrongShapeIsRefused(string key, string text)
    {
        JobSchema.TryParse(key, text, out _, out var problem).ShouldBeFalse();
        problem.ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>A key nobody knows is refused, and near misses are named.</summary>
    [Fact]
    public void AKeyNobodyKnowsIsRefusedAndNearMissesAreNamed()
    {
        JobSchema.TryParse("postrotae", "x", out _, out var near).ShouldBeFalse();
        near.ShouldNotBeNull().ShouldContain("postrotate");

        JobSchema.TryParse("firstaction", "x", out _, out var far).ShouldBeFalse();
        far.ShouldNotBeNull().ShouldNotContain("Did you mean");
    }

    /// <summary>
    /// A size keeps the spelling it was given.
    /// </summary>
    /// <remarks>
    /// <c>GetSize</c> reads a bare integer or a quoted suffix form, and both are correct - so the
    /// one the operator typed is the one written. Rewriting <c>"100M"</c> as <c>104857600</c>
    /// would be a diff they did not ask for on a line they did not touch.
    /// </remarks>
    [Fact]
    public void ASizeKeepsTheSpellingItWasGiven()
    {
        JobSchema.TryParse("maxsize", "100M", out var suffixed, out _).ShouldBeTrue();
        suffixed.ToString().ShouldBe("\"100M\"");

        JobSchema.TryParse("maxsize", "104857600", out var bare, out _).ShouldBeTrue();
        bare.ToString().ShouldBe("104857600");
    }

    /// <summary>An enum is written in its canonical spelling, whatever case was typed.</summary>
    [Fact]
    public void AnEnumIsWrittenCanonically()
    {
        JobSchema.TryParse("compresstype", "GZIP", out var value, out _).ShouldBeTrue();
        value.ToString().ShouldBe("\"gzip\"");
    }

    /// <summary>
    /// The structural keys are exactly the ones <c>[defaults]</c> may not carry.
    /// </summary>
    /// <remarks>
    /// `DefaultsKeys` is derived as the job keys minus these, so getting the flag wrong on a row
    /// silently adds or removes a key from what `[defaults]` accepts.
    /// </remarks>
    [Fact]
    public void TheStructuralKeysAreTheOnesDefaultsMayNotCarry() =>
        JobSchema.Keys.Where(k => k.PerJobOnly).Select(k => k.Key)
            .ShouldBe(["name", "paths", "kind", "enabled", "allowdangerous"]);
}
