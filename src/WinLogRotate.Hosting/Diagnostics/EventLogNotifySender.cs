using System.Runtime.Versioning;
using WinLogRotate.Contracts;
using WinLogRotate.Core.Notify.Delivery;

namespace WinLogRotate.Hosting.Diagnostics;

/// <summary>
/// The <c>eventlog:</c> notification target.
/// </summary>
/// <remarks>
/// <para>
/// The one channel with no credential, no network and no way for an outage elsewhere to silence
/// it, which is why it is the sensible first target on a machine that has nothing else set up.
/// </para>
/// <para>
/// It is <b>not</b> the same thing as the Event Log sink. That mirrors individual diagnostics at
/// Warning and above as they happen; this delivers the digest the planner decided to send, under
/// its own event ID, so an alert rule can name one without matching the other.
/// </para>
/// <para>
/// It lives in Hosting rather than beside the other senders because it is a P/Invoke into
/// advapi32, and Core is platform-neutral. <c>SenderTable</c> exists so that one entry can come
/// from a different assembly.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class EventLogNotifySender(string source) : INotifySender
{
    public SendResult Send(ResolvedChannel channel, NotifyMessage message, TimeSpan timeout)
    {
        if (!EventLogWriter.IsRegistered(source))
        {
            // Expected on a per-user install, which registers no source because that needs
            // administrator. Reported as a plain failure rather than an exception, and 400 rather
            // than 0 so it is never retried - it will not become true within the run.
            return SendResult.Failed(400,
                "the Event Log source is not registered, which is normal for a per-user install");
        }

        var severity = message.Plan.Severity < Severity.Warning ? Severity.Warning : message.Plan.Severity;

        var text = message.Subject + Environment.NewLine + Environment.NewLine + message.Body;

        // WriteDigest, not TryWrite: the digests have their own allowance. Sharing the mirrored
        // diagnostics' fifty meant that on a night bad enough to produce fifty of them - which is
        // the only night this channel matters - the digest arrived over budget, and was reported
        // as delivered because the allowance's announcement had been written in its place.
        //
        // Three outcomes rather than a bool, because they have three different fixes and an
        // operator shown the wrong one goes looking in the wrong place.
        return EventLogWriter.WriteDigest(source, severity, text) switch
        {
            EventLogOutcome.Written => SendResult.Delivered(),

            // Cannot become true within this run: the handle is opened once and the failure
            // memoised, so retrying would spend the run's shared deadline to fail identically.
            EventLogOutcome.Unavailable => SendResult.Failed(400,
                "the Event Log source is not registered, which is normal for a per-user install"),

            // An answer, not a silence - which is what 0 is for. The body is already fitted to
            // the Event Log's insertion-string limit, so a refusal is most plausibly about this
            // one message rather than about the channel, and 4xx is where that belongs.
            EventLogOutcome.Refused => SendResult.Failed(400, "the Event Log refused the entry"),

            // Unreachable today - the digests have no ceiling - and kept honest rather than
            // folded into the refusal, so that giving them one later cannot quietly resurrect
            // "delivered" for a message nothing wrote.
            _ => SendResult.Failed(400,
                "this run's Event Log allowance was already spent, so the digest was not written"),
        };
    }
}
