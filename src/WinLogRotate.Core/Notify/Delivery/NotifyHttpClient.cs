using System.Net.Http;

namespace WinLogRotate.Core.Notify.Delivery;

/// <summary>
/// The one place an outbound HTTP client is configured for notifications.
/// </summary>
/// <remarks>
/// Shared by the webhook and Pushover transports so the proxy and the certificate pin cannot apply
/// to one and not the other - a setting that silently covers some channels is worse than one that
/// covers none, because it looks like it worked.
/// </remarks>
internal static class NotifyHttpClient
{
    public static (HttpClient Client, HttpClientHandler Handler) Create(
        NotifySettings settings, string userAgent)
    {
        var proxy = ProxyPolicy.Resolve(settings, out var bypass);

        var handler = new HttpClientHandler
        {
            // A webhook URL is a secret. Following a redirect would hand it, or the request body,
            // to whatever the first host names next.
            AllowAutoRedirect = false,
        };

        if (bypass)
        {
            handler.UseProxy = false;
        }
        else if (proxy is not null)
        {
            handler.Proxy = proxy;
            handler.UseProxy = true;
        }

        if (settings.ServerCertThumbprint is { } pin && !string.IsNullOrWhiteSpace(pin))
        {
            handler.ServerCertificateCustomValidationCallback =
                (_, certificate, _, _) => TlsPinning.Matches(pin, certificate);
        }

        // No client-wide Timeout: each attempt gets its own share of the phase budget, and a
        // client-wide one would silently override the smaller of the two.
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);

        return (client, handler);
    }
}
