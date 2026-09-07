namespace WinLogRotate.Core.Journaling;

/// <summary>
/// Generates the identifier that groups every event of one invocation.
/// <para>
/// Lexicographically sortable and time-prefixed, so journal entries sort into run order by
/// string comparison alone and the GUI can group a run without timestamp heuristics.
/// </para>
/// </summary>
public static class RunId
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";  // Crockford base32

    /// <summary>Creates an identifier from a timestamp plus randomness.</summary>
    public static string New(TimeProvider clock)
    {
        var ms = clock.GetUtcNow().ToUnixTimeMilliseconds();
        Span<char> buffer = stackalloc char[26];

        // 48 bits of timestamp, most significant first.
        for (var i = 9; i >= 0; i--)
        {
            buffer[i] = Alphabet[(int)(ms & 31)];
            ms >>= 5;
        }

        Span<byte> entropy = stackalloc byte[16];
        System.Security.Cryptography.RandomNumberGenerator.Fill(entropy);
        for (var i = 0; i < 16; i++)
        {
            buffer[10 + i] = Alphabet[entropy[i] & 31];
        }

        return new string(buffer);
    }
}
