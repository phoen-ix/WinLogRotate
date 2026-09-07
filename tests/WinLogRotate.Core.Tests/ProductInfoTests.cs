using Shouldly;
using WinLogRotate.Core;
using Xunit;

namespace WinLogRotate.Core.Tests;

public class ProductInfoTests
{
    // This is the bug the reference project shipped in four consecutive releases: the SDK
    // stamps "1.4.0.0" into the assembly while the tag and the update feed say "1.4.0",
    // and System.Version ranks an absent component (-1) below 0. Its updater therefore
    // decided every freshly-unpacked exe was not the release it claimed to be, and refused
    // every portable self-update.
    [Theory]
    [InlineData("1.4.0", "1.4.0.0")]
    [InlineData("0.0.0", "0.0.0.0")]
    [InlineData("2.10.3", "2.10.3.0")]
    public void NormalizeTreatsMissingComponentsAsZero(string threePart, string fourPart)
    {
        var a = Version.Parse(threePart);
        var b = Version.Parse(fourPart);

        ProductInfo.SameVersion(a, b).ShouldBeTrue();
        ProductInfo.IsNewer(current: a, candidate: b).ShouldBeFalse();
        ProductInfo.IsNewer(current: b, candidate: a).ShouldBeFalse();
    }

    // The companion assertion: without Normalize, the comparison above is FALSE. Keeping
    // the broken behaviour pinned here means a future "simplification" back to a raw
    // comparison fails loudly instead of silently disabling self-update again.
    [Fact]
    public void RawVersionComparisonIsWrong_WhichIsWhyNormalizeExists()
    {
        Version.Parse("1.4.0").ShouldNotBe(Version.Parse("1.4.0.0"));
        (Version.Parse("1.4.0") < Version.Parse("1.4.0.0")).ShouldBeTrue();
    }

    [Theory]
    [InlineData("1.4.0", "1.4.1")]
    [InlineData("1.4.0", "1.5.0")]
    [InlineData("1.4.0", "2.0.0")]
    [InlineData("1.9.0", "1.10.0")]  // not a string comparison
    public void IsNewerDetectsAnUpgrade(string current, string candidate) =>
        ProductInfo.IsNewer(Version.Parse(current), Version.Parse(candidate)).ShouldBeTrue();

    [Fact]
    public void VersionIsThreePartAndParseable() =>
        Version.TryParse(ProductInfo.Version, out _).ShouldBeTrue();
}
