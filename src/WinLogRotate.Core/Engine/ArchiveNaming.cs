using System.Globalization;
using WinLogRotate.Core.Compression;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Safety;

namespace WinLogRotate.Core.Engine;

/// <summary>Builds the names a rotate job writes and looks for.</summary>
public static class ArchiveNaming
{
    /// <summary>
    /// The name the live log is renamed to.
    /// </summary>
    /// <remarks>
    /// Deliberately without any compression extension. The rename produces an uncompressed
    /// file; a separate compress step then appends the extension, which is also why
    /// <c>delaycompress</c> needs nothing special here - it simply omits that later step, and
    /// the newest generation stays uncompressed for one cycle so a writer still holding the
    /// renamed file is not compressed out from under it.
    /// </remarks>
    public static string FirstRotation(EffectiveJob job, string logPath, DateTimeOffset now)
    {
        var directory = ResolveDirectory(job, logPath);
        var name = WinPath.FileName(logPath);

        var suffix = job.DateExt
            ? now.ToString(job.DateFormat, CultureInfo.InvariantCulture)
            : "." + job.Start.ToString(CultureInfo.InvariantCulture);

        return WinPath.Combine(directory, name + suffix);
    }

    /// <summary>The name at a given rotation index.</summary>
    public static string Numbered(EffectiveJob job, string logPath, int index, bool compressed)
    {
        var directory = ResolveDirectory(job, logPath);
        var name = WinPath.FileName(logPath);
        var extension = compressed ? Compressor.Extension(job.CompressType) : string.Empty;

        return WinPath.Combine(
            directory, $"{name}.{index.ToString(CultureInfo.InvariantCulture)}{extension}");
    }

    /// <summary>
    /// Where archives live: <c>olddir</c> if set, otherwise beside the log.
    /// </summary>
    /// <remarks>
    /// A relative <c>olddir</c> resolves against the log's own directory, matching logrotate.
    /// </remarks>
    public static string ResolveDirectory(EffectiveJob job, string logPath)
    {
        var logDirectory = WinPath.DirectoryName(logPath);

        if (job.OldDir is not { Length: > 0 } oldDir)
        {
            return logDirectory;
        }

        var normalized = WinPath.Normalize(oldDir);
        return WinPath.IsAbsolute(normalized)
            ? normalized
            : WinPath.Combine(logDirectory, normalized);
    }

    /// <summary>
    /// A glob that finds the archives a dateext job has written.
    /// </summary>
    /// <remarks>
    /// Derived from the date format by replacing each format specifier with a digit class, so
    /// the pattern needs the character classes Win32 wildcards do not have - one of the reasons
    /// the matcher is ours. Note the known upstream hazard this inherits: changing
    /// <c>dateformat</c> between runs leaves the older archives unmatched by the new pattern,
    /// so they are never counted and never deleted. <c>config check</c> warns about it.
    /// </remarks>
    public static string DateExtGlob(EffectiveJob job, string logPath)
    {
        var directory = ResolveDirectory(job, logPath);
        var name = WinPath.FileName(logPath);

        var pattern = new System.Text.StringBuilder();
        var i = 0;
        while (i < job.DateFormat.Length)
        {
            var c = job.DateFormat[i];
            if (c is 'y' or 'M' or 'd' or 'H' or 'm' or 's')
            {
                var run = 0;
                while (i + run < job.DateFormat.Length && job.DateFormat[i + run] == c)
                {
                    run++;
                }

                // One digit class per character of the specifier: "yyyy" becomes four.
                for (var r = 0; r < run; r++)
                {
                    pattern.Append("[0-9]");
                }

                i += run;
                continue;
            }

            pattern.Append(c);
            i++;
        }

        // Ends with a bare "*" rather than the compression extension, so it matches both the
        // compressed archives and the one delaycompress deliberately left uncompressed.
        // Retention has to see every archive, not just the tidy ones.
        return WinPath.Combine(directory, $"{name}{pattern}*")
            .Replace("**", "*", StringComparison.Ordinal);
    }
}
