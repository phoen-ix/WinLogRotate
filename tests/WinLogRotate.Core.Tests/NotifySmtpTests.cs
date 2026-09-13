using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Shouldly;
using WinLogRotate.Contracts;
using WinLogRotate.Core.Notify;
using WinLogRotate.Core.Notify.Delivery;
using WinLogRotate.Core.State;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// The suites that install the process-global SMTP certificate callback, run one at a time.
/// </summary>
/// <remarks>
/// <see cref="SmtpCertificatePin"/> is <c>ServicePointManager.ServerCertificateValidationCallback</c>,
/// which is one slot per process. xunit runs one collection per class in parallel, so a test in
/// another class setting the callback to "accept anything" between a sender installing its pin
/// and the handshake reading it would make a mismatched pin pass - only sometimes.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class SmtpCallbackCollection
{
    public const string Name = "installs the smtp certificate callback";
}

/// <summary>
/// The SMTP transport against a relay that actually answers.
/// </summary>
/// <remarks>
/// <para>
/// Every other sender test either has nothing listening or hands the dispatcher a fake, and
/// neither reaches the code that decides what a real relay's behaviour means. A relay without
/// STARTTLS, a relay presenting a certificate the operator did not pin, a relay presenting the one
/// they did - these are the cases the transport exists for, and they need a socket to be answered
/// on. The relay here binds loopback on an ephemeral port and speaks the minimum SmtpClient needs.
/// </para>
/// <para>
/// All of it runs on the Linux leg: nothing here touches the Event Log or a Windows-only API.
/// </para>
/// </remarks>
[Collection(SmtpCallbackCollection.Name)]
public sealed class NotifySmtpTests
{
    /// <summary>Generous, because a hung handshake should fail the test rather than the suite.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A relay on loopback that speaks just enough SMTP for <c>SmtpClient</c> to hand it a message.
    /// </summary>
    /// <remarks>
    /// Every connection is served on its own task, so a client that hangs up mid-conversation - a
    /// refused handshake, most often - ends that conversation and nothing else. The counters are
    /// what the tests assert on: how many times the wire was opened, how many handshakes finished,
    /// and what arrived after DATA.
    /// </remarks>
    private sealed class FakeSmtpRelay : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly X509Certificate2? _certificate;
        private readonly bool _offersStartTls;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private int _connections;
        private int _handshakes;

        public FakeSmtpRelay(bool offersStartTls, X509Certificate2? certificate = null)
        {
            _offersStartTls = offersStartTls;
            _certificate = certificate;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _loop = Task.Run(AcceptLoop);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        /// <summary>Everything that arrived between DATA and the terminating dot.</summary>
        public ConcurrentQueue<string> Received { get; } = new();

        /// <summary>Connections opened, encrypted or not.</summary>
        public int Connections => Volatile.Read(ref _connections);

        /// <summary>Connections on which a TLS handshake completed.</summary>
        public int Handshakes => Volatile.Read(ref _handshakes);

        private async Task AcceptLoop()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }

                Interlocked.Increment(ref _connections);
                _ = Task.Run(() => Serve(client));
            }
        }

        private async Task Serve(TcpClient client)
        {
            using var owned = client;
            using var deadline = new CancellationTokenSource(Timeout);
            var token = deadline.Token;

            Stream stream = client.GetStream();
            var reader = Reader(stream);
            var writer = Writer(stream);
            var secured = false;

            try
            {
                await writer.WriteLineAsync("220 fake.relay.test ESMTP");

                while (await reader.ReadLineAsync(token) is { } line)
                {
                    var verb = line.Split(' ', 2)[0].ToUpperInvariant();

                    switch (verb)
                    {
                        case "EHLO":
                            await writer.WriteLineAsync("250-fake.relay.test");
                            if (_offersStartTls && !secured)
                            {
                                await writer.WriteLineAsync("250-STARTTLS");
                            }

                            await writer.WriteLineAsync("250 OK");
                            break;

                        case "HELO":
                            await writer.WriteLineAsync("250 fake.relay.test");
                            break;

                        case "STARTTLS":
                            if (!_offersStartTls || _certificate is null || secured)
                            {
                                await writer.WriteLineAsync("454 TLS not available");
                                break;
                            }

                            await writer.WriteLineAsync("220 Ready to start TLS");
                            var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                            await ssl.AuthenticateAsServerAsync(
                                _certificate, clientCertificateRequired: false, checkCertificateRevocation: false);
                            Interlocked.Increment(ref _handshakes);
                            stream = ssl;
                            secured = true;
                            reader = Reader(stream);
                            writer = Writer(stream);
                            break;

                        case "MAIL" or "RCPT" or "NOOP" or "RSET":
                            await writer.WriteLineAsync("250 OK");
                            break;

                        case "DATA":
                            await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                            var body = new StringBuilder();
                            while (await reader.ReadLineAsync(token) is { } data && data != ".")
                            {
                                body.Append(data).Append("\r\n");
                            }

                            Received.Enqueue(body.ToString());
                            await writer.WriteLineAsync("250 OK queued");
                            break;

                        case "QUIT":
                            await writer.WriteLineAsync("221 Bye");
                            return;

                        default:
                            await writer.WriteLineAsync("502 Command not implemented");
                            break;
                    }
                }
            }
            catch (Exception e) when (e is IOException or AuthenticationException or OperationCanceledException
                                        or ObjectDisposedException or SocketException)
            {
                // The client hung up, or refused the handshake. Several tests are about exactly
                // that, and the relay's job is to keep answering the next connection.
            }
        }

        private static StreamReader Reader(Stream stream) =>
            new(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, 1024, leaveOpen: true);

        private static StreamWriter Writer(Stream stream) =>
            new(stream, Encoding.ASCII, 1024, leaveOpen: true) { AutoFlush = true, NewLine = "\r\n" };

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();

            try
            {
                _loop.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
                // Already stopped, or refusing to; either way the test is over.
            }

            _stop.Dispose();
        }
    }

    /// <summary>
    /// A certificate SslStream can present on every leg.
    /// </summary>
    /// <remarks>
    /// Exported and reloaded rather than used as created: SChannel cannot use an ephemeral private
    /// key for server authentication, and the round trip through PKCS#12 is what gives the key a
    /// container on Windows. On Linux it is a no-op with the same result.
    /// </remarks>
    private static X509Certificate2 SelfSigned()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=fake.relay.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], critical: false));

        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        san.AddDnsName("fake.relay.test");
        request.CertificateExtensions.Add(san.Build());

        var now = DateTimeOffset.UtcNow;
        using var ephemeral = request.CreateSelfSigned(now.AddDays(-1), now.AddDays(1));

        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12), password: null);
    }

    private static string ThumbprintOf(X509Certificate2 certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));

    private static SendResult Send(FakeSmtpRelay relay, SmtpTls tls, string? pin) =>
        Send(relay.Port, tls, pin, Timeout);

    private static SendResult Send(int port, SmtpTls tls, string? pin, TimeSpan timeout)
    {
        var provider = new NotifyProvider
        {
            Name = "email.relay",
            Kind = NotifyProviderKind.Email,
            Host = "127.0.0.1",
            Port = port,
            Tls = tls,
            From = "winlogrotate@example.test",
            To = ["ops@example.test"],
        };

        var channel = new ResolvedChannel
        {
            Action = HookAction.Create(HookScheme.Smtp, "email.relay", "email.relay", provider: "email.relay"),
            Provider = provider,
            Pin = pin,
        };

        var run = new RunSummary
        {
            RunId = "r1",
            Machine = "TESTBOX",
            JobsRun = 1,
            Completed = 0,
            BytesFreed = 0,
            ObservedJobs = [],
        };

        DigestLine[] lines =
        [
            new()
            {
                Severity = Severity.Error,
                Code = DiagnosticCode.FileLocked,
                Job = "iis",
                Text = "Access denied",
                Where = @"C:\logs",
                DirectoryKey = @"c:\logs",
                Count = 1,
            },
        ];

        var plan = new PlannedNotification
        {
            Reason = NotifyReason.NewFailure,
            Job = "iis",
            Severity = Severity.Error,
            Subject = "[WinLogRotate] FAILED on TESTBOX - iis",
            Lines = lines,
            Context = lines,
            Fingerprint = "abc",
            NextState = new JobNotifyState(),
        };

        return new SmtpNotifySender().Send(
            channel,
            new NotifyMessage { Subject = plan.Subject, Body = "body", Plan = plan, Run = run },
            timeout);
    }

    // ---- opportunistic tls ------------------------------------------------------------------

    /// <summary>
    /// The documented downgrade: no STARTTLS on offer, so the message goes in the clear and says so.
    /// </summary>
    /// <remarks>
    /// This never happened. The sender retried in the clear only when the error text contained
    /// "secure connections", and no branch of its own classifier produced that phrase - so a relay
    /// without STARTTLS was a failed channel every night and, after breaker_after nights, a
    /// suppressed one. That is the common case the documentation describes: port 25, an internal
    /// relay that authorises by IP, the default tls. SmtpClient has no capability API, so the sender
    /// now asks the relay itself with a plaintext EHLO before deciding whether to insist.
    /// </remarks>
    [Fact]
    public void OpportunisticTlsSendsInTheClearWhenTheRelayOffersNoStartTlsAndSaysSo()
    {
        using var relay = new FakeSmtpRelay(offersStartTls: false);

        var result = Send(relay, SmtpTls.Opportunistic, pin: null);

        result.Ok.ShouldBeTrue(result.Error);
        result.Note.ShouldNotBeNull().ShouldContain("STARTTLS");
        relay.Handshakes.ShouldBe(0);
        relay.Received.ShouldHaveSingleItem();

        // One connection to ask, one to send. The cost is stated so that it is a decision.
        relay.Connections.ShouldBe(2);
    }

    [Fact]
    public void OpportunisticTlsStillEncryptsWhenTheRelayOffersIt()
    {
        // The downgrade is the exception, not the rule: a relay that offers STARTTLS gets it, and
        // nothing is said, because nothing surprising happened.
        using var certificate = SelfSigned();
        using var relay = new FakeSmtpRelay(offersStartTls: true, certificate);

        var result = Send(relay, SmtpTls.Opportunistic, ThumbprintOf(certificate));

        result.Ok.ShouldBeTrue(result.Error);
        result.Note.ShouldBeNull();
        relay.Handshakes.ShouldBe(1);
    }

    [Fact]
    public void RequiredTlsRefusesARelayWithoutStartTlsAndNamesTheReason()
    {
        // Required means required. And it says what was missing, rather than "the relay refused
        // the message" - which is what SmtpClient's bare GeneralFailure used to be read as.
        using var relay = new FakeSmtpRelay(offersStartTls: false);

        var result = Send(relay, SmtpTls.Required, pin: null);

        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldContain("STARTTLS");
        relay.Received.ShouldBeEmpty("nothing may go in the clear when the operator said required");
    }

    [Fact]
    public void ARelayThatIsNotListeningIsUnreachable()
    {
        // A free port: bound, read, released. Nothing else is listening on it by the time the
        // sender connects, and a refused connection is the outcome that matters here.
        int port;
        using (var free = new TcpListener(IPAddress.Loopback, 0))
        {
            free.Start();
            port = ((IPEndPoint)free.LocalEndpoint).Port;
            free.Stop();
        }

        var result = Send(port, SmtpTls.Opportunistic, pin: null, Timeout);

        result.Ok.ShouldBeFalse();
        result.Status.ShouldBe(0, "a connection that never completed is unreachable, and retryable");
    }

    /// <summary>
    /// A relay that accepts and never speaks is a timeout, and a timeout is "unreachable".
    /// </summary>
    /// <remarks>
    /// SmtpClient reports its own timeout as an SmtpException with GeneralFailure and no inner
    /// exception, and that landed in the classifier's fallthrough arm as "the relay refused the
    /// message" - status 500, and a sentence about a relay that had said nothing at all.
    /// </remarks>
    [Fact]
    public async Task ARelayThatNeverAnswersIsUnreachableNotARefusal()
    {
        using var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();

        // Accepted and then ignored, which is what a wedged relay looks like from here.
        var held = silent.AcceptTcpClientAsync(TestContext.Current.CancellationToken);

        var result = Send(
            ((IPEndPoint)silent.LocalEndpoint).Port, SmtpTls.None, pin: null, TimeSpan.FromSeconds(1));

        result.Ok.ShouldBeFalse();
        result.Status.ShouldBe(0);

        if (held.IsCompletedSuccessfully)
        {
            (await held).Dispose();
        }
    }

    // ---- the pin ----------------------------------------------------------------------------

    /// <summary>
    /// The test <see cref="SmtpCertificatePin"/> cites, end to end.
    /// </summary>
    /// <remarks>
    /// The obsoletion on <c>ServicePointManager</c> says its settings no longer affect
    /// <c>SslStream</c>. <c>SmtpClient</c> is the exception - it hands the callback to the stream
    /// it constructs - and if a future runtime stops doing so, pinning becomes decoration without
    /// anything failing. A self-signed certificate is refused by default validation, so a send that
    /// completes proves the pin was consulted and honoured.
    /// </remarks>
    [Fact]
    public void APinnedRelayCertificateIsActuallyChecked()
    {
        using var certificate = SelfSigned();
        using var relay = new FakeSmtpRelay(offersStartTls: true, certificate);

        var result = Send(relay, SmtpTls.Required, ThumbprintOf(certificate));

        result.Ok.ShouldBeTrue(result.Error);
        relay.Handshakes.ShouldBe(1, "the message must have travelled over TLS");
        relay.Received.ShouldHaveSingleItem().ShouldContain("ops@example.test");
    }

    /// <summary>
    /// A wrong pin is a delivery failure, not an exception.
    /// </summary>
    /// <remarks>
    /// <c>SmtpClient.Send</c> deliberately rethrows <c>AuthenticationException</c> unwrapped, and
    /// the sender caught only <c>SmtpException</c>. So the one scenario pinning exists for - a
    /// certificate that is not the one expected - escaped <c>Send</c>, went through the dispatcher
    /// and the phase, and turned a completed rotation into exit 4 with the notification state
    /// unsaved. docs/notifications.md promises that delivery never changes a run's exit code.
    /// </remarks>
    [Fact]
    public void AMismatchedPinFailsTheSendWithoutThrowing()
    {
        using var certificate = SelfSigned();
        using var impostor = SelfSigned();
        using var relay = new FakeSmtpRelay(offersStartTls: true, certificate);

        var result = Send(relay, SmtpTls.Required, ThumbprintOf(impostor));

        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldContain("server_cert_thumbprint");
        relay.Received.ShouldBeEmpty("nothing may be sent over a connection whose peer was rejected");
    }

    /// <summary>
    /// With no pin the operating system decides, and a self-signed relay is refused - quietly.
    /// </summary>
    [Fact]
    public void AnUnpinnedSelfSignedRelayIsRefusedWithoutThrowing()
    {
        using var certificate = SelfSigned();
        using var relay = new FakeSmtpRelay(offersStartTls: true, certificate);

        var result = Send(relay, SmtpTls.Required, pin: null);

        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNullOrWhiteSpace();
        relay.Received.ShouldBeEmpty();
    }
}
