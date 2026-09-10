using Shouldly;
using WinLogRotate.Core.Io;
using WinLogRotate.Core.State;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// The failure no other Windows log rotator detects: a writer that caches its own file offset
/// resumes at it after truncation, NTFS zero-fills the gap, and the "rotated" file instantly
/// reappears at its old size - every rotation, forever, without ever raising an error.
/// </summary>
public class NulFillDetectorTests
{
    private const long FourGigabytes = 4L * 1024 * 1024 * 1024;

    [Fact]
    public void AFileThatComesStraightBackIsConfirmed()
    {
        var result = NulFillDetector.Judge(sizeBefore: FourGigabytes, sizeNow: FourGigabytes);

        result.Verdict.ShouldBe(NulFillVerdict.Confirmed);
        result.Explanation.ShouldNotBeNull().ShouldContain("cached offset");
    }

    [Fact]
    public void AFileThatResumedFromEmptyIsClean()
    {
        NulFillDetector.Judge(sizeBefore: FourGigabytes, sizeNow: 4096)
            .Verdict.ShouldBe(NulFillVerdict.Clean);
    }

    // Leading NULs are the signature itself, not an inference - a log that genuinely regrew
    // would start with log text. So this is conclusive even when the size looks unremarkable.
    [Fact]
    public void ALeadingNulRunIsConclusiveOnItsOwn()
    {
        var result = NulFillDetector.Judge(
            sizeBefore: FourGigabytes, sizeNow: 1024, nulRun: 4096);

        result.Verdict.ShouldBe(NulFillVerdict.Confirmed);
        result.Explanation.ShouldNotBeNull().ShouldContain("NUL bytes");
    }

    /// <summary>
    /// A small file with a NUL run is still the failure.
    /// </summary>
    /// <remarks>
    /// The size floor used to be asked first, so a 512 KB log carrying four thousand NUL bytes
    /// came back Unknown. The floor exists because proportional reasoning is noise below a
    /// megabyte - and a NUL run is not proportional reasoning.
    /// </remarks>
    [Fact]
    public void ANulRunIsConclusiveBelowTheSizeFloorToo()
    {
        NulFillDetector.Judge(sizeBefore: 512 * 1024, sizeNow: 512 * 1024, nulRun: 4096)
            .Verdict.ShouldBe(NulFillVerdict.Confirmed);
    }

    /// <summary>
    /// Which rule fired is reported, because the two are not interchangeable.
    /// </summary>
    /// <remarks>
    /// A NUL run means the same thing whenever it is seen. The size ratio is an inference whose
    /// force depends on how recently the truncation happened - one full interval later, a
    /// steadily-growing log returns to its old size for entirely innocent reasons. The caller
    /// cannot apply that distinction without being told which rule produced the verdict.
    /// </remarks>
    [Fact]
    public void TheEvidenceSaysWhichRuleFired()
    {
        NulFillDetector.Judge(FourGigabytes, FourGigabytes)
            .Evidence.ShouldBe(NulFillEvidence.ReGrowth);

        NulFillDetector.Judge(FourGigabytes, 1024, nulRun: 4096)
            .Evidence.ShouldBe(NulFillEvidence.NulSignature);
    }

    /// <summary>Not sampling is not the same as sampling and finding log text.</summary>
    /// <remarks>
    /// Zero used to mean both. A file that could not be opened would then be recorded as clean -
    /// a positive claim about the writer's behaviour, made on no evidence at all.
    /// </remarks>
    [Fact]
    public void AnUnsampledFileIsNotReportedAsHavingBeenLookedAt()
    {
        // Both resume from empty, so both are Clean. Only one of them is Clean on evidence.
        NulFillDetector.Judge(FourGigabytes, 1024, nulRun: null)
            .Evidence.ShouldBe(NulFillEvidence.None);

        NulFillDetector.Judge(FourGigabytes, 1024, nulRun: 0)
            .Evidence.ShouldBe(NulFillEvidence.NulSignature);
    }

    [Fact]
    public void SmallFilesAreNotJudgedAtAll()
    {
        // Proportional reasoning on a 100-byte file is noise, and a false positive here would
        // permanently disable copytruncate for a perfectly healthy path.
        NulFillDetector.Judge(sizeBefore: 100, sizeNow: 100)
            .Verdict.ShouldBe(NulFillVerdict.Unknown);
    }

    [Theory]
    [InlineData(1.00, NulFillVerdict.Confirmed)]
    [InlineData(0.95, NulFillVerdict.Confirmed)]
    [InlineData(0.90, NulFillVerdict.Confirmed)]
    [InlineData(0.50, NulFillVerdict.Clean)]
    [InlineData(0.01, NulFillVerdict.Clean)]
    public void TheThresholdIsProportional(double ratio, NulFillVerdict expected) =>
        NulFillDetector.Judge(FourGigabytes, (long)(FourGigabytes * ratio)).Verdict.ShouldBe(expected);

    [Fact]
    public void CountsLeadingNuls()
    {
        var bytes = new byte[1000];
        Array.Fill(bytes, (byte)0, 0, 600);
        Array.Fill(bytes, (byte)'x', 600, 400);

        using var stream = new MemoryStream(bytes);
        NulFillDetector.CountLeadingNuls(stream).ShouldBe(600);
    }

    [Fact]
    public void CountsZeroWhenTheFileStartsWithText()
    {
        using var stream = new MemoryStream("2026-09-07 INFO started"u8.ToArray());
        NulFillDetector.CountLeadingNuls(stream).ShouldBe(0);
    }
}
