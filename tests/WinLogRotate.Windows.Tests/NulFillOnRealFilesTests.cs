using Shouldly;
using WinLogRotate.Core.Io;
using WinLogRotate.Core.State;
using Xunit;

namespace WinLogRotate.Windows.Tests;

/// <summary>
/// The failure the product is named for, reproduced on real NTFS.
/// </summary>
/// <remarks>
/// <para>
/// A writer that caches its own file offset does not seek back to zero when we truncate underneath
/// it. Its next write lands at the offset it remembers and NTFS zero-fills the gap, producing a
/// file of NUL bytes plus one line of log - which reappears at its old size every rotation,
/// forever. log4net and most C++ <c>std::ofstream</c> implementations do exactly this, and a
/// <c>FileStream</c>'s cached position is a faithful stand-in.
/// </para>
/// <para>
/// <c>FileMode.Append</c> would prove nothing: <c>FILE_APPEND_DATA</c> always writes at the end of
/// the file, which is the healthy case.
/// </para>
/// </remarks>
public sealed class NulFillOnRealFilesTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-nulfill-");

    /// <summary>Above <c>NulFillDetector.MinimumInterestingSize</c>, and quick to write.</summary>
    private const int LogSize = 2 << 20;

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// A cached-offset writer really does produce a NUL-filled file.
    /// </summary>
    /// <remarks>
    /// The environmental precondition, asserted on its own so that a change in NTFS behaviour is
    /// diagnosable rather than looking like our detector regressing.
    /// </remarks>
    [Fact]
    public void AWriterThatCachesItsOffsetReallyProducesANulFilledFile()
    {
        WindowsOnly.Require();

        var path = Path.Combine(_dir.FullName, "app.log");
        File.WriteAllBytes(path, new byte[LogSize]);

        using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        writer.Position = LogSize;

        FileOps.CopyTruncate(path, Path.Combine(_dir.FullName, "app.log.1"), truncate: true);

        // The rotation just emptied the file. This writer has no idea, and resumes where it was.
        writer.Write("resumed at a cached offset\r\n"u8);
        writer.Flush();

        new FileInfo(path).Length.ShouldBeGreaterThan(LogSize - 1, "NTFS zero-filled the gap");
    }

    /// <summary>
    /// And the whole chain reaches a confirmed verdict on it.
    /// </summary>
    /// <remarks>
    /// The end-to-end proof: a real truncation, a real misbehaving writer, a real sample taken at
    /// the offset the truncation left, and the verdict that quarantines the path. Every link of
    /// this was written, tested and connected to nothing.
    /// </remarks>
    [Fact]
    public void TheWholeChainConfirmsARealNulFill()
    {
        WindowsOnly.Require();

        var path = Path.Combine(_dir.FullName, "app.log");
        File.WriteAllBytes(path, new byte[LogSize]);

        using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        writer.Position = LogSize;

        var outcome = FileOps.CopyTruncate(path, Path.Combine(_dir.FullName, "app.log.1"), truncate: true);

        writer.Write("resumed at a cached offset\r\n"u8);
        writer.Flush();

        // Sampled where the truncation left the file, not at byte zero - tail preservation means
        // a truncated log usually begins with log text.
        var sample = new WriterInspector().Sample(path, outcome.SizeAfter);

        sample.Opened.ShouldBeTrue();
        sample.NulRun.ShouldBeGreaterThanOrEqualTo(512);

        var judged = NulFillDetector.Judge(outcome.SizeBefore, sample.Size, sample.NulRunOrNull);

        judged.Verdict.ShouldBe(NulFillVerdict.Confirmed);
        judged.Evidence.ShouldBe(NulFillEvidence.NulSignature);
    }

    /// <summary>A writer that honours truncation is not quarantined.</summary>
    /// <remarks>
    /// The other side of the same reproduction, and the one that matters for false positives: an
    /// append handle resumes at zero, so the bytes at the resume offset are log text.
    /// </remarks>
    [Fact]
    public void AWriterThatHonoursTruncationIsFoundClean()
    {
        WindowsOnly.Require();

        var path = Path.Combine(_dir.FullName, "app.log");
        File.WriteAllBytes(path, new byte[LogSize]);

        var outcome = FileOps.CopyTruncate(path, Path.Combine(_dir.FullName, "app.log.1"), truncate: true);

        // FILE_APPEND_DATA always writes at the end, which is what a healthy logger does.
        using (var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            writer.Write("resumed from empty\r\n"u8);
        }

        var sample = new WriterInspector().Sample(path, outcome.SizeAfter);

        sample.NulRun.ShouldBe(0);
        NulFillDetector.Judge(outcome.SizeBefore, sample.Size, sample.NulRunOrNull)
            .Verdict.ShouldBe(NulFillVerdict.Clean);
    }
}
