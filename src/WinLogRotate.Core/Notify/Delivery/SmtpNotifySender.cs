using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Security.Authentication;

namespace WinLogRotate.Core.Notify.Delivery;

/// <summary>
/// Email, through a relay or a pickup directory.
/// </summary>
/// <remarks>
/// <para>
/// <b>Shelf life.</b> <see cref="SmtpClient"/> has no OAuth2, so <c>auth = "login"</c> against
/// Exchange Online stops working when Microsoft disables basic authentication for SMTP at the end
/// of December 2026. Internal relays, IP-authorised relays, <c>delivery = "pickup"</c> and
/// <c>auth = "integrated"</c> are unaffected - none of them ever used it. The documentation says so
/// and points Microsoft 365 tenants at a webhook.
/// </para>
/// <para>
/// <b>Pinning goes through a process-global.</b> <see cref="SmtpClient"/> exposes no per-client
/// certificate callback; the only hook it reads is
/// <c>ServicePointManager.ServerCertificateValidationCallback</c>, which it hands to its
/// <c>SslStream</c> - see <see cref="SmtpCertificatePin"/> for why that still works despite the
/// obsoletion saying otherwise. So a pin is installed and restored in a <c>try</c>/<c>finally</c>
/// immediately around the send. That is safe only because the dispatcher sends strictly serially -
/// it is a load-bearing invariant, not an incidental one, and
/// <c>TheGlobalSmtpCallbackIsAlwaysRestored</c> pins it.
/// </para>
/// <para>
/// This file is on the <c>Reveal()</c> allowlist: it is the transport that authenticates.
/// </para>
/// </remarks>
public sealed class SmtpNotifySender : INotifySender
{
    public SendResult Send(ResolvedChannel channel, NotifyMessage message, TimeSpan timeout)
    {
        var provider = channel.Provider;

        if (provider is null)
        {
            return SendResult.Failed(400,
                "an smtp: target needs a [notify.email.*] provider naming the relay");
        }

        if (provider.Delivery == SmtpDelivery.PickupDirectory)
        {
            return ToPickupDirectory(provider, channel, message);
        }

        if (string.IsNullOrWhiteSpace(provider.Host))
        {
            return SendResult.Failed(400, "the email provider names no host");
        }

        // Opportunistic first; if the relay offers no STARTTLS the send is retried in the clear
        // and says so. "Required" never retries.
        var result = Deliver(provider, channel, message, timeout, tls: provider.Tls != SmtpTls.None);

        if (result.Ok || provider.Tls != SmtpTls.Opportunistic || !result.Error!.Contains(
                "secure connections", StringComparison.OrdinalIgnoreCase))
        {
            return result;
        }

        var plain = Deliver(provider, channel, message, timeout, tls: false);

        return plain.Ok
            ? plain with
            {
                Note = $"{provider.Host} offers no STARTTLS, so the message was sent unencrypted. "
                     + "Set tls = \"required\" to refuse instead.",
            }
            : plain;
    }

    private static SendResult Deliver(
        NotifyProvider provider, ResolvedChannel channel, NotifyMessage message,
        TimeSpan timeout, bool tls)
    {
        var pin = TlsPinning.CallbackFor(channel.Pin);

        var restore = SmtpCertificatePin.Current;

        try
        {
            if (pin is not null)
            {
                SmtpCertificatePin.Current = pin;
            }

            using var client = new SmtpClient(provider.Host, provider.Port)
            {
                EnableSsl = tls,
                Timeout = Milliseconds(timeout),
                DeliveryMethod = SmtpDeliveryMethod.Network,
            };

            switch (provider.Auth)
            {
                case SmtpAuth.Integrated:
                    // The machine account under LocalSystem: DOMAIN\HOST$. No credential to store,
                    // and the best option on a domain-joined relay.
                    client.UseDefaultCredentials = true;
                    break;

                case SmtpAuth.Login:
                    if (!channel.Credential.HasValue)
                    {
                        return SendResult.Failed(401, "no password was resolved for this relay");
                    }

                    client.UseDefaultCredentials = false;
                    client.Credentials = new NetworkCredential(
                        provider.Username ?? provider.From, channel.Credential.Reveal());
                    break;

                default:
                    client.UseDefaultCredentials = false;
                    break;
            }

            using var mail = Compose(provider, channel, message);
            client.Send(mail);

            return SendResult.Delivered();
        }
        catch (SmtpException e)
        {
            return Classify(e);
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or ArgumentException)
        {
            return SendResult.Failed(400, "the email provider is not usable as configured");
        }
        finally
        {
            SmtpCertificatePin.Current = restore;
        }
    }

    private static SendResult ToPickupDirectory(
        NotifyProvider provider, ResolvedChannel channel, NotifyMessage message)
    {
        // No network call at all, so no timeout can wedge a rotation and no credential is stored.
        // The best option wherever IIS SMTP or an Exchange transport is already dropping files.
        if (string.IsNullOrWhiteSpace(provider.PickupDirectory))
        {
            return SendResult.Failed(400, "delivery = \"pickup\" needs a pickup_directory");
        }

        try
        {
            using var client = new SmtpClient
            {
                DeliveryMethod = SmtpDeliveryMethod.SpecifiedPickupDirectory,
                PickupDirectoryLocation = provider.PickupDirectory,
            };

            using var mail = Compose(provider, channel, message);
            client.Send(mail);

            return SendResult.Delivered();
        }
        catch (SmtpException)
        {
            return SendResult.Failed(400, $"the pickup directory {provider.PickupDirectory} could not be written");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                    or DirectoryNotFoundException or FormatException or ArgumentException)
        {
            return SendResult.Failed(400, $"the pickup directory {provider.PickupDirectory} could not be written");
        }
    }

    private static MailMessage Compose(
        NotifyProvider provider, ResolvedChannel channel, NotifyMessage message)
    {
        var from = provider.From ?? $"winlogrotate@{message.Run.Machine}";
        var subject = provider.SubjectPrefix is { Length: > 0 } prefix
            ? $"{prefix} {message.Subject}"
            : message.Subject;

        var mail = new MailMessage
        {
            From = new MailAddress(from),

            // Header injection is impossible by construction: MailMessage refuses a control
            // character in Subject, and the body is a separate field rather than text appended
            // after a blank line.
            Subject = subject,
            Body = message.Body,
            IsBodyHtml = false,
        };

        // The provider's recipients, plus the one the target named: `smtp:ops@example.com` is a
        // complete instruction on its own, and a provider with a standing `to` list is the other
        // way people write it.
        foreach (var recipient in Recipients(provider, channel))
        {
            mail.To.Add(recipient);
        }

        return mail;
    }

    private static IEnumerable<string> Recipients(NotifyProvider provider, ResolvedChannel channel)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var address in provider.To)
        {
            if (seen.Add(address))
            {
                yield return address;
            }
        }

        if (channel.Action.Scheme == HookScheme.Smtp
            && channel.Action.Target.Contains('@', StringComparison.Ordinal)
            && seen.Add(channel.Action.Target))
        {
            yield return channel.Action.Target;
        }
    }

    /// <summary>
    /// Maps SMTP failures onto the vocabulary <see cref="RetrySchedule"/> speaks.
    /// </summary>
    /// <remarks>
    /// Composed from <see cref="SmtpException.StatusCode"/> and wording this project owns, never
    /// from <c>Message</c>: the published binary sets <c>UseSystemResourceKeys</c>, so a framework
    /// exception's message is a bare resource key such as <c>net_io_connectionclosed</c>.
    /// </remarks>
    private static SendResult Classify(SmtpException e) => e.StatusCode switch
    {
        SmtpStatusCode.MailboxBusy or SmtpStatusCode.TransactionFailed
            or SmtpStatusCode.LocalErrorInProcessing or SmtpStatusCode.InsufficientStorage =>
            SendResult.Failed(503, "the relay is temporarily refusing mail"),

        SmtpStatusCode.MailboxUnavailable or SmtpStatusCode.MailboxNameNotAllowed =>
            SendResult.Failed(400, "the relay rejected the recipient"),

        SmtpStatusCode.ClientNotPermitted or SmtpStatusCode.MustIssueStartTlsFirst =>
            SendResult.Failed(401, "the relay rejected the credential"),

        SmtpStatusCode.ServiceNotAvailable =>
            SendResult.Unreachable("the relay is not accepting connections"),

        SmtpStatusCode.GeneralFailure when e.InnerException is not null =>
            SendResult.Unreachable(Describe(e)),

        _ => SendResult.Failed(500, "the relay refused the message"),
    };

    private static string Describe(SmtpException e)
    {
        for (var inner = e.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is System.Net.Sockets.SocketException socket)
            {
                return socket.SocketErrorCode switch
                {
                    System.Net.Sockets.SocketError.HostNotFound => "the relay's host name did not resolve",
                    System.Net.Sockets.SocketError.ConnectionRefused => "the relay refused the connection",
                    System.Net.Sockets.SocketError.TimedOut => "the relay did not answer in time",
                    _ => "the relay could not be reached",
                };
            }

            if (inner is AuthenticationException)
            {
                return "the relay's certificate was rejected - check server_cert_thumbprint";
            }
        }

        return "the relay could not be reached";
    }

    /// <summary>
    /// <see cref="SmtpClient.Timeout"/> is milliseconds as an int, and rejects anything negative.
    /// </summary>
    private static int Milliseconds(TimeSpan timeout) =>
        (int)Math.Clamp(timeout.TotalMilliseconds, 1_000, int.MaxValue);
}
