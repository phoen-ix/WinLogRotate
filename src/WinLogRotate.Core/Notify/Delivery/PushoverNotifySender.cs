using System.Globalization;
using System.Net.Http;
using System.Net.Sockets;

namespace WinLogRotate.Core.Notify.Delivery;

/// <summary>
/// Pushover: a phone notification, for the case where an email at 03:00 is not enough.
/// </summary>
/// <remarks>
/// <para>
/// Two credentials, and they are different things: the <c>token</c> identifies the application and
/// the <c>user_key</c> identifies who gets buzzed. Both are resolved before this type sees them.
/// </para>
/// <para>
/// The 1024-character limit is enforced by <c>MessageComposer</c> before we get here, because
/// Pushover discards anything longer <i>silently</i> - a truncated message beats a message the
/// operator believes was sent.
/// </para>
/// <para>
/// This file is on the <c>Reveal()</c> allowlist: it is the transport that authenticates.
/// </para>
/// </remarks>
public sealed class PushoverNotifySender : INotifySender, IDisposable
{
    /// <summary>Fixed rather than configurable. A "Pushover" target that posts elsewhere is a trap.</summary>
    public const string Endpoint = "https://api.pushover.net/1/messages.json";

    private readonly HttpClient _client;
    private readonly HttpClientHandler _handler;

    public PushoverNotifySender(NotifySettings settings, string userAgent) =>
        (_client, _handler) = NotifyHttpClient.Create(settings, userAgent);

    public SendResult Send(ResolvedChannel channel, NotifyMessage message, TimeSpan timeout)
    {
        if (!channel.Credential.HasValue)
        {
            return SendResult.Failed(401, "no Pushover application token was resolved");
        }

        if (!channel.Target.HasValue)
        {
            return SendResult.Failed(400, "no Pushover user key was resolved");
        }

        var fields = new List<KeyValuePair<string, string>>
        {
            new("token", channel.Credential.Reveal()),
            new("user", channel.Target.Reveal()),
            new("title", Fit(message.Subject, 250)),
            new("message", message.Body),
        };

        if (channel.Provider?.Priority is { } priority and not 0)
        {
            fields.Add(new KeyValuePair<string, string>(
                "priority", Math.Clamp(priority, -2, 1).ToString(CultureInfo.InvariantCulture)));
        }

        try
        {
            using var cancel = new CancellationTokenSource(timeout);
            using var content = new FormUrlEncodedContent(fields);
            using var response = _client.Send(
                new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = content },
                HttpCompletionOption.ResponseHeadersRead,
                cancel.Token);

            var status = (int)response.StatusCode;

            return response.IsSuccessStatusCode
                ? SendResult.Delivered(status)
                : SendResult.Failed(status, Describe(status));
        }
        catch (OperationCanceledException)
        {
            return SendResult.Unreachable(
                $"no response within {timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)}s");
        }
        catch (HttpRequestException e)
        {
            var socket = e.InnerException as SocketException;
            return SendResult.Unreachable(
                socket is null ? "the request did not complete" : "the connection failed",
                socket?.ErrorCode);
        }
    }

    /// <summary>Pushover's own title limit. Silently truncated there too.</summary>
    private static string Fit(string text, int limit) =>
        text.Length <= limit ? text : text[..limit];

    private static string Describe(int status) => status switch
    {
        // Pushover answers 4xx with a JSON body naming the bad field; deliberately not parsed,
        // because the body can echo the token back and this string is stored in notify.json.
        400 => "Pushover rejected the request - check the user key (400)",
        401 => "Pushover rejected the application token (401)",
        429 => "the Pushover monthly limit is exhausted (429)",
        _ => $"Pushover answered {status.ToString(CultureInfo.InvariantCulture)}",
    };

    public void Dispose()
    {
        _client.Dispose();
        _handler.Dispose();
    }
}
