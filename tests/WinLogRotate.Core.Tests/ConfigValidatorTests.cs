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
}
