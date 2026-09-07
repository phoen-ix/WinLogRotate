using System.IO.Compression;
using System.Text;

namespace WinLogRotate.Core.Compression;

/// <summary>
/// Writes a proper gzip member, including the original filename and timestamp.
/// </summary>
/// <remarks>
/// <para>
/// .NET's <see cref="GZipStream"/> writes a minimal header with no FNAME and no MTIME, so
/// <c>gzip -l</c> reports the original name as unknown and <c>gunzip -N</c> cannot restore it.
/// For a rotated log that is a small but real loss of provenance, and it is about forty lines
/// to do properly: emit the ten-byte header ourselves with the fields filled in, then raw
/// DEFLATE, then the CRC-32 and length trailer.
/// </para>
/// <para>
/// Note that <see cref="DeflateStream"/> alone must never be written to a <c>.gz</c> file:
/// raw DEFLATE carries no header, and <c>gunzip</c> rejects it. That mistake is common enough
/// to be worth naming.
/// </para>
/// </remarks>
public static class GzipWriter
{
    private const byte MagicByte1 = 0x1f;
    private const byte MagicByte2 = 0x8b;
    private const byte DeflateMethod = 8;
    private const byte FlagName = 0x08;

    /// <summary>Compresses <paramref name="source"/> into <paramref name="destination"/>.</summary>
    /// <param name="originalName">Recorded in the header so gzip -l and gunzip -N work.</param>
    /// <param name="modified">Recorded in the header as the original modification time.</param>
    public static void Compress(
        Stream source, Stream destination, string originalName, DateTimeOffset modified,
        CompressionLevel level = CompressionLevel.Optimal)
    {
        WriteHeader(destination, originalName, modified);

        uint crc = 0;
        long length = 0;

        using (var deflate = new DeflateStream(destination, level, leaveOpen: true))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                crc = Crc32.Append(crc, buffer.AsSpan(0, read));
                length += read;
                deflate.Write(buffer, 0, read);
            }
        }

        Span<byte> trailer = stackalloc byte[8];
        BitConverter.TryWriteBytes(trailer, crc);
        BitConverter.TryWriteBytes(trailer[4..], (uint)(length & 0xFFFFFFFF));
        destination.Write(trailer);
    }

    private static void WriteHeader(Stream destination, string originalName, DateTimeOffset modified)
    {
        // gzip's MTIME is a 32-bit Unix timestamp; 0 means "not available", which is also what
        // it becomes for dates outside the representable range rather than writing nonsense.
        var seconds = modified.ToUnixTimeSeconds();
        var mtime = seconds is > 0 and <= uint.MaxValue ? (uint)seconds : 0u;

        var nameBytes = Encoding.Latin1.GetBytes(Path.GetFileName(originalName));

        Span<byte> header = stackalloc byte[10];
        header[0] = MagicByte1;
        header[1] = MagicByte2;
        header[2] = DeflateMethod;
        header[3] = FlagName;
        BitConverter.TryWriteBytes(header[4..], mtime);
        header[8] = 0;      // no extra flags
        header[9] = 0x00;   // OS: FAT, which is what .NET writes and what gzip-on-Windows uses
        destination.Write(header);

        destination.Write(nameBytes);
        destination.WriteByte(0);
    }
}

/// <summary>CRC-32 (IEEE), needed for the gzip trailer.</summary>
internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }

    public static uint Append(uint crc, ReadOnlySpan<byte> data)
    {
        var value = ~crc;
        foreach (var b in data)
        {
            value = Table[(value ^ b) & 0xFF] ^ (value >> 8);
        }

        return ~value;
    }
}
