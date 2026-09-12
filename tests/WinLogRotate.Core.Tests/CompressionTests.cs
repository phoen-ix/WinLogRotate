using System.IO.Compression;
using Shouldly;
using WinLogRotate.Core.Compression;
using WinLogRotate.Core.Configuration;
using Xunit;

namespace WinLogRotate.Core.Tests;

public sealed class CompressionTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-zip-");

    public void Dispose()
    {
        try
        {
            _dir.Delete(recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private string Seed(string name, string content)
    {
        var path = Path.Combine(_dir.FullName, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Theory]
    [InlineData(CompressType.Gzip, ".gz")]
    [InlineData(CompressType.Zip, ".zip")]
    public void CompressesAndRemovesTheOriginal(CompressType type, string extension)
    {
        var source = Seed("app.log.1", new string('x', 100_000));

        var result = Compressor.Compress(source, type);

        result.Destination.ShouldEndWith(extension);
        File.Exists(result.Destination).ShouldBeTrue();
        File.Exists(source).ShouldBeFalse();
        result.BytesAfter.ShouldBeLessThan(result.BytesBefore);
    }

    [Fact]
    public void GzipOutputIsReadableByAStandardDecompressor()
    {
        var content = string.Join('\n', Enumerable.Range(0, 500).Select(i => $"2026-09-07 line {i}"));
        var source = Seed("app.log.1", content);

        var result = Compressor.Compress(source, CompressType.Gzip);

        using var file = File.OpenRead(result.Destination);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        reader.ReadToEnd().ShouldBe(content);
    }

    /// <summary>
    /// .NET's GZipStream writes no FNAME, so gzip -l reports "unknown" and gunzip -N cannot
    /// restore the name. Writing the header by hand costs about forty lines and keeps the
    /// archive self-describing.
    /// </summary>
    [Fact]
    public void GzipHeaderCarriesTheOriginalNameAndTimestamp()
    {
        var source = Seed("app.log.1", "hello");
        var modified = new DateTime(2026, 9, 7, 3, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, modified);

        var result = Compressor.Compress(source, CompressType.Gzip);
        var bytes = File.ReadAllBytes(result.Destination);

        bytes[0].ShouldBe((byte)0x1f);
        bytes[1].ShouldBe((byte)0x8b);
        bytes[2].ShouldBe((byte)8);
        (bytes[3] & 0x08).ShouldBe(0x08, "the FNAME flag must be set");

        var mtime = BitConverter.ToUInt32(bytes, 4);
        mtime.ShouldBe((uint)new DateTimeOffset(modified).ToUnixTimeSeconds());

        var name = System.Text.Encoding.Latin1.GetString(bytes[10..].TakeWhile(b => b != 0).ToArray());
        name.ShouldBe("app.log.1");
    }

    /// <summary>
    /// Age-based retention reads mtime. Stamping "now" on the archive would make every file
    /// look brand new and silently defeat maxage.
    /// </summary>
    [Fact]
    public void TheArchiveKeepsTheLogsOwnTimestamp()
    {
        var source = Seed("app.log.1", "content");
        var modified = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, modified);

        var result = Compressor.Compress(source, CompressType.Zip);

        File.GetLastWriteTimeUtc(result.Destination).ShouldBe(modified, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void ZipContainsExactlyOneEntryNamedAfterTheLog()
    {
        var source = Seed("app.log.1", "content");

        var result = Compressor.Compress(source, CompressType.Zip);

        using var archive = ZipFile.OpenRead(result.Destination);
        archive.Entries.ShouldHaveSingleItem().Name.ShouldBe("app.log.1");
    }

    [Fact]
    public void NoTemporaryFileIsLeftBehind()
    {
        var source = Seed("app.log.1", "content");
        var result = Compressor.Compress(source, CompressType.Gzip);

        File.Exists(result.Destination + ".tmp").ShouldBeFalse();
        Directory.GetFiles(_dir.FullName).ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData("-1", CompressionLevel.Fastest)]
    [InlineData("--best", CompressionLevel.SmallestSize)]
    [InlineData("-6", CompressionLevel.Optimal)]
    [InlineData(null, CompressionLevel.Optimal)]
    [InlineData("nonsense", CompressionLevel.Optimal)]
    public void LogrotateCompressionOptionsMapToDotNetLevels(string? option, CompressionLevel expected) =>
        Compressor.MapLevel(option).ShouldBe(expected);
    /// <summary>
    /// A compression that fails leaves no staging file behind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing in this product ever collects one. Discovery probes the exact names a job would
    /// have written, and <c>LogSeriesTests</c> pins that a <c>.tmp</c> is deliberately not among
    /// them - so an orphan sits in the log directory for ever, in the one tool whose job is to
    /// stop log directories filling up.
    /// </para>
    /// <para>
    /// The failure has to land after the staging file exists, or there is nothing to clean up and
    /// this proves nothing - the first version of this test blocked the staging path itself and
    /// was green against the unfixed code. A directory where the archive must go fails the move
    /// instead, which is the last step and reaches it with the staging file written.
    /// </para>
    /// </remarks>
    [Fact]
    public void AFailedCompressionLeavesNoTemporaryBehind()
    {
        var source = Path.Combine(_dir.FullName, "app.log.1");
        File.WriteAllText(source, "something to compress");

        var length = new FileInfo(source).Length;

        Directory.CreateDirectory(source + ".zip");

        Should.Throw<Exception>(() => Compressor.Compress(
            source, CompressType.Zip, retryCount: 1, retryIntervalMs: 1));

        // Controls. The move is what failed, which means the staging file had been written and
        // stamped by then - without these, a compression that fell over before choosing a staging
        // name would satisfy the assertion below while proving nothing.
        Directory.Exists(source + ".zip").ShouldBeTrue("the obstruction must still be what it was");
        new FileInfo(source).Length.ShouldBe(length, "the source is only deleted after a success");

        File.Exists(source + ".zip.tmp").ShouldBeFalse(
            "a staging file nothing in the product ever collects was left in the log directory");
    }
}
