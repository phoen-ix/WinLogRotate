using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace WinLogRotate.Core.Secrets;

/// <summary>
/// Wraps a secret so its ciphertext length says nothing useful about it.
/// </summary>
/// <remarks>
/// Per-entry encryption means the file records a length per entry, and a length is a hint: it
/// separates a six-character password from a forty-character API token, and it makes a changed
/// secret visible as a changed size. Padding to a fixed boundary removes the cheap part of that
/// signal. It is not a serious defence and is not offered as one - the file's permissions are -
/// but it costs a few bytes and one function.
/// </remarks>
public static class PlaintextPadding
{
    /// <summary>Everything is padded up to a multiple of this many bytes.</summary>
    private const int Boundary = 64;

    private const int LengthPrefixBytes = 4;

    /// <summary>Length-prefixed UTF-8, padded to <see cref="Boundary"/> with random bytes.</summary>
    public static byte[] Wrap(string value) => Wrap(value.AsSpan());

    /// <summary>
    /// The same, for a caller holding characters rather than a string.
    /// </summary>
    /// <remarks>
    /// The GUI reads a credential out of a text box into a <c>char[]</c> it can zero afterwards.
    /// Routing it through a <c>string</c> to get here would undo that: the string would sit in the
    /// managed heap until a collection that may never come, and land in any crash dump taken in
    /// between.
    /// </remarks>
    public static byte[] Wrap(ReadOnlySpan<char> value)
    {
        // Sized then filled, rather than GetBytes(char[]): the span overload never materialises
        // an intermediate array holding the credential.
        var utf8 = new byte[Encoding.UTF8.GetByteCount(value)];
        Encoding.UTF8.GetBytes(value, utf8);

        var needed = LengthPrefixBytes + utf8.Length;
        var total = ((needed + Boundary - 1) / Boundary) * Boundary;

        var buffer = new byte[total];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, utf8.Length);
        utf8.CopyTo(buffer, LengthPrefixBytes);

        // Random rather than zero: a run of zeroes at a predictable offset is a known-plaintext
        // gift, and random costs the same.
        RandomNumberGenerator.Fill(buffer.AsSpan(needed));
        return buffer;
    }

    /// <summary>
    /// Unwraps what <see cref="Wrap(ReadOnlySpan{char})"/> produced. False for anything else.
    /// </summary>
    /// <remarks>
    /// Returning false rather than throwing, and never echoing what it saw: this runs on bytes
    /// that just came out of a decryption, and a failure here means the ciphertext was not ours.
    /// An exception message quoting the buffer would put fragments of whatever it actually
    /// decrypted to into a log.
    /// </remarks>
    public static bool TryUnwrap(ReadOnlySpan<byte> padded, out string value)
    {
        value = string.Empty;

        if (padded.Length < LengthPrefixBytes || padded.Length % Boundary != 0)
        {
            return false;
        }

        // Subtract rather than add: this length prefix came out of a file, so it can be
        // anything at all, and "LengthPrefixBytes + length" overflows to a negative number for
        // a large one - which passes the bounds check and then throws inside Slice.
        var length = BinaryPrimitives.ReadInt32LittleEndian(padded);
        if (length < 0 || length > padded.Length - LengthPrefixBytes)
        {
            return false;
        }

        value = Encoding.UTF8.GetString(padded.Slice(LengthPrefixBytes, length));
        return true;
    }
}
