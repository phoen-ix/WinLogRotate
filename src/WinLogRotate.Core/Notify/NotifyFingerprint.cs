using System.Security.Cryptography;
using System.Text;

namespace WinLogRotate.Core.Notify;

/// <summary>
/// Identifies a failure <em>shape</em>, so that "is this the same problem as last time?" can be
/// answered without comparing prose.
/// </summary>
/// <remarks>
/// <para>
/// What goes in is as important as how it is hashed, and every exclusion below is a specific
/// false "changed" that would otherwise be sent:
/// </para>
/// <list type="bullet">
/// <item><description><b>The message and the remedy.</b> Prose gets improved between releases.
/// A prose-derived fingerprint reports every ongoing failure as newly broken the night of an
/// upgrade, and then newly recovered - two pages per incident, for a wording change.</description></item>
/// <item><description><b>The count.</b> 412 locked files today and 413 tomorrow is one incident.
/// Including it makes every run a change, which turns <c>on = "change"</c> into <c>on = "every"</c>
/// wearing a disguise.</description></item>
/// <item><description><b>The example filename.</b> <c>u_ex260907.log</c> becomes
/// <c>u_ex260908.log</c> at midnight - a guaranteed daily false change on the most common
/// Windows log job there is.</description></item>
/// <item><description>Timestamps, run ids, machine name, line and column.</description></item>
/// </list>
/// </remarks>
public static class NotifyFingerprint
{
    /// <summary>
    /// Names this scheme in the stored state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When a future release changes what goes into a fingerprint, this changes with it and the
    /// stored values are adopted silently rather than compared. A version prefix <em>inside</em>
    /// the digest would instead guarantee the mass re-page it is meant to prevent.
    /// </para>
    /// <para>
    /// <c>wlr-fp-2</c>: the parts are sorted before hashing. <c>wlr-fp-1</c> hashed them in the
    /// order the planner displays them - severity, then <em>count</em>, then code - so two problem
    /// groups swapping places by count produced a different digest although the count itself is
    /// excluded by design. A spurious CHANGED for every such swap.
    /// </para>
    /// </remarks>
    public const string Algorithm = "wlr-fp-2";

    /// <summary>Between fields. A control character, because a path may contain anything else.</summary>
    private const char FieldSeparator = '\u001F';

    /// <summary>Between groups.</summary>
    private const char GroupSeparator = '\u001E';

    /// <summary>
    /// Digests the groups of one job, in an order of its own choosing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The separators are what make this safe. With an ordinary delimiter, ("AB", "C") and
    /// ("A", "BC") digest identically - a constructible collision, and a collision here means a
    /// real change is read as "no change", which is silence. Nobody would notice until it
    /// swallowed a live failure.
    /// </para>
    /// <para>
    /// The sort is what makes it honest. The caller's order is for the reader and includes the
    /// count, which the digest deliberately leaves out; hashing in that order let the count back
    /// in through the side door, so 5x locked + 3x failed and 2x locked + 3x failed were two
    /// different problems. Ordinal on every field, so two runtimes agree.
    /// </para>
    /// </remarks>
    public static string For(IEnumerable<FingerprintPart> parts)
    {
        var builder = new StringBuilder();
        var any = false;

        var ordered = parts
            .OrderBy(p => p.Code, StringComparer.Ordinal)
            .ThenBy(p => p.NativeError)
            .ThenBy(p => p.Severity, StringComparer.Ordinal)
            .ThenBy(p => p.Where, StringComparer.Ordinal);

        foreach (var part in ordered)
        {
            if (any)
            {
                builder.Append(GroupSeparator);
            }

            any = true;
            builder.Append(part.Code).Append(FieldSeparator)
                   .Append(part.NativeError.ToString(System.Globalization.CultureInfo.InvariantCulture))
                   .Append(FieldSeparator)
                   .Append(part.Severity).Append(FieldSeparator)
                   .Append(part.Where);
        }

        // Distinguishable from the digest of an empty string, and cheap to assert.
        return any
            ? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())).AsSpan(0, 8))
            : string.Empty;
    }

    /// <summary>
    /// Normalises a directory so two spellings of one place agree.
    /// </summary>
    /// <remarks>
    /// Delegates to <see cref="Safety.WinPath.CanonicalKey"/>, which is what state already keys
    /// paths by. A hand-rolled version here got two edges wrong: it did not collapse repeated
    /// separators, so <c>C:\logs\\app</c> and <c>C:\logs\app</c> fingerprinted differently -
    /// exactly what this method promises not to do - and trimming trailing separators turned
    /// <c>C:\</c> into <c>C:</c>, giving a volume root two spellings depending on which code
    /// path produced it. It also carries the Turkish-locale reasoning that the copy would have
    /// had to rediscover.
    /// </remarks>
    public static string Canonical(string? directory) =>
        string.IsNullOrEmpty(directory) ? string.Empty : Safety.WinPath.CanonicalKey(directory);
}

/// <summary>One group's contribution to a fingerprint.</summary>
/// <remarks>
/// <see cref="Severity"/> is carried as its NAME rather than its numeric value, so that a future
/// renumbering of the enum fails loudly in review instead of silently reclassifying every stored
/// fingerprint.
/// </remarks>
public readonly record struct FingerprintPart(string Code, int NativeError, string Severity, string Where);
