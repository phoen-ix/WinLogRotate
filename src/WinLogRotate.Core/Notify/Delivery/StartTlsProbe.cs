using System.Net.Sockets;
using System.Text;

namespace WinLogRotate.Core.Notify.Delivery;

/// <summary>
/// What a relay said when asked: whether it offers STARTTLS, or why it could not be asked.
/// </summary>
/// <remarks>
/// <see cref="Offered"/> is null when the conversation never got as far as the answer - no
/// connection, no greeting, no reply in time - and <see cref="Error"/> then says which, in the
/// wording <see cref="SmtpNotifySender"/> would have used for the same failure on the send itself.
/// </remarks>
internal readonly record struct StartTlsAnswer(bool? Offered, string? Error, int? NativeError);

/// <summary>
/// A plaintext EHLO, to learn whether the relay offers STARTTLS before <c>SmtpClient</c> is told to
/// insist on it.
/// </summary>
/// <remarks>
/// <para>
/// <c>SmtpClient</c> exposes no capability API. With <c>EnableSsl</c> it reads the EHLO response
/// itself and throws a bare <c>SmtpException</c> when STARTTLS is absent; without it, it never
/// looks. So "opportunistic" - encrypt when the relay can, send in the clear and say so when it
/// cannot - is not expressible through the client alone. The sender used to fake it by matching the
/// exception's message for "secure connections", a phrase no branch of its own classifier produced,
/// so the downgrade never once happened: every relay without STARTTLS was a failed channel, and
/// after <c>breaker_after</c> nights a suppressed one. That is the documented common case - port
/// 25, an IP-authorised internal relay, the default <c>tls</c>.
/// </para>
/// <para>
/// One extra connection per send, spent out of the attempt's own timeout. The probe sends no
/// message and no credential - greeting, EHLO, QUIT - so the relay sees what a monitoring check
/// sends it. It is also why <c>tls = "required"</c> can name what was missing instead of reporting
/// a relay that "refused the message".
/// </para>
/// </remarks>
internal static class StartTlsProbe
{
    /// <summary>A reply that keeps going is not a reply; enough lines for any real EHLO.</summary>
    private const int MaxReplyLines = 64;

    /// <summary>Asks the relay, within <paramref name="timeout"/> for the whole conversation.</summary>
    public static StartTlsAnswer Ask(string host, int port, TimeSpan timeout)
    {
        try
        {
            using var client = new TcpClient();
            using var cancel = new CancellationTokenSource(timeout);
            client.ConnectAsync(host, port, cancel.Token).AsTask().GetAwaiter().GetResult();

            using var stream = client.GetStream();
            stream.ReadTimeout = Milliseconds(timeout);
            stream.WriteTimeout = Milliseconds(timeout);

            using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(stream, Encoding.ASCII, 1024, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\r\n",
            };

            var greeting = Reply(reader);

            if (greeting.Count == 0 || !greeting[0].StartsWith("220", StringComparison.Ordinal))
            {
                // Port 465 is the usual way to get here: implicit TLS, where the server expects a
                // handshake before it speaks. SmtpClient does not do implicit TLS at all, so the
                // remedy is the port, not the setting.
                return new StartTlsAnswer(null,
                    "the relay did not answer with an SMTP greeting - port 465 is implicit TLS, which "
                    + "is not supported; use 587 or 25", null);
            }

            writer.WriteLine($"EHLO {ClientName()}");
            var ehlo = Reply(reader);

            // A relay that does not speak ESMTP cannot offer STARTTLS. SmtpClient falls back to
            // HELO in the same situation and reaches the same conclusion.
            var offered = ehlo.Count > 0
                && ehlo[0].StartsWith("250", StringComparison.Ordinal)
                && ehlo.Any(line => line.Length > 4
                    && line.AsSpan(4).Trim().StartsWith("STARTTLS", StringComparison.OrdinalIgnoreCase));

            writer.WriteLine("QUIT");

            return new StartTlsAnswer(offered, null, null);
        }
        catch (SocketException e)
        {
            return new StartTlsAnswer(null, Describe(e.SocketErrorCode), e.ErrorCode);
        }
        catch (IOException e) when (e.InnerException is SocketException socket)
        {
            // A read timeout surfaces this way: the stream wraps the socket's TimedOut.
            return new StartTlsAnswer(null, Describe(socket.SocketErrorCode), socket.ErrorCode);
        }
        catch (IOException)
        {
            return new StartTlsAnswer(null, "the relay closed the connection", null);
        }
        catch (OperationCanceledException)
        {
            return new StartTlsAnswer(null, "the relay did not answer in time", null);
        }
        catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException or ArgumentException)
        {
            return new StartTlsAnswer(null, "the relay could not be reached", null);
        }
    }

    /// <summary>One SMTP reply: every <c>NNN-</c> continuation line up to and including the <c>NNN </c> last one.</summary>
    private static List<string> Reply(StreamReader reader)
    {
        var lines = new List<string>();

        while (lines.Count < MaxReplyLines && reader.ReadLine() is { } line)
        {
            lines.Add(line);

            if (line.Length < 4 || line[3] != '-')
            {
                break;
            }
        }

        return lines;
    }

    /// <summary>
    /// Something to say after EHLO. The machine name where it is a legal host name, and a fixed
    /// word otherwise - a relay wants an identifier, not an accurate one.
    /// </summary>
    private static string ClientName()
    {
        var name = Environment.MachineName;

        return name.Length > 0 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.')
            ? name
            : "winlogrotate";
    }

    /// <summary>
    /// Socket failures in this project's words, shared with the send itself so the two cannot
    /// describe one relay two ways.
    /// </summary>
    public static string Describe(SocketError error) => error switch
    {
        SocketError.HostNotFound or SocketError.NoData => "the relay's host name did not resolve",
        SocketError.ConnectionRefused => "the relay refused the connection",
        SocketError.TimedOut => "the relay did not answer in time",
        SocketError.NetworkUnreachable or SocketError.HostUnreachable => "the relay is unreachable",
        _ => "the relay could not be reached",
    };

    /// <summary>Stream timeouts are milliseconds as an int, and reject anything below one.</summary>
    private static int Milliseconds(TimeSpan timeout) =>
        (int)Math.Clamp(timeout.TotalMilliseconds, 1, int.MaxValue);
}
