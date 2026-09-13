using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;

namespace WinLogRotate.Core.Notify.Delivery;

/// <summary>Why a request never completed, in this project's words, with the socket error when there was one.</summary>
internal readonly record struct HttpFailure(string Error, int? NativeError);

/// <summary>
/// The one place an outbound HTTP client is configured for notifications.
/// </summary>
/// <remarks>
/// Shared by the webhook and Pushover transports so the proxy and the certificate pin cannot apply
/// to one and not the other - a setting that silently covers some channels is worse than one that
/// covers none, because it looks like it worked. The same goes for what a failure is called: one
/// <see cref="Describe(HttpRequestException)"/>, so a rejected pin reads the same on both.
/// </remarks>
internal static class NotifyHttpClient
{
    /// <summary>
    /// What to call an <see cref="HttpRequestException"/>, however deep the runtime buried the cause.
    /// </summary>
    /// <remarks>
    /// Composed from what this project owns - the published binary sets <c>UseSystemResourceKeys</c>,
    /// so the exception's own message is a bare resource key. Both senders used to read "the request
    /// did not complete" for anything that was not a <c>SocketException</c>, and the Pushover sender
    /// looked one level deep for that; a pin that did not match - the case pinning exists for - was
    /// indistinguishable from a cable pulled out, and the operator went looking at the network.
    /// </remarks>
    public static HttpFailure Describe(HttpRequestException e)
    {
        for (Exception? inner = e.InnerException; inner is not null; inner = inner.InnerException)
        {
            switch (inner)
            {
                case AuthenticationException:
                    return new HttpFailure("the certificate was rejected - check server_cert_thumbprint", null);

                case SocketException socket:
                    return new HttpFailure(Describe(socket.SocketErrorCode), socket.ErrorCode);
            }
        }

        return new HttpFailure("the request did not complete", null);
    }

    private static string Describe(SocketError error) => error switch
    {
        SocketError.HostNotFound or SocketError.NoData => "the host name did not resolve",
        SocketError.ConnectionRefused => "the connection was refused",
        SocketError.TimedOut => "the connection timed out",
        SocketError.NetworkUnreachable or SocketError.HostUnreachable => "the host is unreachable",
        _ => "the connection failed",
    };
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
