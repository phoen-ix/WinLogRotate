namespace WinLogRotate.Core.Notify.Delivery;

/// <summary>
/// The one place a transport is called, and the place its contract is enforced.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="INotifySender.Send"/> promises never to throw, and until this existed the promise was
/// taken on trust. The SMTP sender broke it without meaning to: .NET rethrows a certificate
/// rejection unwrapped, past a catch written for <c>SmtpException</c>, so a wrong
/// <c>server_cert_thumbprint</c> - the one case pinning exists for - travelled out of the phase and
/// into the safety net. LR1006, exit 4, the notification state never saved, for a rotation that had
/// completed. docs/notifications.md says delivery never changes a run's exit code.
/// </para>
/// <para>
/// The interface is an extension point, so fixing the one sender is not enough: the next transport
/// will have its own runtime surprise. Whatever a transport throws is a failure of that transport,
/// reported as unreachable so it is retried within the budget, abandoned for the run, and counted
/// by the breaker like any other outage. The exception's type is kept and its message is not - under
/// <c>UseSystemResourceKeys</c> a framework message is a bare resource key, and this string is
/// stored in notify.json.
/// </para>
/// </remarks>
public static class SendGuard
{
    /// <summary>Sends one message, turning anything the transport throws into a result.</summary>
    public static SendResult Send(
        INotifySender sender, ResolvedChannel channel, NotifyMessage message, TimeSpan timeout)
    {
        try
        {
            return sender.Send(channel, message, timeout);
        }
        catch (Exception e)
        {
            return SendResult.Unreachable($"the transport failed with {e.GetType().Name}");
        }
    }
}
