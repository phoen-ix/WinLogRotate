using System.Security.Cryptography;
using System.Text;

namespace WinLogRotate.Core.Notify;

/// <summary>
/// One parsed hook or notification target.
/// </summary>
/// <remarks>
/// Immutable, and it never prints its own credential: <see cref="ToString"/> goes through
/// <see cref="Redaction"/>, so a target that reaches a diagnostic, a journal line or an
/// exception message by accident is masked by default rather than by the diligence of whoever
/// wrote that call site.
/// </remarks>
public sealed record HookAction
{
    public required HookScheme Scheme { get; init; }

    /// <summary>Exactly as written, for diagnostics. Never rendered unmasked.</summary>
    public required string Raw { get; init; }

    /// <summary>The scheme-specific part: a command line, a service name, a full URL.</summary>
    public required string Target { get; init; }

    /// <summary><c>service:VERB:NAME</c>'s verb. Null for every other scheme, and for
    /// <c>service:NAME</c>, where it means the default control code.</summary>
    public string? Verb { get; init; }

    /// <summary>The provider table this came from, such as <c>email.relay</c>, when it came
    /// from one rather than being written inline.</summary>
    public string? Provider { get; init; }

    /// <summary>Where it was written, so a diagnostic can be clicked.</summary>
    public string? File { get; init; }

    public int Line { get; init; }

    public int Column { get; init; }

    /// <summary>
    /// Stable identity, for keying persisted state such as a circuit breaker.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The provider name when there is one, because that is what an operator types into
    /// <c>notify reset</c> and what they see in <c>notify status</c>.
    /// </para>
    /// <para>
    /// For an inline target it is the scheme, the host, and eight hex of SHA-256 over the whole
    /// target. It cannot be the masked target: masking a webhook URL removes the path, and the
    /// path is the only thing distinguishing two Slack webhooks - they would collapse to one key,
    /// so a breaker opened by one would suppress the other and neither could be addressed
    /// individually. The hash distinguishes them while revealing nothing about a high-entropy
    /// URL, and it is derived from Scheme and Target alone, so rewording a message can never
    /// make the breaker think it is looking at a different channel.
    /// </para>
    /// </remarks>
    public string Key { get; private init; } = string.Empty;

    /// <summary>Builds an action with its <see cref="Key"/> computed.</summary>
    public static HookAction Create(
        HookScheme scheme, string raw, string target,
        string? verb = null, string? provider = null,
        string? file = null, int line = 0, int column = 0)
    {
        var action = new HookAction
        {
            Scheme = scheme,
            Raw = raw,
            Target = target,
            Verb = verb,
            Provider = provider,
            File = file,
            Line = line,
            Column = column,
        };

        return action with { Key = ComputeKey(scheme, target, provider) };
    }

    private static string ComputeKey(HookScheme scheme, string target, string? provider)
    {
        if (!string.IsNullOrEmpty(provider))
        {
            return provider.ToLowerInvariant();
        }

        var name = HookSchemes.Name(scheme);

        if (!HookSchemes.TargetIsUrl(scheme))
        {
            return $"{name}:{target}".ToLowerInvariant();
        }

        var digest = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(target)).AsSpan(0, 4));

        var host = Uri.TryCreate(target, UriKind.Absolute, out var uri) ? uri.Host : "?";
        return $"{name}:{host}:{digest}".ToLowerInvariant();
    }

    /// <summary>The target, masked. This is what any diagnostic or journal line should print.</summary>
    public string Display => Redaction.MaskTarget(Scheme, Target);

    public override string ToString() => Display;
}
