using WinLogRotate.Core.Io;
using WinLogRotate.Core.Safety;

namespace WinLogRotate.Core.Globbing;

/// <summary>One file a pattern resolved to.</summary>
public sealed record MatchedFile
{
    /// <summary>
    /// The path as written, not as resolved.
    /// </summary>
    /// <remarks>
    /// Deliberately textual even where a link was followed to reach it. <c>StateStore</c> keys on
    /// this, so recording resolved paths would make every log behind a junction look new, baseline
    /// the lot, and reset the rotation clock on every installation that has one.
    /// </remarks>
    public required string Path { get; init; }

    public required long Length { get; init; }
    public required DateTimeOffset LastWriteUtc { get; init; }
}

/// <summary>What one pattern resolved to, and what it refused to look at.</summary>
public sealed record EnumerationResult
{
    public required IReadOnlyList<MatchedFile> Files { get; init; }

    /// <summary>Links not followed, each named. Never silently dropped.</summary>
    public required IReadOnlyList<GuardDecision> Refusals { get; init; }

    /// <summary>Where the anchor really led, when that differed from what was written.</summary>
    public string? ResolvedAnchor { get; init; }

    public static readonly EnumerationResult Nothing =
        new() { Files = [], Refusals = [] };
}

/// <summary>
/// Where a job's live logs come from.
/// </summary>
/// <remarks>
/// Injectable for the reason <c>IArchiveSource</c> is, one field below it in
/// <c>RotationRunner</c>: this is the code that decides which files a job acts on, and that
/// decision deserves tests that do not need a file system. It needs one more than the archive
/// side does, because <c>PathGuard</c> refuses every Unix path as not absolute - so a runner test
/// driven by real files can only run on Windows, and the rules worth proving here are
/// platform-neutral.
/// </remarks>
public interface IFileSource
{
    EnumerationResult Resolve(string pattern);
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
/// <para>
/// <b>Every link is resolved before it is entered, including the anchor.</b> The walk is by hand
/// rather than through <c>RecurseSubdirectories</c> precisely so it can decline one - see
/// <see cref="Walk"/> - but the anchor used to be pushed onto that walk unchecked, so a job
/// anchored at a junction was followed wherever it led while the guard compared only the string
/// the operator had typed.
/// </para>
/// </remarks>
public sealed class FileEnumerator(PathGuard guard, ILinkResolver? links = null) : IFileSource
{
    private readonly ILinkResolver _links = links ?? new LinkResolver();

    /// <summary>
    /// Verdicts already reached this run, keyed on the path asked about.
    /// </summary>
    /// <remarks>
    /// Per instance, and the instance lives for one run. Nothing inside a single run can change
    /// where a link leads, so re-asking only adds ways to fail - and the cache is also what makes
    /// a junction met twice in one <c>**</c> walk report once rather than twice.
    /// </remarks>
    private readonly Dictionary<string, GuardDecision> _decided = new(StringComparer.OrdinalIgnoreCase);

    public EnumerationResult Resolve(string pattern)
    {
        var normalized = WinPath.Normalize(pattern);
        var anchor = Glob.LiteralPrefix(normalized);

        if (string.IsNullOrEmpty(anchor) || !Directory.Exists(anchor))
        {
            return EnumerationResult.Nothing;
        }

        var refusals = new List<GuardDecision>();

        // The whole anchor at once. Glob.LiteralPrefix can hand back several levels, and every
        // one of them is independently junctionable - but the final-path call canonicalises all of
        // them in a single open, so there is nothing left to check component by component.
        var resolvedAnchor = Vet(anchor, refusals);
        if (resolvedAnchor is null)
        {
            return new EnumerationResult { Files = [], Refusals = refusals };
        }

        var recurse = normalized.Contains("**", StringComparison.Ordinal);
        var results = new List<MatchedFile>();

        foreach (var path in Walk(anchor, resolvedAnchor, recurse, refusals))
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

            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                // Vetted first, so a link aimed somewhere protected is the security finding it is
                // rather than a routine skip - and only then reported as unfollowed. Either way it
                // is named: this skip used to be a bare continue, so a linked log was never
                // rotated and nobody was ever told.
                if (Vet(path, refusals) is { } target)
                {
                    refusals.Add(guard.LinkedFileNotFollowed(path, target));
                }

                continue;
            }

            results.Add(new MatchedFile
            {
                Path = path,
                Length = info.Length,
                LastWriteUtc = info.LastWriteTimeUtc,
            });
        }

        // Windows enumeration order is unspecified - NTFS returns roughly B-tree order, ReFS
        // and SMB differ. Retention decides which files die, so the order it sees must be
        // deterministic and must not depend on which filesystem the logs happen to live on.
        results.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));

        return new EnumerationResult
        {
            Files = results,
            Refusals = refusals,
            ResolvedAnchor = WinPath.CanonicalKey(resolvedAnchor) == WinPath.CanonicalKey(anchor)
                ? null
                : resolvedAnchor,
        };
    }

    /// <summary>
    /// Where this path really leads, or null if it must not be entered.
    /// </summary>
    /// <remarks>
    /// Appends to <paramref name="refusals"/> at most once per distinct path per run, so the same
    /// junction reached from two branches of a <c>**</c> walk is reported once.
    /// </remarks>
    private string? Vet(string path, List<GuardDecision> refusals)
    {
        if (_decided.TryGetValue(path, out var remembered))
        {
            return remembered.IsAllowed ? remembered.Subject : null;
        }

        var target = _links.Resolve(path);

        if (!target.Resolved || target.FinalPath is not { } final)
        {
            var refusal = guard.UnresolvableLink(
                path, Win32Error.Describe(target.Error), target.Error);

            _decided[path] = refusal;
            refusals.Add(refusal);
            return null;
        }

        var decision = guard.CheckLinkTarget(path, final);

        // Remembered carrying the resolved path, so a cache hit answers the same question the
        // miss did rather than handing back the name we started from.
        _decided[path] = decision.IsAllowed
            ? decision with { Subject = final }
            : decision;

        if (decision.IsAllowed)
        {
            return final;
        }

        refusals.Add(decision);
        return null;
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
    private IEnumerable<string> Walk(
        string root, string resolvedRoot, bool recurse, List<GuardDecision> refusals)
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

        var pending = new Stack<(string Path, string Resolved)>();
        pending.Push((root, resolvedRoot));

        // Keyed on where a directory REALLY is, which is what makes it a cycle guard at all. Keyed
        // on the textual path - as it was - a junction loop produces C:\a, C:\a\b, C:\a\b\b and so
        // on for ever, every one of them a different string, and the set never fires.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (pending.Count > 0)
        {
            var (dir, resolved) = pending.Pop();

            if (!seen.Add(WinPath.CanonicalKey(resolved)))
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

            IEnumerable<DirectoryInfo> children;
            try
            {
                // DirectoryInfo rather than the path overload: these carry their attributes
                // already, from the same find data, so testing for a reparse point below costs
                // nothing where the old code paid an extra stat on every child directory.
                children = new DirectoryInfo(dir).EnumerateDirectories("*", options);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or DirectoryNotFoundException)
            {
                continue;
            }

            foreach (var child in children)
            {
                if (!child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    // A child of a directory we have already resolved, that is not itself a link,
                    // is resolved by concatenation. No handle, no syscall.
                    pending.Push((child.FullName, WinPath.Combine(resolved, child.Name)));
                    continue;
                }

                if (Vet(child.FullName, refusals) is { } target)
                {
                    pending.Push((child.FullName, target));
                }
            }
        }
    }
}
