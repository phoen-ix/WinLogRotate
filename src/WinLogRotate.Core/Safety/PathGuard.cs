using WinLogRotate.Core.Globbing;

namespace WinLogRotate.Core.Safety;

/// <summary>The reason a guard refused, or <see cref="Allowed"/>.</summary>
public enum GuardVerdict
{
    Allowed,

    /// <summary>The path is inside, or is, a location we will not delete files from.</summary>
    ProtectedLocation,

    /// <summary>A drive or share root. A pattern rooted there almost always means the operator
    /// mistyped, and the blast radius is the whole volume.</summary>
    VolumeRoot,

    /// <summary>More files matched than the configured ceiling.</summary>
    TooManyMatches,

    /// <summary>The path is malformed. See <see cref="GuardDecision.PathProblem"/>.</summary>
    InvalidPath,

    /// <summary>A link whose real target is somewhere we will not act. Any user can create a
    /// junction with no privilege at all, so following one while running as SYSTEM hands them
    /// our privileges.</summary>
    ReparsePoint,

    /// <summary>
    /// A link whose target could not be established.
    /// </summary>
    /// <remarks>
    /// Not an accusation. We simply could not prove it safe, so nothing behind it was searched -
    /// which is a different thing from finding something wrong, and is reported as such.
    /// </remarks>
    Unverifiable,
}

/// <summary>The guard's answer, with everything needed to explain it to a human.</summary>
public sealed record GuardDecision
{
    public required GuardVerdict Verdict { get; init; }
    public required string Subject { get; init; }
    public string? Message { get; init; }
    public string? Remedy { get; init; }
    public PathProblem PathProblem { get; init; }

    /// <summary>True when the verdict would have been a refusal but a scoped override
    /// permitted it. Always journaled - an override must never be quietly forgotten.</summary>
    public bool Overridden { get; init; }

    /// <summary>The Win32 error behind the refusal, where one caused it.</summary>
    public int NativeError { get; init; }

    public bool IsAllowed => Verdict == GuardVerdict.Allowed;
}

/// <summary>Configuration for <see cref="PathGuard"/>.</summary>
public sealed record GuardOptions
{
    /// <summary>Locations we refuse to rotate or delete inside. Overridable per pattern, never
    /// globally.</summary>
    public IReadOnlyList<string> ProtectedRoots { get; init; } = DefaultProtectedRoots();

    /// <summary>
    /// Patterns the operator has explicitly, individually unlocked.
    /// <para>
    /// Scoped on purpose: this is a list of exact patterns, not a boolean. There is no
    /// "disable safety" switch, because that is the switch everyone flips once during an
    /// incident and never flips back.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> AllowDangerous { get; init; } = [];

    /// <summary>Refuse a pattern that resolves to more files than this. A pattern matching
    /// 40,000 files is a typo far more often than it is a plan.</summary>
    public int MaxMatches { get; init; } = 1000;

    /// <summary>True when the current process holds an elevated token.</summary>
    public bool Elevated { get; init; }

    /// <summary>
    /// Overrides <see cref="MaxMatches"/>. Only for directories the product owns and the
    /// installer created with a locked-down ACL - the journal's own folder - where a
    /// "did you really mean 40,000 files?" prompt protects nobody.
    /// </summary>
    public int? MaxFilesOverride { get; init; }

    /// <summary>
    /// The default protected set. Resolved from the running system where possible so a machine
    /// with Windows on D: is still protected, with literals as a fallback for non-Windows test
    /// runs.
    /// </summary>
    public static IReadOnlyList<string> DefaultProtectedRoots()
    {
        var roots = new List<string>();

        void Add(Environment.SpecialFolder folder)
        {
            var path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(path))
            {
                roots.Add(WinPath.Normalize(path));
            }
        }

        Add(Environment.SpecialFolder.Windows);
        Add(Environment.SpecialFolder.System);
        Add(Environment.SpecialFolder.SystemX86);
        Add(Environment.SpecialFolder.ProgramFiles);
        Add(Environment.SpecialFolder.ProgramFilesX86);

        if (roots.Count == 0)
        {
            roots.AddRange([@"C:\Windows", @"C:\Program Files", @"C:\Program Files (x86)"]);
        }

        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}

/// <summary>
/// Refuses destructive work in places it should never happen.
/// <para>
/// This runs before anything is opened, renamed or deleted, and it is deliberately biased
/// toward refusing: a false refusal costs an operator one config edit and a clear message,
/// while a false permit can delete a system directory as SYSTEM at three in the morning.
/// </para>
/// </summary>
public sealed class PathGuard(GuardOptions options)
{
    public GuardOptions Options { get; } = options;

    /// <summary>
    /// Checks a configured glob pattern, before any filesystem access.
    /// </summary>
    public GuardDecision CheckPattern(string pattern)
    {
        var problem = WinPath.Validate(pattern, allowWildcards: true);
        if (problem != PathProblem.None)
        {
            return new GuardDecision
            {
                Verdict = GuardVerdict.InvalidPath,
                Subject = pattern,
                PathProblem = problem,
                Message = Describe(problem, pattern),
                Remedy = "Use a full absolute path, e.g. \"C:/logs/app/*.log\".",
            };
        }

        var normalized = WinPath.Normalize(pattern);
        var overridden = IsOverridden(normalized);

        // The directory the pattern is anchored at, which is what actually gets walked.
        var anchor = Glob.LiteralPrefix(normalized);

        if (string.IsNullOrEmpty(anchor) || WinPath.IsRoot(anchor))
        {
            return Refuse(GuardVerdict.VolumeRoot, pattern, overridden,
                $"'{pattern}' is anchored at a volume root, so it would walk the entire drive.",
                "Anchor the pattern at the directory the logs actually live in.");
        }

        foreach (var root in Options.ProtectedRoots)
        {
            if (IsWithin(anchor, root))
            {
                return Refuse(GuardVerdict.ProtectedLocation, pattern, overridden,
                    $"'{pattern}' resolves inside '{root}', which WinLogRotate will not delete files from.",
                    $"If this is genuinely intended, add the exact pattern to allowDangerous. " +
                    "There is no global override, on purpose.");
            }
        }

        return Allow(pattern, overridden);
    }

    /// <summary>Checks a concrete file about to be modified or deleted.</summary>
    public GuardDecision CheckPath(string path)
    {
        var problem = WinPath.Validate(path);
        if (problem != PathProblem.None)
        {
            return new GuardDecision
            {
                Verdict = GuardVerdict.InvalidPath,
                Subject = path,
                PathProblem = problem,
                Message = Describe(problem, path),
            };
        }

        var normalized = WinPath.Normalize(path);
        var overridden = IsOverridden(normalized);

        return ClassifyLocation(normalized) switch
        {
            (GuardVerdict.VolumeRoot, _) =>
                Refuse(GuardVerdict.VolumeRoot, path, overridden, $"'{path}' is a volume root.", null),

            (GuardVerdict.ProtectedLocation, var root) =>
                Refuse(GuardVerdict.ProtectedLocation, path, overridden,
                    $"'{path}' is inside the protected location '{root}'.", null),

            _ => Allow(path, overridden),
        };
    }

    /// <summary>
    /// Where a normalised path sits, by location alone.
    /// </summary>
    /// <remarks>
    /// Extracted so the link check runs literally the same rule, rather than a second copy of it
    /// that can drift. A link is refused for exactly the reasons a path spelled out in full would
    /// be, which is the whole of the new rule: resolve it, then ask the question already asked.
    /// </remarks>
    private (GuardVerdict Verdict, string? Root) ClassifyLocation(string normalized)
    {
        if (WinPath.IsRoot(normalized))
        {
            return (GuardVerdict.VolumeRoot, null);
        }

        foreach (var root in Options.ProtectedRoots)
        {
            if (IsWithin(normalized, root))
            {
                return (GuardVerdict.ProtectedLocation, root);
            }
        }

        return (GuardVerdict.Allowed, null);
    }

    /// <summary>Checks the size of a resolved match set.</summary>
    public GuardDecision CheckMatchCount(string pattern, int count)
    {
        if (count <= (Options.MaxFilesOverride ?? Options.MaxMatches))
        {
            return Allow(pattern, overridden: false);
        }

        var overridden = IsOverridden(WinPath.Normalize(pattern));
        return Refuse(GuardVerdict.TooManyMatches, pattern, overridden,
            $"'{pattern}' matches {count:N0} files, above the limit of {Options.MaxMatches:N0}.",
            "Narrow the pattern, or raise maxfiles if this really is one job.");
    }

    /// <summary>
    /// Decides whether a link may be followed, by asking where it actually leads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Junctions need no privilege to create - any user with write access to a directory can point
    /// it anywhere. A low-privileged user who controls <c>C:\App\logs</c> can aim it at
    /// <c>System32</c> and turn our maxage cleanup into their privilege escalation.
    /// </para>
    /// <para>
    /// The rule is the one <see cref="CheckPath"/> already applies, run against the resolved
    /// target instead of the name. That is deliberately not "refuse every link": relocating a log
    /// directory onto another volume when a system drive fills up is ordinary practice, and
    /// <c>C:\inetpub\logs</c> pointing at <c>D:\logs\iis</c> is a path we would have permitted had
    /// the operator typed it out. What is refused is a link that arrives somewhere we would have
    /// refused anyway - which also closes two holes the textual check never could, since
    /// <c>C:\PROGRA~1</c> and a <c>subst</c>ed drive are different strings for the same place.
    /// </para>
    /// <para>
    /// <b>Never overridable.</b> Not by <c>allowdangerous</c>, not by anything. The destination is
    /// reachable by naming it directly, which is explicit and auditable; and an override lives in
    /// <c>conf.d</c>, whose whole <c>LR9001</c> machinery exists because a non-administrator may be
    /// able to write there - so an override a local user can author would be the escalation rather
    /// than the fix.
    /// </para>
    /// <para>
    /// The severity does not soften when unelevated. A junction into <c>System32</c> is the same
    /// finding whichever token this process holds, and one condition arriving at two severities is
    /// how an alert threshold quietly stops matching.
    /// </para>
    /// </remarks>
    public GuardDecision CheckLinkTarget(string linkPath, string resolvedTarget)
    {
        var normalized = WinPath.Normalize(resolvedTarget);

        var (verdict, root) = ClassifyLocation(normalized);

        if (verdict == GuardVerdict.Allowed)
        {
            return Allow(linkPath, overridden: false);
        }

        var where = verdict == GuardVerdict.VolumeRoot
            ? "a volume root"
            : $"the protected location '{root}'";

        return new GuardDecision
        {
            Verdict = GuardVerdict.ReparsePoint,
            Subject = linkPath,

            // Both paths, always. "Not followed" without saying what was aimed where leaves an
            // operator nothing to act on and no way to tell a mistake from an attack.
            Message = $"'{linkPath}' is a link to '{normalized}', which is {where}; not following it.",
            Remedy = "A link is never followed anywhere its target would not be permitted, and "
                   + "that is not overridable. If the target really is a log directory, name it "
                   + "directly in this job's paths instead.",
        };
    }

    /// <summary>
    /// A linked log file, whose target is fine and which is skipped anyway.
    /// </summary>
    /// <remarks>
    /// Not a security refusal: the target passed. Files are left unfollowed because the three
    /// actions disagree about what "the file" would mean - <c>copytruncate</c> would empty the
    /// target, a rename moves the link and leaves the target growing, and a delete removes only
    /// the link. Following them is a feature with semantics to settle, not a safety fix. What this
    /// changes is that the skip is said out loud, because a log that is never rotated and never
    /// mentioned is the worst outcome this product has.
    /// </remarks>
    public GuardDecision LinkedFileNotFollowed(string linkPath, string resolvedTarget) => new()
    {
        Verdict = GuardVerdict.Unverifiable,
        Subject = linkPath,
        Message = $"'{linkPath}' is a link to '{resolvedTarget}'. Linked log files are not "
                + "rotated, so it was left alone.",
        Remedy = "Name the target directly in this job's paths if it should be rotated.",
    };

    /// <summary>
    /// A link whose target could not be established.
    /// </summary>
    /// <remarks>
    /// Takes the operating system's own words rather than its error number, so the guard keeps
    /// owning the sentence and the remedy without <c>Safety</c> acquiring a dependency on
    /// <c>Io</c>.
    /// </remarks>
    public GuardDecision UnresolvableLink(string linkPath, string because, int nativeError) =>
        new()
        {
            Verdict = GuardVerdict.Unverifiable,
            Subject = linkPath,
            NativeError = nativeError,
            Message = $"'{linkPath}' is a link whose target could not be established: {because}. "
                    + "Nothing behind it was searched.",
            Remedy = "This is not a refusal - we could not prove it safe rather than finding it "
                   + "unsafe. A link to a disconnected share or a deleted directory looks like "
                   + "this; so does one pointing somewhere this account may not open.",
        };

    /// <summary>
    /// True when <paramref name="candidate"/> is <paramref name="root"/> or sits beneath it.
    /// <para>
    /// The separator check is what stops <c>C:\Program Files Custom</c> from being treated as
    /// inside <c>C:\Program Files</c> - a plain StartsWith would say yes.
    /// </para>
    /// </summary>
    internal static bool IsWithin(string candidate, string root)
    {
        var c = WinPath.Normalize(candidate);
        var r = WinPath.Normalize(root);

        if (c.Equals(r, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return c.StartsWith(r.EndsWith('\\') ? r : r + '\\', StringComparison.OrdinalIgnoreCase);
    }

    private bool IsOverridden(string normalized) =>
        Options.AllowDangerous.Any(allowed =>
            WinPath.Normalize(allowed).Equals(normalized, StringComparison.OrdinalIgnoreCase)
            || Glob.IsMatch(normalized, WinPath.Normalize(allowed)));

    private static GuardDecision Allow(string subject, bool overridden) =>
        new() { Verdict = GuardVerdict.Allowed, Subject = subject, Overridden = overridden };

    private static GuardDecision Refuse(
        GuardVerdict verdict, string subject, bool overridden, string message, string? remedy) =>
        overridden
            ? new GuardDecision
            {
                Verdict = GuardVerdict.Allowed,
                Subject = subject,
                Overridden = true,
                Message = message + " Permitted by an explicit allowDangerous entry.",
            }
            : new GuardDecision
            {
                Verdict = verdict,
                Subject = subject,
                Message = message,
                Remedy = remedy,
            };

    private static string Describe(PathProblem problem, string path) => problem switch
    {
        PathProblem.Empty => "The path is empty.",
        PathProblem.NotAbsolute => $"'{path}' is not an absolute path. Drive-relative paths like 'C:logs' resolve against a per-drive current directory, which a service does not have.",
        PathProblem.ReservedName => $"'{path}' contains a reserved device name (CON, NUL, COM1...), which Windows resolves to a device rather than a file.",
        PathProblem.TrailingDotOrSpace => $"'{path}' has a segment ending in a dot or space. Win32 silently strips those, so this would not name the file you think it does.",
        PathProblem.AlternateDataStream => $"'{path}' names an alternate data stream.",
        PathProblem.InvalidCharacter => $"'{path}' contains a character that is not valid in a Windows path.",
        PathProblem.ParentTraversal => $"'{path}' contains '..'. Resolve the path yourself rather than having it walk upward at runtime.",
        _ => $"'{path}' is not usable.",
    };
}
