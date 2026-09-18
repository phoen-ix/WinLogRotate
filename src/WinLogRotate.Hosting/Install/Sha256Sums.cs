using System.Diagnostics.CodeAnalysis;

namespace WinLogRotate.Hosting.Install;

/// <summary>
/// The contents of a release's <c>SHA256SUMS.txt</c>.
/// </summary>
/// <remarks>
/// <para>
/// The format is sha256sum's: sixty-four hex digits, two spaces, the file name; a <c>*</c>
/// before the name marks binary mode and is accepted too. Anything else on a non-blank line
/// is a problem, and the whole file is refused rather than the line skipped: a sums file that
/// cannot be read completely is not one a download should be trusted against.
/// </para>
/// <para>
/// A name listed twice with two different digests is refused for the same reason. Listed twice
/// with the same digest is merely redundant.
/// </para>
/// </remarks>
public sealed class Sha256Sums
{
    private readonly IReadOnlyDictionary<string, string> _digests;

    private Sha256Sums(IReadOnlyDictionary<string, string> digests) => _digests = digests;

    public int Count => _digests.Count;

    /// <summary>The recorded digest of a file, lower-case hex, or null if it is not listed.</summary>
    public string? DigestOf(string fileName) =>
        _digests.TryGetValue(fileName, out var digest) ? digest : null;

    /// <summary>
    /// Whether the digest recorded for a file equals the one computed.
    /// </summary>
    /// <remarks>
    /// False when the file is not listed at all. "Not listed" and "does not match" both mean
    /// the download must not be run, and the caller says which in its own words.
    /// </remarks>
    public bool Matches(string fileName, ReadOnlySpan<byte> digest) =>
        DigestOf(fileName) is { } recorded
        && string.Equals(recorded, Convert.ToHexStringLower(digest), StringComparison.Ordinal);

    public static bool TryParse(string text, [NotNullWhen(true)] out Sha256Sums? sums, [NotNullWhen(false)] out string? problem)
    {
        var digests = new Dictionary<string, string>(StringComparer.Ordinal);
        var number = 0;

        foreach (var raw in text.Split('\n'))
        {
            number++;
            var line = raw.Trim('\r', ' ', '\t', '﻿');

            if (line.Length == 0)
            {
                continue;
            }

            if (line.Length < 64 || !IsHex(line.AsSpan(0, 64)) || (line.Length > 64 && line[64] != ' '))
            {
                problem = $"line {number} does not start with a SHA-256 digest";
                sums = null;
                return false;
            }

            var name = line[64..].TrimStart(' ').TrimStart('*');

            if (name.Length == 0)
            {
                problem = $"line {number} names no file";
                sums = null;
                return false;
            }

            var digest = line[..64].ToLowerInvariant();

            if (digests.TryGetValue(name, out var earlier) && !string.Equals(earlier, digest, StringComparison.Ordinal))
            {
                problem = $"'{name}' is listed twice with different digests";
                sums = null;
                return false;
            }

            digests[name] = digest;
        }

        if (digests.Count == 0)
        {
            problem = "the file lists nothing";
            sums = null;
            return false;
        }

        sums = new Sha256Sums(digests);
        problem = null;
        return true;
    }

    private static bool IsHex(ReadOnlySpan<char> s)
    {
        foreach (var c in s)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
