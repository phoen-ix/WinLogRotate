using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace WinLogRotate.Core.Notify.Delivery;

/// <summary>
/// Certificate pinning, and the proxy the senders use.
/// </summary>
/// <remarks>
/// <para>
/// There is deliberately no way to skip verification. <c>NotifySettings</c> refuses an
/// <c>insecure</c> key by name, for the reason written there: such a flag is set once during an
/// incident and never unset. What pinning does instead is let an internal CA or a self-signed
/// relay work <i>without</i> that flag - the operator names the certificate they expect, and only
/// that certificate is accepted.
/// </para>
/// <para>
/// So a pin <b>replaces</b> chain validation rather than relaxing it. With no pin configured no
/// callback is installed at all and the operating system's trust store decides, exactly as it does
/// for the update check.
/// </para>
/// </remarks>
public static class TlsPinning
{
    /// <summary>Whether this certificate is the pinned one. SHA-256 over the raw DER.</summary>
    public static bool Matches(string thumbprint, X509Certificate? certificate)
    {
        if (certificate is null)
        {
            return false;
        }

        Span<byte> hash = stackalloc byte[32];
        if (!SHA256.TryHashData(certificate.GetRawCertData(), hash, out _))
        {
            return false;
        }

        return Normalise(Convert.ToHexString(hash)) == Normalise(thumbprint);
    }

    /// <summary>Accepts colons, spaces and any casing, so a thumbprint can be pasted from anywhere.</summary>
    public static string Normalise(string thumbprint)
    {
        Span<char> buffer = stackalloc char[thumbprint.Length];
        var length = 0;

        foreach (var c in thumbprint)
        {
            if (char.IsAsciiHexDigit(c))
            {
                buffer[length++] = char.ToUpperInvariant(c);
            }
        }

        return new string(buffer[..length]);
    }

    /// <summary>A validation callback for the pin, or null to leave the default alone.</summary>
    public static RemoteCertificateValidationCallback? CallbackFor(string? thumbprint) =>
        string.IsNullOrWhiteSpace(thumbprint)
            ? null
            : (_, certificate, _, _) => Matches(thumbprint, certificate);
}

/// <summary>Turns the <c>proxy</c> and <c>no_proxy</c> settings into an <see cref="IWebProxy"/>.</summary>
public static class ProxyPolicy
{
    /// <summary>The literal that means "go direct, whatever the system says".</summary>
    public const string None = "none";

    /// <summary>
    /// Null means "leave the handler's default alone", which is the system proxy.
    /// </summary>
    /// <param name="bypass">
    /// Set false for <c>proxy = "none"</c>: the handler must be told to use no proxy at all, which
    /// is a different thing from being given an empty one.
    /// </param>
    public static IWebProxy? Resolve(NotifySettings settings, out bool bypass)
    {
        bypass = false;

        if (string.IsNullOrWhiteSpace(settings.Proxy))
        {
            return null;
        }

        if (settings.Proxy.Equals(None, StringComparison.OrdinalIgnoreCase))
        {
            bypass = true;
            return null;
        }

        // BypassOnLocal matches WinHTTP and every browser: a single-label host, or one in this
        // machine's own domain, goes direct without being listed. An internal relay named
        // "smtp" or "smtp.corp.local" is the common case, and requiring it in no_proxy would
        // surprise everyone who has configured a proxy anywhere else.
        return new WebProxy(settings.Proxy, BypassOnLocal: true, BypassList(settings.NoProxy));
    }

    /// <summary>
    /// Converts host suffixes to the regular expressions <see cref="WebProxy"/> actually wants.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>WebProxy.BypassList</c> takes regexes, not hostnames, and nothing about the property says
    /// so. An unescaped <c>example.com</c> matches <c>exampleXcom</c> and, far worse,
    /// <c>notexample.com.evil.test</c> - so a bypass list written the obvious way sends traffic
    /// direct that the operator meant to route through the proxy.
    /// </para>
    /// <para>
    /// The pattern is matched against the whole <c>scheme://host[:port]</c>, not against the host
    /// alone. Verified against .NET 10 rather than assumed: an expression anchored at the host
    /// never matches the bare domain, and matches subdomains only by accident when a leading
    /// <c>.*</c> happens to swallow the scheme.
    /// </para>
    /// </remarks>
    public static string[] BypassList(IReadOnlyList<string> hosts)
    {
        var patterns = new List<string>(hosts.Count);

        foreach (var host in hosts)
        {
            var trimmed = host.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            // A leading dot is the conventional "and every subdomain" spelling; without one, the
            // host itself and its subdomains are both matched, which is what people mean.
            var bare = System.Text.RegularExpressions.Regex.Escape(trimmed.TrimStart('.'));
            patterns.Add($"^https?://([^/]*\\.)?{bare}(:[0-9]+)?$");
        }

        return [.. patterns];
    }
}
