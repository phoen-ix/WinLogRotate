using System.Globalization;
using System.Net.Http;
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
    private readonly TimeProvider _clock;

    /// <param name="clock">
    /// What a date-form <c>Retry-After</c> is measured against. It was <c>DateTimeOffset.UtcNow</c>,
    /// the one clock in the notification phase that nothing could fake.
    /// </param>
    public HttpNotifySender(NotifySettings settings, string userAgent, TimeProvider? clock = null)
    {
        (_client, _handler) = NotifyHttpClient.Create(settings, userAgent);
        _clock = clock ?? TimeProvider.System;
    }

    public SendResult Send(ResolvedChannel channel, NotifyMessage message, TimeSpan timeout)
    {
        var provider = channel.Provider;
        var contentType = provider?.ContentType ?? "application/json";

        // The resolver drops a webhook with no url before it gets here, so this is the last line
        // rather than the first. It used to be a catch around Reveal(), which never throws - the
        // empty target went on to build a request with no URI, and the exception that produced
        // was read as a 400 about the message.
        if (!channel.Target.HasValue)
        {
            return SendResult.Failed(400, "no URL was resolved for this target");
        }

        var url = channel.Target.Reveal();

        var body = NotifyBody.Render(
            provider?.Body, contentType, message.Plan, message.Subject, message.Body, message.Run, message.Redact);

        try
        {
            // Inside the try, both of them. HttpMethod and StringContent throw FormatException for
            // a method that is not a token and a content type that is not a media type; the
            // binder now refuses both at load time, and this is what stops a value it did not
            // think of from leaving the phase as exit 4.
            var method = new HttpMethod((provider?.Method ?? "POST").ToUpperInvariant());

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

            return status switch
            {
                // About the message, and the next one may well go: the digest is bigger than this
                // endpoint takes, or a templated body was not what the endpoint could parse -
                // which depends on what was substituted, so only a provider with a template gets
                // that reading. The same 400 against the default flat object is the endpoint
                // saying it does not want our payload at all, and no message will change that.
                413 => SendResult.Refused(status, Describe(status)),
                400 or 422 when provider?.Body is { Length: > 0 } => SendResult.Refused(status, Describe(status)),

                _ => SendResult.Failed(status, Describe(status), RetryAfter(response, _clock)),
            };
        }
        catch (OperationCanceledException)
        {
            // Includes the timeout: HttpClient surfaces a cancelled token, not a timeout type.
            return SendResult.Unreachable(
                $"no response within {timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)}s");
        }
        catch (HttpRequestException e)
        {
            var failure = NotifyHttpClient.Describe(e);
            return SendResult.Unreachable(failure.Error, failure.NativeError);
        }
        catch (FormatException)
        {
            return SendResult.Failed(400, "the provider's method or content_type is not usable");
        }
        catch (Exception e) when (e is InvalidOperationException or UriFormatException or NotSupportedException)
        {
            return SendResult.Failed(400, "the target is not a usable URL");
        }
    }

    /// <summary>
    /// What the server asked us to wait, never less than nothing.
    /// </summary>
    /// <remarks>
    /// A date-form header is a moment, and it can already be behind the clock by the time it is
    /// read. The negative span that produced was handed on as it came, and <c>Thread.Sleep</c>
    /// throws for anything below -1 ms - so a stale header ended the run with exit 4. Measured
    /// against the injected clock so a test can say what "now" is.
    /// </remarks>
    internal static TimeSpan? RetryAfter(HttpResponseMessage response, TimeProvider clock)
    {
        var header = response.Headers.RetryAfter;

        if (header?.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (header?.Date is not { } date)
        {
            return null;
        }

        var wait = date - clock.GetUtcNow();
        return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
    }

    private static string Describe(int status) => status switch
    {
        400 => "the endpoint rejected the request (400)",
        401 => "the credential was rejected (401)",
        403 => "the credential is not permitted here (403)",
        404 => "there is nothing at that URL (404)",
        408 => "the endpoint timed out (408)",
        413 => "the message was too large (413)",
        422 => "the endpoint could not use the request body (422)",
        429 => "rate limited (429)",
        _ => $"the endpoint answered {status.ToString(CultureInfo.InvariantCulture)}",
    };

    public void Dispose()
    {
        _client.Dispose();
        _handler.Dispose();
    }
}
