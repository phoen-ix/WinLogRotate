using System.Buffers.Binary;

namespace WinLogRotate.Core.Secrets;

/// <summary>
/// The wire format for handing one credential down a pipe.
/// </summary>
/// <remarks>
/// <para>
/// In Core because three assemblies need it and only Core is referenced by all of them: the GUI
/// writes it, the CLI reads it, and the pipe server that carries it lives in Hosting. It was
/// briefly written out twice - once on each end - which is a wire format with two definitions and
/// therefore a wire format that will eventually disagree with itself, silently, in the one code
/// path nobody can test on a build server.
/// </para>
/// <para>
/// The payload is <see cref="PlaintextPadding"/>'s, already length-prefixed, already padded to a
/// fixed boundary, and already hardened against the integer overflow a hostile length prefix
/// would otherwise cause. The outer prefix exists so a reader knows how much to expect before it
/// has seen any of it.
/// </para>
/// </remarks>
public static class SecretFrame
{
    public const int PrefixBytes = 4;

    /// <summary>Refused above this, before anything is allocated for it.</summary>
    public const int MaxPayloadBytes = 64 * 1024;

    public static byte[] Wrap(string value) => Wrap(value.AsSpan());

    /// <summary>For a caller holding characters it intends to zero, rather than a string.</summary>
    public static byte[] Wrap(ReadOnlySpan<char> value)
    {
        var payload = PlaintextPadding.Wrap(value);
        var message = new byte[PrefixBytes + payload.Length];

        BinaryPrimitives.WriteInt32LittleEndian(message, payload.Length);
        payload.CopyTo(message, PrefixBytes);

        return message;
    }

    /// <summary>
    /// How many payload bytes follow, or -1 if the prefix is not plausible.
    /// </summary>
    /// <remarks>
    /// Validated before it is used to size anything. This number arrives from another process; the
    /// pipe's descriptor is what makes that process trustworthy, and the prefix is not.
    /// </remarks>
    public static int PayloadLength(ReadOnlySpan<byte> prefix)
    {
        if (prefix.Length < PrefixBytes)
        {
            return -1;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);

        return length > 0 && length <= MaxPayloadBytes && length % 64 == 0 ? length : -1;
    }
}
