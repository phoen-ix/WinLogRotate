using System.Net.Security;

namespace WinLogRotate.Core.Notify.Delivery;

/// <summary>
/// The only way to pin an SMTP server's certificate, and the one place SYSLIB0014 is suppressed.
/// </summary>
/// <remarks>
/// <para>
/// <c>SmtpClient</c> exposes no per-client certificate callback. The only hook it reads is
/// <c>ServicePointManager.ServerCertificateValidationCallback</c>, which it passes to the
/// <c>SslStream</c> it creates during STARTTLS.
/// </para>
/// <para>
/// The obsoletion says "Settings on ServicePointManager no longer affect SslStream or HttpClient",
/// which reads like this cannot work. It is accurate about an <c>SslStream</c> you construct
/// yourself and about <c>HttpClient</c>, and misleading here: <c>SmtpClient</c> constructs the
/// stream itself and hands it this delegate. Verified against .NET 10.0.11 with a local STARTTLS
/// listener presenting a self-signed certificate - the callback is invoked with
/// <c>RemoteCertificateChainErrors</c>, and returning true completes a handshake that default
/// validation refuses. If a future runtime stops honouring it, pinning silently becomes
/// unenforced, so <c>APinnedRelayCertificateIsActuallyChecked</c> exercises this path end to end
/// rather than trusting the property.
/// </para>
/// <para>
/// It is process-global, so it is installed and removed around a single send. That is safe only
/// because the dispatcher sends strictly serially - a load-bearing invariant, not an incidental
/// one.
/// </para>
/// </remarks>
internal static class SmtpCertificatePin
{
#pragma warning disable SYSLIB0014 // The only hook SmtpClient reads. See the remarks above.
    public static RemoteCertificateValidationCallback? Current
    {
        get => System.Net.ServicePointManager.ServerCertificateValidationCallback;
        set => System.Net.ServicePointManager.ServerCertificateValidationCallback = value;
    }
#pragma warning restore SYSLIB0014
}
