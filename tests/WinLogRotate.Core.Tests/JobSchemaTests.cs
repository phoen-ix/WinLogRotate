using Shouldly;
using WinLogRotate.Contracts;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Safety;
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

    // ---- what a form needs to know that the binder used to keep to itself -------------------

    private static EffectiveJob BindEmptyJob()
    {
        var bag = new DiagnosticBag();
        var job = ConfigBinder.BindJob(
            TomlFile.Parse("schema = 1\n\n[job]\nname = \"x\"\npaths = [\"C:/logs/*.log\"]\n", "x.toml"), bag);

        job.ShouldNotBeNull();
        return SettingsMerge.Resolve(job, null);
    }

    /// <summary>The value a job gets when it says nothing, as the binder resolves it.</summary>
    private static object? Effective(EffectiveJob job, string key) => key switch
    {
        "kind" => job.Kind,
        "enabled" => job.Enabled,
        "allowdangerous" => job.AllowDangerous.Count == 0 ? null : job.AllowDangerous,
        "schedule" => job.Schedule,
        "size" => job.SizeThreshold,
        "weekday" => job.Weekday,
        "monthday" => job.MonthDay,
        "rotate" => job.Rotate,
        "start" => job.Start,
        "maxage" => job.MaxAge,
        "minage" => job.MinAge,
        "minsize" => job.MinSize,
        "maxsize" => job.MaxSize,
        "maxfiles" => job.MaxFiles,
        "compress" => job.Compress,
        "compresstype" => job.CompressType,
        "delaycompress" => job.DelayCompress,
        "dateext" => job.DateExt,
        "dateformat" => job.DateFormat,
        "olddir" => job.OldDir,
        "createolddir" => job.CreateOldDir,
        "missingok" => job.MissingOk,
        "notify" => job.Notify,
        "notifempty" => job.NotIfEmpty,
        "lockstrategy" => job.LockStrategy,
        "livefiles" => job.LiveFiles,
        "retrycount" => job.RetryCount,
        "retryinterval" => job.RetryIntervalMs,
        "prerotate" => job.PreRotate.Count == 0 ? null : job.PreRotate,
        "postrotate" => job.PostRotate.Count == 0 ? null : job.PostRotate,
        "hook_timeout" => job.HookTimeout,

        // name and paths are required, and a shorthand is not a value a job holds.
        _ => null,
    };

    private static bool SameAs(JobKey row, object value) => row.Kind switch
    {
        JobKeyKind.Flag => value is bool b && bool.Parse(row.Default!) == b,
        JobKeyKind.Integer => value is int i && int.Parse(row.Default!, System.Globalization.CultureInfo.InvariantCulture) == i,
        JobKeyKind.Size => value is long l && ConfigBinder.TryParseSize(row.Default!, out var bytes) && bytes == l,
        JobKeyKind.Duration => value is TimeSpan t && ConfigBinder.TryParseDuration(row.Default!, out var span) && span == t,
        JobKeyKind.Enum => string.Equals(value.ToString(), row.Default, StringComparison.OrdinalIgnoreCase),
        JobKeyKind.Text => value is string s && s == row.Default,
        _ => false,
    };

    /// <summary>
    /// The default a row declares is the value the binder gives a job that says nothing.
    /// </summary>
    /// <remarks>
    /// The schema's Default column exists so a form can say "inherited (default 7)" without
    /// binding a job to find out. Restated by hand, it would drift the first time somebody
    /// changed <c>BuiltInDefaults</c> - so this binds an empty job and holds every row to it.
    /// </remarks>
    [Fact]
    public void EveryDefaultIsTheBindersOwn()
    {
        var job = BindEmptyJob();
        var wrong = new List<string>();

        foreach (var row in JobSchema.Keys)
        {
            var effective = Effective(job, row.Key);

            if (row.Default is null)
            {
                if (effective is not null)
                {
                    wrong.Add($"{row.Key}: the schema says no default, the binder gives {effective}");
                }
            }
            else if (effective is null || !SameAs(row, effective))
            {
                wrong.Add($"{row.Key}: the schema says {row.Default}, the binder gives {effective ?? "nothing"}");
            }
        }

        wrong.ShouldBeEmpty();
    }

    [Fact]
    public void EveryDefaultParsesAsItsOwnKey()
    {
        var unparseable = JobSchema.Keys
            .Where(row => row.Default is not null && !JobSchema.TryParse(row.Key, row.Default, out _, out _))
            .Select(row => $"{row.Key} = {row.Default}")
            .ToArray();

        unparseable.ShouldBeEmpty();
    }

    /// <summary>Every key can be captioned and explained, in one sentence each, and no two alike.</summary>
    [Fact]
    public void EveryKeyHasATitleAndOneSentence()
    {
        foreach (var row in JobSchema.Keys)
        {
            row.Title.ShouldNotBeNullOrWhiteSpace(row.Key);
            row.Description.ShouldEndWith(".", Case.Sensitive, row.Key);
            row.Description.Length.ShouldBeLessThanOrEqualTo(120, row.Key);
        }

        JobSchema.Keys.Select(row => row.Description).ShouldBeUnique();
        JobSchema.Keys.Select(row => row.Title).ShouldBeUnique();
    }

    private static EffectiveJob With(EffectiveJob job, string key, int value) => key switch
    {
        "weekday" => job with { Weekday = value },
        "monthday" => job with { MonthDay = value },
        "rotate" => job with { Rotate = value },
        "start" => job with { Start = value },
        "maxage" => job with { MaxAge = value },
        "minage" => job with { MinAge = value },
        "maxfiles" => job with { MaxFiles = value },
        "livefiles" => job with { LiveFiles = value },
        "retrycount" => job with { RetryCount = value },
        "retryinterval" => job with { RetryIntervalMs = value },
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "not an integer key this test knows"),
    };

    private static bool Refuses(EffectiveJob job, string key)
    {
        var bag = new DiagnosticBag();
        ConfigValidator.Validate(job, new PathGuard(new GuardOptions { ProtectedRoots = [] }), bag);
        return bag.Items.Any(d => d.Severity >= Severity.Error && d.Message.StartsWith(key, StringComparison.Ordinal));
    }

    /// <summary>
    /// A range the schema declares is a range the validator enforces, and no more.
    /// </summary>
    /// <remarks>
    /// Both halves matter. A floor the validator does not have would make a form refuse a value
    /// the CLI accepts; a floor the schema does not declare is a refusal the form only learns of
    /// from the dry run. maxage and minage have no floor either side, and that is recorded here
    /// rather than papered over.
    /// </remarks>
    [Fact]
    public void EveryIntegerRangeIsTheValidatorsOwn()
    {
        var job = BindEmptyJob();
        var wrong = new List<string>();

        foreach (var row in JobSchema.Keys.Where(r => r.Kind == JobKeyKind.Integer))
        {
            if (row.Min is { } min)
            {
                if (!Refuses(With(job, row.Key, min - 1), row.Key))
                {
                    wrong.Add($"{row.Key} = {min - 1} is below the schema's floor and the validator accepts it");
                }

                if (Refuses(With(job, row.Key, min), row.Key))
                {
                    wrong.Add($"{row.Key} = {min} is the schema's floor and the validator refuses it");
                }
            }
            else if (Refuses(With(job, row.Key, -1), row.Key))
            {
                wrong.Add($"{row.Key} has no floor in the schema, but the validator refuses -1");
            }

            if (row.Max is { } max)
            {
                if (!Refuses(With(job, row.Key, max + 1), row.Key))
                {
                    wrong.Add($"{row.Key} = {max + 1} is above the schema's ceiling and the validator accepts it");
                }

                if (Refuses(With(job, row.Key, max), row.Key))
                {
                    wrong.Add($"{row.Key} = {max} is the schema's ceiling and the validator refuses it");
                }
            }
        }

        wrong.ShouldBeEmpty();

    }

    /// <summary>
    /// Every choice the schedule row offers is one the binder reads, size included.
    /// </summary>
    /// <remarks>
    /// The binder accepted <c>schedule = "size"</c> - its own remedy lists it - while the schema
    /// offered five choices, so a form built from the schema could not show a job the file was
    /// allowed to hold.
    /// </remarks>
    [Fact]
    public void ScheduleAcceptsSizeBecauseTheBinderDoes()
    {
        var row = JobSchema.Find("schedule").ShouldNotBeNull();
        row.Choices.ShouldContain("size");

        foreach (var choice in row.Choices)
        {
            Enum.TryParse<Schedule>(choice, ignoreCase: true, out var parsed).ShouldBeTrue(choice);

            var bag = new DiagnosticBag();
            var job = ConfigBinder.BindJob(TomlFile.Parse(
                $"schema = 1\n\n[job]\nname = \"x\"\npaths = [\"C:/logs/*.log\"]\nschedule = \"{choice}\"\n", "x.toml"), bag);

            SettingsMerge.Resolve(job.ShouldNotBeNull(), null).Schedule.ShouldBe(parsed, choice);
        }
    }

    [Fact]
    public void EveryCountedUnitSitsOnAnInteger() =>
        JobSchema.Keys
            .Where(row => row.Unit != JobKeyUnit.None)
            .ShouldAllBe(row => row.Kind == JobKeyKind.Integer);
}
