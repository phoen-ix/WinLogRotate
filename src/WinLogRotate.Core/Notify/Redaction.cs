using System.Text.RegularExpressions;

namespace WinLogRotate.Core.Notify;

/// <summary>
/// Hides what is actually a credential, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// The distinction matters in both directions. A Slack or Teams incoming-webhook URL <em>is</em>
/// the bearer credential - the path is the secret, not just the host - so a URL has to lose
/// everything after the host or a diagnostic quoting it hands the credential to whoever reads
/// the log. But <c>command:</c>, <c>service:</c> and <c>event:</c> targets are written in the
/// configuration in the clear, and masking them would only make a diagnostic useless: nobody can
/// fix <c>service:paramchange:***</c>.
/// </para>
/// <para>
/// Over-masking is not a safe default. It costs the operator the one piece of information that
/// would let them repair the thing, and the temptation is then to turn redaction off entirely.
/// </para>
/// </remarks>
public static partial class Redaction
{
    /// <summary>Query-string keys whose values are credentials rather than parameters.</summary>
    [GeneratedRegex(@"(?i)\b(token|key|secret|pass(word)?|sig|signature|auth|api[-_]?key)\b")]
    private static partial Regex SensitiveParameter();

    /// <summary>Masks a target for display, according to what kind of thing it is.</summary>
    public static string MaskTarget(HookScheme scheme, string target) =>
        HookSchemes.TargetIsUrl(scheme) ? MaskUrl(target) : target;

    /// <summary>
    /// Reduces a URL to scheme, host and an ellipsis.
    /// </summary>
    /// <remarks>
    /// The host is kept because it is what an operator needs in order to recognise which
    /// integration is broken, and because it is not the secret: a webhook URL's entropy is all
    /// in its path. Userinfo goes even though it is before the host, since that is a credential
    /// by definition.
    /// </remarks>
    public static string MaskUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            // Not parseable, so its shape is unknown and no part of it can be called safe.
            return "***";
        }

        var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        var hasMore = uri.AbsolutePath.Length > 1 || uri.Query.Length > 0;

        return $"{uri.Scheme}://{uri.Host}{port}{(hasMore ? "/..." : string.Empty)}";
    }

    /// <summary>
    /// Masks anything that looks like a credential inside free text - an error message, most
    /// often, which is where a URL ends up when a library builds the message for us.
    /// </summary>
    public static string MaskText(string text, IReadOnlyList<string>? extra = null)
    {
        var masked = UrlInText().Replace(text, m => MaskUrl(m.Value));

        if (extra is null)
        {
            return masked;
        }

        // Operator-configured strings, for the things only they know are sensitive - a customer
        // name in a path, an internal hostname.
        foreach (var word in extra)
        {
            if (!string.IsNullOrWhiteSpace(word))
            {
                masked = masked.Replace(word, "***", StringComparison.OrdinalIgnoreCase);
            }
        }

        return masked;
    }

    [GeneratedRegex(@"https?://[^\s""'<>]+")]
    private static partial Regex UrlInText();

    /// <summary>True if a query-string key names something that should never be printed.</summary>
    public static bool IsSensitiveParameter(string key) => SensitiveParameter().IsMatch(key);
}
