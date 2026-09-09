using System.Globalization;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;

namespace WinLogRotate.Core.Notify.Delivery;

/// <summary>
/// Webhooks: Slack, Teams, Discord, ntfy, or anything that accepts a request.
/// </summary>
/// <remarks>
/// <para>
/// One handler for the whole phase, because the proxy and the certificate pin are phase-wide
/// settings and rebuilding a handler per send would re-resolve the proxy and re-do the TLS
/// handshake every time.
/// </para>
/// <para>
/// Synchronous <c>HttpClient.Send</c>, matching the rest of the run verb. The timeout arrives per
/// attempt because it is a slice of a shared budget, so it goes on a <c>CancellationTokenSource</c>
/// rather than on the client.
/// </para>
/// <para>
/// This file is on the <c>Reveal()</c> allowlist: a webhook URL is a credential - its entropy is in
/// its path - and this is the transport that authenticates with it.
/// </para>
/// </remarks>
public sealed class HttpNotifySender : INotifySender, IDisposable
{
    private readonly HttpClient _client;
    private readonly HttpClientHandler _handler;

    public HttpNotifySender(NotifySettings settings, string userAgent) =>
        (_client, _handler) = NotifyHttpClient.Create(settings, userAgent);

    public SendResult Send(ResolvedChannel channel, NotifyMessage message, TimeSpan timeout)
    {
        var provider = channel.Provider;
        var contentType = provider?.ContentType ?? "application/json";
        var method = new HttpMethod((provider?.Method ?? "POST").ToUpperInvariant());

        string url;
        try
        {
            url = channel.Target.Reveal();
        }
        catch (InvalidOperationException)
        {
            return SendResult.Failed(400, "no URL was resolved for this target");
        }

        var body = NotifyBody.Render(
            provider?.Body, contentType, message.Plan, message.Subject, message.Body, message.Run);

        try
        {
            using var cancel = new CancellationTokenSource(timeout);
            using var request = new HttpRequestMessage(method, url)
            {
                Content = new StringContent(body, Encoding.UTF8, contentType.Split(';')[0].Trim()),
            };

            using var response = _client.Send(request, HttpCompletionOption.ResponseHeadersRead, cancel.Token);

            var status = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                return SendResult.Delivered(status);
            }

            return SendResult.Failed(status, Describe(status), RetryAfter(response));
        }
        catch (OperationCanceledException)
        {
            // Includes the timeout: HttpClient surfaces a cancelled token, not a timeout type.
            return SendResult.Unreachable(
                $"no response within {timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)}s");
        }
        catch (HttpRequestException e)
        {
            // Composed from what this project owns. The published binary sets
            // UseSystemResourceKeys, so e.Message is a bare resource key rather than a sentence.
            var socket = Inner<SocketException>(e);

            return SendResult.Unreachable(
                socket is null ? "the request did not complete" : Describe(socket.SocketErrorCode),
                socket?.ErrorCode);
        }
        catch (Exception e) when (e is InvalidOperationException or UriFormatException or NotSupportedException)
        {
            return SendResult.Failed(400, "the target is not a usable URL");
        }
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;

        if (header?.Delta is { } delta)
        {
            return delta;
        }

        return header?.Date is { } date ? date - DateTimeOffset.UtcNow : null;
    }

    private static T? Inner<T>(Exception e) where T : Exception
    {
        for (var current = e; current is not null; current = current.InnerException)
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    private static string Describe(int status) => status switch
    {
        400 => "the endpoint rejected the request (400)",
        401 => "the credential was rejected (401)",
        403 => "the credential is not permitted here (403)",
        404 => "there is nothing at that URL (404)",
        408 => "the endpoint timed out (408)",
        413 => "the message was too large (413)",
        429 => "rate limited (429)",
        _ => $"the endpoint answered {status.ToString(CultureInfo.InvariantCulture)}",
    };

    private static string Describe(SocketError error) => error switch
    {
        SocketError.HostNotFound or SocketError.NoData => "the host name did not resolve",
        SocketError.ConnectionRefused => "the connection was refused",
        SocketError.TimedOut => "the connection timed out",
        SocketError.NetworkUnreachable or SocketError.HostUnreachable => "the host is unreachable",
        _ => "the connection failed",
    };

    public void Dispose()
    {
        _client.Dispose();
        _handler.Dispose();
    }
}
