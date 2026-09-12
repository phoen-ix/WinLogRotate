using Shouldly;
using WinLogRotate.Contracts;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Safety;
using Xunit;

namespace WinLogRotate.Core.Tests;

public class TomlRoundTripTests
{
    private const string RealisticConfig = """
        # Rotate IIS logs.
        # NOTE: do NOT enable compress - the shipper can't read .gz.   -- rmk, 2024-03
        schema = 1

        [job]
        name  = "IIS W3SVC1"     # trailing comment
        kind  = "manage"
        paths = [
            "C:/inetpub/logs/LogFiles/W3SVC1/u_ex*.log",
        ]

        rotate   = 30
        compress = false

        """;

    /// <summary>
    /// The property the entire config-format decision rests on. If this ever fails, the GUI is
    /// silently destroying operators' comments and the format must change.
    /// </summary>
    [Fact]
    public void LoadThenSaveIsByteIdentical()
    {
        var file = TomlFile.Parse(RealisticConfig, "test.toml");

        file.HasErrors.ShouldBeFalse();
        file.ToString().ShouldBe(RealisticConfig);
    }

    [Fact]
    public void CommentsAndBlankLinesSurviveAParse()
    {
        var text = TomlFile.Parse(RealisticConfig, "test.toml").ToString();

        text.ShouldContain("-- rmk, 2024-03");
        text.ShouldContain("# trailing comment");
    }

    [Theory]
    [InlineData("")]
    [InlineData("schema = 1\n")]
    [InlineData("# just a comment\n")]
    [InlineData("schema = 1\r\n\r\n[job]\r\nname = \"x\"\r\n")]   // CRLF must survive too
    public void RoundTripsAwkwardInputs(string text) =>
        TomlFile.Parse(text, "t.toml").ToString().ShouldBe(text);
}

public class ConfigBinderTests
{
    private static (JobConfig? Job, DiagnosticBag Diagnostics) Bind(string toml)
    {
        var bag = new DiagnosticBag();
        var job = ConfigBinder.BindJob(TomlFile.Parse(toml, "job.toml"), bag);
        return (job, bag);
    }

    [Fact]
    public void BindsAManageJob()
    {
        var (job, d) = Bind("""
            schema = 1
            [job]
            name = "IIS"
            kind = "manage"
            paths = ["C:/inetpub/logs/**/u_ex*.log"]
            rotate = 30
            maxage = 90
            compresstype = "gzip"
            """);

        d.HasErrors.ShouldBeFalse();
        job.ShouldNotBeNull();
        job.Name.ShouldBe("IIS");
        job.Kind.ShouldBe(JobKind.Manage);
        job.Paths.ShouldBe(["C:/inetpub/logs/**/u_ex*.log"]);
        job.Rotate.ShouldBe(30);
        job.MaxAge.ShouldBe(90);
        job.CompressType.ShouldBe(CompressType.Gzip);
    }

    // Requiring brackets for a single path is the sort of friction that gets a config written
    // wrong once and the documentation abandoned.
    [Fact]
    public void ASinglePathNeedsNoBrackets()
    {
        var (job, d) = Bind("""
            [job]
            name = "app"
            paths = "C:/logs/app.log"
            """);

        d.HasErrors.ShouldBeFalse();
        job.ShouldNotBeNull().Paths.ShouldBe(["C:/logs/app.log"]);
    }

    /// <summary>
    /// The last key in the file that decides when a job is due is the one that wins.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both directions of every pair, deliberately. The test this replaces wrote <c>daily</c> then
    /// <c>weekly</c> and expected Weekly - which is also the answer a fixed precedence table
    /// gives, because <c>weekly</c> sat after <c>daily</c> in the array the binder probed.
    /// Document order and implementation order agreed, so the assertion held under either
    /// semantics and the defect it was named after was live underneath it. Reverse any row here
    /// and its partner fails.
    /// </para>
    /// <para>
    /// The expectations are logrotate 3.21.0's own, executed against a config file holding all
    /// seven blocks. It says so out loud as it reads: "note: 'daily' overrides previously
    /// specified 'weekly'", and the reverse for the reverse. <c>size</c> is included because
    /// upstream treats it as one more assignment to the same field - <c>size 1M</c> then
    /// <c>daily</c> is "after 1 days", and <c>daily</c> then <c>size 1M</c> is "1048576 bytes".
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("weekly = true\ndaily = true", Schedule.Daily)]
    [InlineData("daily = true\nweekly = true", Schedule.Weekly)]
    [InlineData("size = \"1M\"\ndaily = true", Schedule.Daily)]
    [InlineData("daily = true\nsize = \"1M\"", Schedule.Size)]
    [InlineData("monthly = true\nhourly = true", Schedule.Hourly)]
    [InlineData("yearly = true\ndaily = true", Schedule.Daily)]
    [InlineData("daily = true\nmonthly = true", Schedule.Monthly)]
    [InlineData("schedule = \"weekly\"\nhourly = true", Schedule.Hourly)]
    [InlineData("hourly = true\nschedule = \"weekly\"", Schedule.Weekly)]
    public void TheLastScheduleKeyInTheFileWins(string keys, Schedule expected)
    {
        var (job, d) = Bind($"""
            [job]
            name = "app"
            paths = "C:/logs/*.log"
            {keys}
            """);

        d.HasErrors.ShouldBeFalse();
        job.ShouldNotBeNull().Schedule.ShouldBe(expected);
    }

    [Theory]
    [InlineData("100", 100L)]
    [InlineData("\"100\"", 100L)]
    [InlineData("\"100k\"", 102400L)]
    [InlineData("\"100M\"", 104857600L)]
    [InlineData("\"1G\"", 1073741824L)]
    public void SizesAcceptBinarySuffixesLikeLogrotate(string literal, long expected)
    {
        var (job, d) = Bind($"""
            [job]
            name = "app"
            paths = "C:/logs/*.log"
            maxsize = {literal}
            """);

        d.HasErrors.ShouldBeFalse();
        job.ShouldNotBeNull().MaxSize.ShouldBe(expected);
    }

    [Fact]
    public void DiagnosticsCarryLineAndColumn()
    {
        var (_, d) = Bind("""
            [job]
            name = "app"
            paths = "C:/logs/*.log"
            rotate = "not a number"
            """);

        // Two diagnostics: the type error, plus a warning that the file omits 'schema'.
        var error = d.Items.Where(x => x.Severity >= Severity.Error).ShouldHaveSingleItem();
        error.Line.ShouldBe(4);
        error.Message.ShouldContain("rotate");
    }

    [Fact]
    public void AnUnknownEnumValueListsTheValidOnes()
    {
        var (_, d) = Bind("""
            [job]
            name = "app"
            paths = "C:/logs/*.log"
            compresstype = "rar"
            """);

        var error = d.Items.First(x => x.Severity >= Severity.Error);
        error.Remedy.ShouldNotBeNull().ShouldContain("gzip");
    }

    [Fact]
    public void AFutureSchemaIsRefusedRatherThanMisread()
    {
        var (_, d) = Bind("""
            schema = 99
            [job]
            name = "app"
            paths = "C:/logs/*.log"
            """);

        d.Items.ShouldContain(x => x.Severity >= Severity.Error && x.Message.Contains("schema 99"));
    }
}

public class SettingsMergeTests
{
    private static JobConfig Job(JobSettings? overrides = null) => new()
    {
        Name = "app",
        Paths = ["C:/logs/*.log"],
        Rotate = overrides?.Rotate,
        Compress = overrides?.Compress,
        CompressType = overrides?.CompressType,
        Schedule = overrides?.Schedule,
    };

    [Fact]
    public void BuiltInDefaultsApplyWhenNothingElseSaysAnything()
    {
        var job = SettingsMerge.Resolve(Job(), fileDefaults: null);

        job.Schedule.ShouldBe(Schedule.Daily);
        job.Rotate.ShouldBe(7);
        job.CompressType.ShouldBe(CompressType.Zip);
        job.NotIfEmpty.ShouldBeTrue();
        job.LockStrategy.ShouldBe(LockStrategy.Rename);
    }

    [Fact]
    public void FileDefaultsOverrideBuiltInsAndTheJobOverridesBoth()
    {
        var defaults = new JobSettings { Rotate = 14, CompressType = CompressType.Gzip };

        SettingsMerge.Resolve(Job(), defaults).Rotate.ShouldBe(14);
        SettingsMerge.Resolve(Job(new JobSettings { Rotate = 3 }), defaults).Rotate.ShouldBe(3);
        SettingsMerge.Resolve(Job(), defaults).CompressType.ShouldBe(CompressType.Gzip);
    }

    /// <summary>
    /// The reason every setting is nullable. With a plain bool, a job that never mentions
    /// compress would be indistinguishable from one that sets it false - so turning compression
    /// on globally and off for one job would be impossible to express.
    /// </summary>
    [Fact]
    public void UnsetIsDistinctFromSetToFalse()
    {
        var defaults = new JobSettings { Compress = true };

        SettingsMerge.Resolve(Job(), defaults).Compress.ShouldBeTrue();
        SettingsMerge.Resolve(Job(new JobSettings { Compress = false }), defaults).Compress.ShouldBeFalse();
    }

    // "compress = false" and "compresstype = none" must not be able to disagree.
    [Fact]
    public void CompressAndCompressTypeAreNormalisedToOneAnswer()
    {
        var off = SettingsMerge.Resolve(Job(new JobSettings { Compress = false }), null);
        off.CompressType.ShouldBe(CompressType.None);

        var none = SettingsMerge.Resolve(Job(new JobSettings { CompressType = CompressType.None }), null);
        none.Compress.ShouldBeFalse();
    }
}
