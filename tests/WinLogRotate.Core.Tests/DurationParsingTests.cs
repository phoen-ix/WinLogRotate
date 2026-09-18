using Shouldly;
using WinLogRotate.Core.Configuration;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// A duration the unit cannot hold is an unusable value, not a crash.
/// </summary>
/// <remarks>
/// <c>1e30h</c> parses as a double and then overflows <c>TimeSpan.FromHours</c>, which used to
/// leave the binder as an OverflowException: exit 4 and "a defect in the product" for a typo in
/// <c>remind_after</c> or <c>--run-deadline</c>.
/// </remarks>
public sealed class DurationParsingTests
{
    [Theory]
    [InlineData("1e30h")]
    [InlineData("1e300d")]
    [InlineData("Infinityh")]
    [InlineData("NaNm")]
    public void ACountTheUnitCannotHoldIsRefused(string text) =>
        Should.NotThrow(() => ConfigBinder.TryParseDuration(text, out _)).ShouldBeFalse();

    [Theory]
    [InlineData("7d", 7 * 24)]
    [InlineData("2h", 2)]
    public void AnOrdinaryDurationStillParses(string text, int hours)
    {
        ConfigBinder.TryParseDuration(text, out var value).ShouldBeTrue();
        value.ShouldBe(TimeSpan.FromHours(hours));
    }
}
