using System.Diagnostics.CodeAnalysis;

namespace WinLogRotate.Core.Safety;

/// <summary>Why a path was rejected before anything touched the filesystem.</summary>
public enum PathProblem
{
    None,

    /// <summary>Empty or whitespace.</summary>
    Empty,

    /// <summary>Relative, or drive-relative like <c>C:foo</c> - which resolves against a
    /// per-drive current directory that a service does not meaningfully have.</summary>
    NotAbsolute,

    /// <summary>Contains a DOS device name (<c>CON</c>, <c>NUL</c>, <c>COM1</c>...), which
    /// Windows resolves to a device however the path around it looks.</summary>
    ReservedName,

    /// <summary>A segment ends in a dot or space. Win32 silently strips those, so the path
    /// you asked for and the file you get are different - and <c>\\?\</c> paths do not strip,
    /// so the two APIs disagree about the same string.</summary>
    TrailingDotOrSpace,

    /// <summary>Names an NTFS alternate data stream. A colon is not always a drive letter.</summary>
    AlternateDataStream,

    /// <summary>Contains a character Win32 forbids.</summary>
    InvalidCharacter,

    /// <summary>Contains <c>..</c>, which we refuse rather than resolve: a pattern that walks
    /// upward past its own root is almost always a mistake, and resolving it silently is how a
    /// rotation ends up outside the directory the operator was looking at.</summary>
    ParentTraversal,
}

/// <summary>
/// Windows path normalization and validation, kept free of any filesystem access so it can be
/// exhaustively tested anywhere.
/// </summary>
public static class WinPath
{
    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    // '*' and '?' are legal in a PATTERN and illegal in a path, so they are checked separately
    // by the caller rather than listed here.
    private static readonly char[] InvalidChars = ['<', '>', '"', '|'];

    /// <summary>
    /// Normalizes separators and casing without touching the disk.
    /// Accepts either separator - operators paste Linux-style paths - and emits backslashes.
    /// </summary>
    public static string Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var trimmed = path.Trim();
        var normalized = trimmed.Replace('/', '\\');

        // Collapse repeated separators, but preserve a leading \\ for UNC.
        var unc = normalized.StartsWith(@"\\", StringComparison.Ordinal);
        while (normalized.Contains(@"\\", StringComparison.Ordinal))
        {
            normalized = normalized.Replace(@"\\", @"\", StringComparison.Ordinal);
        }

        if (unc)
        {
            normalized = @"\" + normalized;
        }

        // A trailing separator makes "C:\logs\" and "C:\logs" different strings for the same
        // directory, which would give them two state entries and two rotation clocks.
        if (normalized.Length > 3 && normalized.EndsWith('\\'))
        {
            normalized = normalized.TrimEnd('\\');
        }

        return normalized;
    }

    /// <summary>
    /// The key a path is stored under in state and compared by for duplicate detection.
    /// <para>
    /// Without this, <c>C:\Logs\App.log</c> and <c>c:\logs\app.log</c> become two entries with
    /// independent rotation clocks, and the same log rotates twice as often as configured.
    /// Uppercased with <see cref="StringComparison.OrdinalIgnoreCase"/> semantics - never
    /// InvariantCulture, because a Turkish locale maps 'I' to a dotless 'ı' and two paths that
    /// are the same file stop comparing equal.
    /// </para>
    /// </summary>
    public static string CanonicalKey(string path) =>
        Normalize(path).ToUpperInvariant();

    /// <summary>Validates a concrete path (no wildcards expected).</summary>
    public static PathProblem Validate(string path) => Validate(path, allowWildcards: false);

    /// <summary>Validates a path or, with <paramref name="allowWildcards"/>, a glob pattern.</summary>
    public static PathProblem Validate(string path, bool allowWildcards)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return PathProblem.Empty;
        }

        var normalized = Normalize(path);

        if (!IsAbsolute(normalized))
        {
            return PathProblem.NotAbsolute;
        }

        var body = StripPrefix(normalized, out var isUnc);
        var segments = body.Split('\\', StringSplitOptions.RemoveEmptyEntries);

        // Where the ADS check may begin. On a local path segment 0 is the drive ("C:"), which
        // contains a colon and is not a stream; on a UNC path segments 0 and 1 are the server
        // and share. Getting this index wrong rejects every ordinary path as a stream.
        var firstStreamCheckable = isUnc ? 2 : 1;

        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];

            if (i >= firstStreamCheckable && segment.Contains(':'))
            {
                return PathProblem.AlternateDataStream;
            }

            if (segment is "..")
            {
                return PathProblem.ParentTraversal;
            }

            if (segment.Length > 0 && (segment.EndsWith('.') || segment.EndsWith(' ')) && segment != ".")
            {
                return PathProblem.TrailingDotOrSpace;
            }

            if (segment.IndexOfAny(InvalidChars) >= 0)
            {
                return PathProblem.InvalidCharacter;
            }

            if (!allowWildcards && segment.IndexOfAny(['*', '?']) >= 0)
            {
                return PathProblem.InvalidCharacter;
            }

            if (IsReserved(segment))
            {
                return PathProblem.ReservedName;
            }
        }

        return PathProblem.None;
    }

    /// <summary>A DOS device name, with or without an extension - <c>NUL</c> and
    /// <c>NUL.log</c> both open the null device.</summary>
    public static bool IsReserved(string segment)
    {
        var dot = segment.IndexOf('.');
        var stem = dot < 0 ? segment : segment[..dot];
        return ReservedNames.Contains(stem.TrimEnd(' '), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>True for <c>C:\...</c>, <c>\\server\share\...</c> and <c>\\?\...</c>.
    /// Deliberately false for <c>C:foo</c>.</summary>
    public static bool IsAbsolute([NotNullWhen(true)] string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var p = path.Replace('/', '\\');

        if (p.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return true;
        }

        return p.Length >= 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && p[2] == '\\';
    }

    /// <summary>True when the path is nothing but a drive or share root.</summary>
    public static bool IsRoot(string path)
    {
        var p = Normalize(path);

        if (p.Length is 2 or 3 && char.IsAsciiLetter(p[0]) && p[1] == ':')
        {
            return true;
        }

        if (p.StartsWith(@"\\", StringComparison.Ordinal))
        {
            // \\server\share and nothing below it.
            return p.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries).Length <= 2;
        }

        return false;
    }

    /// <summary>
    /// Prefixes with <c>\\?\</c> for Win32 calls, which lifts the 260-character limit.
    /// <para>
    /// The prefix disables all normalization, so the path must already be absolute and clean -
    /// no <c>..</c>, no forward slashes, no trailing dots. That is exactly what
    /// <see cref="Validate(string)"/> guarantees, which is why this takes a normalized path.
    /// </para>
    /// </summary>
    public static string ToExtendedLength(string normalizedAbsolutePath)
    {
        var p = normalizedAbsolutePath;

        if (p.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return p;
        }

        return p.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC" + p[1..]
            : @"\\?\" + p;
    }

    private static string StripPrefix(string normalized, out bool isUnc)
    {
        var p = normalized;
        isUnc = false;

        if (p.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
        {
            isUnc = true;
            return p[8..];
        }

        if (p.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return p[4..];
        }

        if (p.StartsWith(@"\\", StringComparison.Ordinal))
        {
            isUnc = true;
            return p[2..];
        }

        return p;
    }
}
