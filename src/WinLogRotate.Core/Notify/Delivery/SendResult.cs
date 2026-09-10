namespace WinLogRotate.Core.Notify.Delivery;

/// <summary>What one attempt on one channel did.</summary>
/// <remarks>
/// <para>
/// <see cref="Status"/> is an HTTP status where there was one and <b>0 where the request never
/// completed</b> - a connection refused, a DNS answer that did not come, a TLS handshake rejected,
/// a timeout. That is the vocabulary <see cref="RetrySchedule.IsRetryable"/> already speaks, so
/// every transport maps onto it rather than each inventing its own idea of transience. SMTP and
/// the Event Log have no status codes; they report 0 for "could not", and 200 for "did".
/// </para>
/// <para>
/// <see cref="Error"/> is <b>already redacted and already ours</b>. It ends up in
/// <c>ChannelNotifyState.LastError</c>, which is written to a file that goes into support bundles,
/// and it is composed from wording this project owns rather than from an exception message: the
/// published binary sets <c>UseSystemResourceKeys=true</c>, so a framework exception's Message is a
/// bare resource key like <c>net_io_connectionclosed</c> rather than a sentence.
/// </para>
/// </remarks>
public readonly record struct SendResult
{
    public required bool Ok { get; init; }

    /// <summary>HTTP status, or 0 when the request never completed.</summary>
    public int Status { get; init; }

    /// <summary>What the server asked for, when it asked.</summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>One clause, safe to print and safe to store.</summary>
    public string? Error { get; init; }

    /// <summary>The underlying Win32/socket error, where the transport surfaced one.</summary>
    public int? NativeError { get; init; }

    /// <summary>
    /// Something the operator should know even though the send worked.
    /// </summary>
    /// <remarks>
    /// Exists for exactly one case: opportunistic SMTP that found no STARTTLS and sent in the
    /// clear. A downgrade nobody is told about is not opportunistic encryption, it is an
    /// unencrypted connection with a reassuring configuration key next to it.
    /// </remarks>
    public string? Note { get; init; }

    public static SendResult Delivered(int status = 200) => new() { Ok = true, Status = status };

    public static SendResult Failed(int status, string error, TimeSpan? retryAfter = null, int? nativeError = null) =>
        new() { Ok = false, Status = status, Error = error, RetryAfter = retryAfter, NativeError = nativeError };

    /// <summary>The request never completed. Retryable, and the channel is presumed down.</summary>
    public static SendResult Unreachable(string error, int? nativeError = null) =>
        new() { Ok = false, Status = 0, Error = error, NativeError = nativeError };
}

/// <summary>
/// One composed message, ready for any transport.
/// </summary>
/// <remarks>
/// <see cref="Subject"/> and <see cref="Body"/> are already redacted and already fitted to the
/// channel's limit, so every destination renders the same words - two channels disagreeing about
/// what happened is the failure that composing per transport produces. <see cref="Plan"/> and
/// <see cref="Run"/> come along for the webhook templater, which needs the individual fields.
/// </remarks>
public sealed record NotifyMessage
{
    public required string Subject { get; init; }

    public required string Body { get; init; }

    public required PlannedNotification Plan { get; init; }

    public required RunSummary Run { get; init; }
}

/// <summary>One transport.</summary>
/// <remarks>
/// Synchronous on purpose. The run verb is synchronous from <c>Main</c> down, channels are
/// attempted one at a time so the phase's wall-clock budget is a single subtraction rather than a
/// race, and <c>SmtpClient.Send</c> is synchronous anyway. Serial sending is also what makes
/// pinning an SMTP certificate through <c>ServicePointManager</c>'s process-global callback safe -
/// see <c>SmtpNotifySender</c>.
/// </remarks>
public interface INotifySender
{
    /// <summary>Sends one message. Never throws: every failure comes back as a result.</summary>
    /// <param name="timeout">All this attempt may spend, including connecting.</param>
    SendResult Send(ResolvedChannel channel, NotifyMessage message, TimeSpan timeout);
}

/// <summary>The sender for a scheme that is not a notification target.</summary>
/// <remarks>
/// Shipping code, not scaffolding. <c>command:</c>, <c>service:</c> and <c>event:</c> parse - they
/// are hooks, and they run as hooks - but they are not notification targets and will not become
/// them. A <c>[notify]</c> entry naming one therefore gets a refusal every run rather than silence,
/// because a target that quietly does nothing is indistinguishable from one that worked.
/// </remarks>
public sealed class UndeliveredScheme(string why) : INotifySender
{
    public SendResult Send(ResolvedChannel channel, NotifyMessage message, TimeSpan timeout) =>
        SendResult.Failed(400, why);
}
