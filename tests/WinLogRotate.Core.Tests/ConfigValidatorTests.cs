using Shouldly;
using WinLogRotate.Contracts;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Safety;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// The values the validator refuses because the run cannot survive them.
/// </summary>
/// <remarks>
/// Each of these was accepted at config time and met later, where the code that met it threw
/// something no catch filter names - so the whole run ended at exit 4, or in the one case of a
/// negative sleep, did not end at all. A config-time error costs one message; the alternative
/// cost every job on the machine.
/// </remarks>
public sealed class ConfigValidatorTests
{
    private static DiagnosticBag Validate(EffectiveJob job)
    {
        var bag = new DiagnosticBag();
        ConfigValidator.Validate(job, new PathGuard(new GuardOptions { ProtectedRoots = [] }), bag);
        return bag;
    }

    private static IEnumerable<ConfigDiagnostic> Errors(DiagnosticBag bag) =>
        bag.Items.Where(d => d.Severity == Severity.Error);

    /// <summary>
    /// A retry count below one would throw out of RetryPolicy on the first file operation, and a
    /// negative interval is either a sleep for ever or an argument exception.
    /// </summary>
    [Theory]
    [InlineData(0, 100, "retrycount")]
    [InlineData(-1, 100, "retrycount")]
    [InlineData(5, -1, "retryinterval")]
    [InlineData(5, -500, "retryinterval")]
    public void ARetrySettingTheRunCannotSurviveIsRefused(int count, int intervalMs, string key)
    {
        var bag = Validate(Fixtures.Job() with { RetryCount = count, RetryIntervalMs = intervalMs });

        Errors(bag).ShouldContain(d => d.Message.StartsWith(key, StringComparison.Ordinal));
    }

    [Fact]
    public void OneAttemptAndNoWaitAreLegitimate()
    {
        var bag = Validate(Fixtures.Job() with { RetryCount = 1, RetryIntervalMs = 0 });

        Errors(bag).ShouldBeEmpty();
    }

    /// <summary>maxfiles below one refuses every pattern; a negative start names archives nothing can find.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public void AMatchCeilingBelowOneIsRefused(int maxFiles)
    {
        var bag = Validate(Fixtures.Job() with { MaxFiles = maxFiles });

        Errors(bag).ShouldContain(d => d.Message.StartsWith("maxfiles", StringComparison.Ordinal));
    }

    [Fact]
    public void ANegativeStartIndexIsRefused()
    {
        var bag = Validate(Fixtures.Job() with { Start = -1 });

        Errors(bag).ShouldContain(d => d.Message.StartsWith("start", StringComparison.Ordinal));
    }

    [Fact]
    public void ZeroIsAStartIndex()
    {
        Errors(Validate(Fixtures.Job() with { Start = 0 })).ShouldBeEmpty();
    }

    /// <summary>A negative age is refused.</summary>
    /// <remarks>
    /// maxage is a cutoff of now minus that many days, so -1 puts it tomorrow and every archive is
    /// older than that: <c>maxage = -1</c> deleted everything a job had kept, and config check
    /// said nothing. It reads like <c>rotate = -1</c>, which keeps everything - the opposite. A
    /// negative minage only ever lets a rotation through, and is refused so the two read alike.
    /// </remarks>
    [Theory]
    [InlineData("maxage")]
    [InlineData("minage")]
    public void ANegativeAgeIsRefused(string key)
    {
        var job = key == "maxage" ? Fixtures.Job() with { MaxAge = -1 } : Fixtures.Job() with { MinAge = -1 };

        Errors(Validate(job)).ShouldContain(d => d.Message.StartsWith(key, StringComparison.Ordinal));
    }

    [Fact]
    public void ZeroIsAnAge()
    {
        Errors(Validate(Fixtures.Job() with { MaxAge = 0, MinAge = 0 })).ShouldBeEmpty();
    }

    /// <summary>
    /// A dateformat the engine could not format, or could not find again, is refused.
    /// </summary>
    /// <remarks>
    /// <c>-%Y%m%d</c> is what a logrotate user writes first; <c>d</c> is a .NET standard pattern
    /// full of slashes; a slash or a colon cannot be in a file name; <c>MMM</c> is a month's name.
    /// Every one was accepted, and the first two took the run down at plan time.
    /// </remarks>
    [Theory]
    [InlineData("-%Y%m%d", "strftime")]
    [InlineData("d", "too short")]
    [InlineData("", "too short")]
    [InlineData("-yyyy/MM/dd", "'/'")]
    [InlineData("-yyyyMMddTHHmmss", "'T'")]
    [InlineData("-HH:mm", "':'")]
    [InlineData("-dd-MMM-yyyy", "as a name")]
    [InlineData("-dddd", "as a name")]
    public void ADateFormatThatCannotBeFoundAgainIsRefused(string format, string saying)
    {
        var bag = Validate(Fixtures.Job() with { DateExt = true, DateFormat = format });

        Errors(bag).ShouldContain(d => d.Message.StartsWith("dateformat", StringComparison.Ordinal)
                                       && d.Message.Contains(saying, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("-yyyyMMdd")]
    [InlineData("-yyyy.MM.dd")]
    [InlineData("_yyyy-MM-dd_HHmmss")]
    [InlineData("-yyyyMMddHH")]
    public void ADateFormatMadeOfDigitsAndSeparatorsIsAccepted(string format)
    {
        var bag = Validate(Fixtures.Job() with { DateExt = true, DateFormat = format });

        Errors(bag).ShouldNotContain(d => d.Message.StartsWith("dateformat", StringComparison.Ordinal));
    }

    /// <summary>The date-format judgement can be asked without a diagnostic bag, and answers the same.</summary>
    [Fact]
    public void TheDateFormatJudgementIsPure()
    {
        ConfigValidator.DateFormatProblem("-yyyyMMdd").ShouldBeNull();

        var problem = ConfigValidator.DateFormatProblem("-%Y%m%d").ShouldNotBeNull();
        problem.Message.ShouldContain("strftime", Case.Sensitive);
        problem.Remedy.ShouldContain("-yyyyMMdd", Case.Sensitive);

        var bag = Validate(Fixtures.Job() with { DateExt = true, DateFormat = "-%Y%m%d" });
        bag.Items.ShouldContain(d => d.Message == problem.Message && d.Remedy == problem.Remedy);
    }
}
