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
    /// When a future release changes what goes into a fingerprint, this changes with it and the
    /// stored values are adopted silently rather than compared. A version prefix <em>inside</em>
    /// the digest would instead guarantee the mass re-page it is meant to prevent.
    /// </remarks>
    public const string Algorithm = "wlr-fp-1";

    /// <summary>Between fields. A control character, because a path may contain anything else.</summary>
    private const char FieldSeparator = '\u001F';

    /// <summary>Between groups.</summary>
    private const char GroupSeparator = '\u001E';

    /// <summary>
    /// Digests the ordered groups of one job.
    /// </summary>
    /// <remarks>
    /// The separators are what make this safe. With an ordinary delimiter, ("AB", "C") and
    /// ("A", "BC") digest identically - a constructible collision, and a collision here means a
    /// real change is read as "no change", which is silence. Nobody would notice until it
    /// swallowed a live failure.
    /// </remarks>
    public static string For(IEnumerable<FingerprintPart> parts)
    {
        var builder = new StringBuilder();
        var any = false;

        foreach (var part in parts)
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
