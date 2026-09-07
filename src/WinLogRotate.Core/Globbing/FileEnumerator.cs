using WinLogRotate.Core.Safety;

namespace WinLogRotate.Core.Globbing;

/// <summary>One file a pattern resolved to.</summary>
public sealed record MatchedFile
{
    public required string Path { get; init; }
    public required long Length { get; init; }
    public required DateTimeOffset LastWriteUtc { get; init; }
    public bool IsReparsePoint { get; init; }
}

/// <summary>
/// Resolves glob patterns against the filesystem.
/// </summary>
/// <remarks>
/// <para>
/// The OS is asked for a bare <c>*</c> and every name is matched by <see cref="Glob"/>. Letting
/// Windows do the matching would silently include 8.3 short-name hits - <c>*.log</c> also
/// matching <c>something.logfile</c> - which for a tool that deletes files is the difference
/// between doing what was asked and doing more.
/// </para>
/// <para>
/// <c>AttributesToSkip</c> is set to <c>None</c>. The .NET default is
/// <c>Hidden | System</c>, which would silently skip any log file an application marked
/// hidden, and "some of your logs were quietly not rotated" is the worst possible failure mode
/// for this product.
/// </para>
/// </remarks>
public static class FileEnumerator
{
    public static IReadOnlyList<MatchedFile> Resolve(string pattern, bool followReparsePoints = false)
    {
        var normalized = WinPath.Normalize(pattern);
        var anchor = Glob.LiteralPrefix(normalized);

        if (string.IsNullOrEmpty(anchor) || !Directory.Exists(anchor))
        {
            return [];
        }

        var recurse = normalized.Contains("**", StringComparison.Ordinal);
        var results = new List<MatchedFile>();

        foreach (var path in Walk(anchor, recurse, followReparsePoints))
        {
            if (!Glob.IsMatch(path, normalized))
            {
                continue;
            }

            var info = new FileInfo(path);
            if (!info.Exists)
            {
                // Raced with something that deleted it between enumeration and stat. Skipping
                // is correct: we were going to act on a file that no longer exists.
                continue;
            }

            var isReparse = info.Attributes.HasFlag(FileAttributes.ReparsePoint);
            if (isReparse && !followReparsePoints)
            {
                continue;
            }

            results.Add(new MatchedFile
            {
                Path = path,
                Length = info.Length,
                LastWriteUtc = info.LastWriteTimeUtc,
                IsReparsePoint = isReparse,
            });
        }

        // Windows enumeration order is unspecified - NTFS returns roughly B-tree order, ReFS
        // and SMB differ. Retention decides which files die, so the order it sees must be
        // deterministic and must not depend on which filesystem the logs happen to live on.
        results.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return results;
    }

    /// <summary>
    /// Walks the tree ourselves rather than using <c>RecurseSubdirectories</c>.
    /// <para>
    /// This is the junction defence, and it is why the built-in recursion is unusable here:
    /// <see cref="EnumerationOptions"/> offers no way to decline to descend into a reparse
    /// point, so a recursive enumeration will happily follow a junction wherever it leads. Any
    /// user can create one with no privilege whatsoever (<c>mklink /J</c>), so a low-privileged
    /// user who controls a log directory could aim it at <c>System32</c> and have our
    /// SYSTEM-privileged retention pass delete files there. Walking by hand lets us test each
    /// directory before entering it.
    /// </para>
    /// </summary>
    private static IEnumerable<string> Walk(string root, bool recurse, bool followReparsePoints)
    {
        var options = new EnumerationOptions
        {
            // Ours, not Win32's - see the class remarks.
            MatchType = MatchType.Simple,

            // The .NET default hides Hidden and System files. A log we skip is a log that
            // grows forever.
            AttributesToSkip = FileAttributes.None,

            // A directory we cannot read must not abort the whole job.
            IgnoreInaccessible = true,

            RecurseSubdirectories = false,
        };

        var pending = new Stack<string>();
        pending.Push(root);

        // Guards against a junction cycle that a depth-first walk would otherwise follow
        // forever. Cheap insurance; the set is bounded by the directory count either way.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (pending.Count > 0)
        {
            var dir = pending.Pop();

            if (!seen.Add(WinPath.CanonicalKey(dir)))
            {
                continue;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir, "*", options);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or DirectoryNotFoundException)
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            if (!recurse)
            {
                continue;
            }

            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(dir, "*", options);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or DirectoryNotFoundException)
            {
                continue;
            }

            foreach (var child in children)
            {
                if (!followReparsePoints
                    && new DirectoryInfo(child).Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                pending.Push(child);
            }
        }
    }
}
