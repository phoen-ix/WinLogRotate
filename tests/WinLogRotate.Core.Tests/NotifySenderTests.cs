using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Shouldly;
using WinLogRotate.Contracts;
using WinLogRotate.Core.Notify;
using WinLogRotate.Core.Notify.Delivery;
using WinLogRotate.Core.Secrets;
using WinLogRotate.Core.State;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// The transports' shared machinery: the budget, request bodies, TLS pinning, the proxy, and
/// turning a credential reference into a credential.
/// </summary>
/// <remarks>
/// In the SMTP callback collection because <c>TheGlobalSmtpCallbackIsAlwaysRestoredEvenWhenTheSendFails</c>
/// sets the process-global callback by hand, which would race the relay tests in
/// <c>NotifySmtpTests</c> if the two classes ran side by side.
/// </remarks>
[Collection(SmtpCallbackCollection.Name)]
public sealed class NotifySenderTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-senders-");

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    private static readonly DateTimeOffset Start = new(2026, 9, 9, 3, 0, 0, TimeSpan.Zero);

    // ---- the budget and the run deadline -----------------------------------------------------

    [Fact]
    public void NoDeadlineMeansNoClamp()
    {
        // A hand-run rotation is never truncated: nothing is going to kill it, and inventing a
        // deadline would make the interactive case behave differently from the scheduled one.
        var (allowed, clamped) = NotifyBudget.For(
            TimeSpan.FromSeconds(30), deadline: null, Start, Start + TimeSpan.FromHours(9));

        allowed.ShouldBe(TimeSpan.FromSeconds(30));
        clamped.ShouldBeFalse();
    }

    [Fact]
    public void AnEarlyRunKeepsItsWholeBudget()
    {
        var (allowed, clamped) = NotifyBudget.For(
            TimeSpan.FromSeconds(30), TimeSpan.FromHours(1), Start, Start + TimeSpan.FromMinutes(2));

        allowed.ShouldBe(TimeSpan.FromSeconds(30));
        clamped.ShouldBeFalse();
    }

    [Fact]
    public void TheBudgetIsClampedByTheRunDeadline()
    {
        // 59m40s of rotating against a one-hour limit leaves twenty seconds, less the five
        // reserved to save state and exit. Fifteen is less than the thirty the phase wanted, so
        // the deadline is what decides - and the operator is told, because a shortened phase
        // that says nothing is indistinguishable from one that had nothing to say.
        var (allowed, clamped) = NotifyBudget.For(
            TimeSpan.FromSeconds(30), TimeSpan.FromHours(1), Start,
            Start + TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(40));

        allowed.ShouldBe(TimeSpan.FromSeconds(15));
        clamped.ShouldBeTrue();
    }

    [Fact]
    public void ARunThatHasAlreadyOverrunGetsNothing()
    {
        // Nothing is sent, and - because nothing was sent - nothing is recorded as reported, so
        // the next run says it again rather than treating a live incident as old news.
        var (allowed, clamped) = NotifyBudget.For(
            TimeSpan.FromSeconds(30), TimeSpan.FromHours(1), Start, Start + TimeSpan.FromMinutes(61));

        allowed.ShouldBe(TimeSpan.Zero);
        clamped.ShouldBeTrue();
    }

    [Fact]
    public void TheBudgetIsSharedEvenlyRatherThanFirstCome()
    {
        NotifyBudget.Share(TimeSpan.FromSeconds(30), 3).ShouldBe(TimeSpan.FromSeconds(10));
        NotifyBudget.Share(TimeSpan.FromSeconds(30), 1).ShouldBe(TimeSpan.FromSeconds(30));
        NotifyBudget.Share(TimeSpan.Zero, 3).ShouldBe(TimeSpan.Zero);
    }

    // ---- request bodies ----------------------------------------------------------------------

    private static PlannedNotification Message(string job, string text)
    {
        DigestLine[] lines =
        [
            new()
            {
                Severity = Severity.Error,
                Code = DiagnosticCode.FileLocked,
                Job = job,
                Text = text,
                Where = @"C:\logs",
                DirectoryKey = @"c:\logs",
                Count = 1,
            },
        ];

        return new PlannedNotification
        {
            Reason = NotifyReason.NewFailure,
            Job = job,
            Severity = Severity.Error,
            Subject = "subject",
            Lines = lines,
            Context = lines,
            Fingerprint = "abc",
            NextState = new JobNotifyState(),
        };
    }

    private static RunSummary Run() => new()
    {
        RunId = "r1",
        Machine = "TESTBOX",
        JobsRun = 1,
        Completed = 0,
        BytesFreed = 0,
        ObservedJobs = [],
    };

    /// <summary>
    /// The injection test.
    /// </summary>
    /// <remarks>
    /// Every Slack, Teams, Discord and ntfy example is a JSON template with the message
    /// substituted into a string, and the substituted text contains file paths and error messages
    /// the operator never wrote. A log called <c>a"b.log</c> would end the string early; something
    /// crafted could append fields. The template supplies the quotes, so the value must be escaped
    /// for the declared content type before it is placed.
    /// </remarks>
    [Fact]
    public void ATemplatedBodyCannotBeBrokenByAFilename()
    {
        var nasty = "a\"b\\c\r\n\"role\":\"admin\"";

        var body = NotifyBody.Render(
            """{"text": "{body}", "job": "{job}"}""",
            "application/json",
            Message(nasty, nasty),
            subject: nasty,
            body: nasty,
            Run());

        // It has to still be JSON, and the injected key must not have become a real one.
        var parsed = System.Text.Json.JsonDocument.Parse(body);
        parsed.RootElement.GetProperty("job").GetString().ShouldBe(nasty);
        parsed.RootElement.TryGetProperty("role", out _).ShouldBeFalse();
    }

    [Fact]
    public void AFormEncodedTemplateIsPercentEncodedInstead()
    {
        var body = NotifyBody.Render(
            "message={body}&job={job}", "application/x-www-form-urlencoded",
            Message("a&b=c", "x"), subject: "s", body: "x", Run());

        body.ShouldBe("message=x&job=a%26b%3Dc");
    }

    [Fact]
    public void APlainTextTemplateIsLeftExactlyAsWritten()
    {
        // There is no structure to break out of, so escaping would corrupt the message rather
        // than protect it.
        NotifyBody.Render("{body}", "text/plain", Message("j", "x"), "s", "a\"b", Run())
            .ShouldBe("a\"b");
    }

    [Fact]
    public void AnUnknownPlaceholderIsLeftInPlaceRatherThanBlanked()
    {
        // A typo that silently produces an empty field is a message that looks fine and is not.
        NotifyBody.Render("{bdoy}", "text/plain", Message("j", "x"), "s", "b", Run())
            .ShouldBe("{bdoy}");
    }

    [Fact]
    public void AProviderWithNoTemplateGetsAWholeJsonObject()
    {
        var body = NotifyBody.Render(
            template: null, "application/json", Message("iis", "locked"), "subject", "body", Run());

        var parsed = System.Text.Json.JsonDocument.Parse(body);
        parsed.RootElement.GetProperty("machine").GetString().ShouldBe("TESTBOX");
        parsed.RootElement.GetProperty("severity").GetString().ShouldBe("error");
        parsed.RootElement.GetProperty("job").GetString().ShouldBe("iis");
    }

    [Fact]
    public void TheRunScopeIsNamedInWordsRatherThanAsAnAsterisk()
    {
        // "*" is an internal key. A webhook payload saying job: "*" is a payload nobody can route.
        var body = NotifyBody.Render(
            null, "application/json", Message(NotifyStateDocument.RunScope, "x"), "s", "b", Run());

        System.Text.Json.JsonDocument.Parse(body)
            .RootElement.GetProperty("job").GetString().ShouldBe("configuration");
    }

    /// <summary>
    /// The operator's redact list reaches the payload fields, not only the prose.
    /// </summary>
    /// <remarks>
    /// The subject and body were masked before they reached the transport; <c>{machine}</c>,
    /// <c>{job}</c> and the default flat object's fields were not. An internal hostname the operator
    /// listed in <c>redact</c> therefore left the machine in every webhook payload, in the one
    /// field a template is most likely to put in a heading.
    /// </remarks>
    [Fact]
    public void TheRedactListReachesThePayloadFieldsToo()
    {
        var flat = NotifyBody.Render(
            null, "application/json", Message("iis", "x"), "s", "b", Run(), redact: ["TESTBOX"]);

        System.Text.Json.JsonDocument.Parse(flat)
            .RootElement.GetProperty("machine").GetString().ShouldBe("***");

        var templated = NotifyBody.Render(
            """{"host": "{machine}", "job": "{job}"}""", "application/json",
            Message("iis", "x"), "s", "b", Run(), redact: ["iis"]);

        System.Text.Json.JsonDocument.Parse(templated)
            .RootElement.GetProperty("job").GetString().ShouldBe("***");
    }

    [Fact]
    public void AnXmlTemplateEscapesTheApostropheToo()
    {
        // The XML escaper covered four of the five reserved characters. An attribute delimited
        // with single quotes - legal XML, and what some templates use - was breakable by a path
        // containing one.
        NotifyBody.Render("<m a='{body}'/>", "application/xml", Message("j", "x"), "s", "it's <b>", Run())
            .ShouldBe("<m a='it&apos;s &lt;b&gt;'/>");
    }

    // ---- redaction ----------------------------------------------------------------------------

    [Fact]
    public void AnEntryShorterThanThreeCharactersMasksNothing()
    {
        // redact = ["a"] replaced every letter a in every message. The binder refuses such entries
        // now; this is the belt to that brace, for a list built any other way.
        Redaction.MaskText("banana split", ["a"]).ShouldBe("banana split");
        Redaction.MaskText("banana split", ["ban"]).ShouldBe("***ana split");
    }

    // ---- fitting the body to what the destination adds to it -------------------------------------

    /// <summary>
    /// The body's limit leaves room for whatever the destination puts beside it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The dispatcher fitted <c>{body}</c> alone to <c>max_message</c>. The documented Discord
    /// template is <c>{subject}\n\n{body}</c>, so the delivered content overflowed 2000 by the
    /// subject's length, Discord answered 400, and the message was recorded as refused - for ever,
    /// since it would overflow again tomorrow. The Event Log sender prepends the subject too, and
    /// its limit was above the writer's hard cut besides, so the footer-preserving truncation was
    /// undone by a plain cut a few hundred characters later.
    /// </para>
    /// <para>
    /// Only the destinations that add something reserve anything: Pushover's title is a separate
    /// field, and a channel with no limit has nothing to reserve against.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheBodyLimitLeavesRoomForWhatTheDestinationAddsToTheBody()
    {
        const string discord = """{"content": "{subject}\n\n{body}"}""";
        const string plain = """{"content": "{body}"}""";
        const string subject = "[WinLogRotate] FAILED on TESTBOX - iis - 3 problems";

        MessageComposer.BodyLimit(2000, HookScheme.Http, discord, subject)
            .ShouldBe(2000 - discord.Length - subject.Length);

        MessageComposer.BodyLimit(2000, HookScheme.Http, plain, subject)
            .ShouldBe(2000 - plain.Length);

        // Subject, a blank line, body: the sender's own concatenation, on any newline convention.
        MessageComposer.BodyLimit(MessageComposer.EventLogLimit, HookScheme.EventLog, null, subject)
            .ShouldBe(MessageComposer.EventLogLimit - subject.Length - 4);

        MessageComposer.BodyLimit(MessageComposer.PushoverLimit, HookScheme.Pushover, null, subject)
            .ShouldBe(MessageComposer.PushoverLimit);

        MessageComposer.BodyLimit(MessageComposer.NoLimit, HookScheme.Http, discord, subject)
            .ShouldBe(MessageComposer.NoLimit);
    }

    [Fact]
    public void TheEventLogLimitIsTheWritersHardCutNotTheApisCeiling()
    {
        // 31,839 is what ReportEventW accepts; 31,000 is where EventLogWriter cuts, deterministically,
        // to stay clear of it. A composer limit above the writer's cut meant the composer's careful
        // truncation - whole lines, footer kept - was followed by the writer's blunt one.
        MessageComposer.EventLogLimit.ShouldBe(31_000);
    }

    // ---- retry-after ---------------------------------------------------------------------------

    /// <summary>
    /// A date-form <c>Retry-After</c> is measured against the injected clock, and never negative.
    /// </summary>
    /// <remarks>
    /// It was measured against <c>DateTimeOffset.UtcNow</c> - the one clock in the notification
    /// phase nothing could fake - and passed through as it came. A date already behind the clock
    /// produced a negative wait, <c>Thread.Sleep</c> refuses anything below -1 ms, and the run died
    /// with exit 4 over a header.
    /// </remarks>
    [Fact]
    public void ARetryAfterDateIsMeasuredAgainstTheInjectedClockAndClampedAtZero()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Start);

        using var later = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
        later.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(Start.AddSeconds(30));

        using var earlier = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
        earlier.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(Start.AddSeconds(-30));

        HttpNotifySender.RetryAfter(later, clock).ShouldBe(TimeSpan.FromSeconds(30));
        HttpNotifySender.RetryAfter(earlier, clock).ShouldBe(TimeSpan.Zero);
    }

    // ---- TLS pinning -------------------------------------------------------------------------

    private static X509Certificate2 SelfSigned(string name)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return request.CreateSelfSigned(Start.AddDays(-1), Start.AddDays(1));
    }

    [Fact]
    public void APinnedCertificateIsTheOnlyOneAccepted()
    {
        using var expected = SelfSigned("relay.example.test");
        using var impostor = SelfSigned("relay.example.test");

        var thumbprint = Convert.ToHexString(SHA256.HashData(expected.GetRawCertData()));

        TlsPinning.Matches(thumbprint, expected).ShouldBeTrue();
        TlsPinning.Matches(thumbprint, impostor).ShouldBeFalse(
            "the same subject name is not the same certificate - that is the whole point");
        TlsPinning.Matches(thumbprint, null).ShouldBeFalse();
    }

    [Theory]
    [InlineData("AA:BB:CC")]
    [InlineData("aa bb cc")]
    [InlineData("aabbcc")]
    [InlineData("AA-BB-CC")]
    public void AThumbprintCanBePastedFromAnywhere(string written)
    {
        // Every tool that prints one formats it differently, and an operator will paste whichever
        // they were given. Refusing on punctuation would send them looking for a bug.
        TlsPinning.Normalise(written).ShouldBe("AABBCC");
    }

    [Fact]
    public void AThumbprintIsWellFormedOnlyAsSixtyFourHexDigits()
    {
        // A SHA-256, in whatever punctuation. A SHA-1 pasted from an older tool, or a digit lost
        // in transit, matches no certificate that exists and would refuse every peer for ever.
        TlsPinning.IsWellFormed("9F:86:D0:81:88:4C:7D:65:9A:2F:EA:A0:C5:5A:D0:15:A3:BF:4F:1B:2B:0B:82:2C:D1:5D:6C:15:B0:F0:0A:08")
            .ShouldBeTrue();
        TlsPinning.IsWellFormed(new string('A', 63)).ShouldBeFalse();
        TlsPinning.IsWellFormed(new string('A', 65)).ShouldBeFalse();
        TlsPinning.IsWellFormed(string.Empty).ShouldBeFalse();
    }

    [Fact]
    public void NormaliseDoesNotSizeItsStackFromTheConfiguration()
    {
        // The scratch buffer was stackalloc'd at the length of whatever was written. A value this
        // long belongs on the heap, and the result has to be right either way.
        var absurd = string.Join(':', Enumerable.Repeat("ab", 25_000));

        TlsPinning.Normalise(absurd).Length.ShouldBe(50_000);
    }

    [Fact]
    public void AnUnsetThumbprintInstallsNoCallbackAtAll()
    {
        // Not "a callback that returns true" - that would be an insecure flag wearing a disguise.
        // With no pin the operating system's trust store decides, untouched.
        TlsPinning.CallbackFor(null).ShouldBeNull();
        TlsPinning.CallbackFor("   ").ShouldBeNull();
        TlsPinning.CallbackFor("AABB").ShouldNotBeNull();
    }

    /// <summary>
    /// The invariant the serial dispatcher rests on.
    /// </summary>
    /// <remarks>
    /// SmtpClient reads a process-global validation callback, so the sender installs one around a
    /// single send and must remove it however that send ends. A leaked callback would apply the
    /// wrong relay's pin - or a stale one - to every later connection in the process.
    /// </remarks>
    [Fact]
    public void TheGlobalSmtpCallbackIsAlwaysRestoredEvenWhenTheSendFails()
    {
        var before = SmtpCertificatePin.Current;

        try
        {
            SmtpCertificatePin.Current = (_, _, _, _) => true;
            var sentinel = SmtpCertificatePin.Current;

            var provider = new NotifyProvider
            {
                Name = "email.relay",
                Kind = NotifyProviderKind.Email,

                // Nothing is listening, so the send fails inside the try - which is the case that
                // matters: the finally is what has to put the callback back. tls = none, because
                // any other setting asks the relay first and a refused probe never reaches the
                // try at all - the test would pass without exercising the thing it pins.
                Host = "127.0.0.1",
                Port = 9,
                Tls = SmtpTls.None,
                From = "a@example.test",
                To = ["b@example.test"],
            };

            var channel = new ResolvedChannel
            {
                Action = HookAction.Create(HookScheme.Smtp, "email.relay", "email.relay", provider: "email.relay"),
                Provider = provider,
                Pin = "AABBCC",
            };

            var result = new SmtpNotifySender().Send(
                channel,
                new NotifyMessage
                {
                    Subject = "s",
                    Body = "b",
                    Plan = Message("iis", "x"),
                    Run = Run(),
                },
                TimeSpan.FromSeconds(2));

            result.Ok.ShouldBeFalse();
            SmtpCertificatePin.Current.ShouldBeSameAs(sentinel);
        }
        finally
        {
            SmtpCertificatePin.Current = before;
        }
    }

    // ---- what an http failure is called ---------------------------------------------------------

    /// <summary>
    /// A rejected certificate is named as one, however deep the runtime buries it.
    /// </summary>
    /// <remarks>
    /// Both HTTP senders read "the request did not complete" for anything that was not a
    /// <c>SocketException</c>, and the Pushover sender looked one inner exception deep for that.
    /// A pin that did not match - the case pinning exists for - was therefore indistinguishable from
    /// a cable pulled out, and the operator went looking at the network.
    /// </remarks>
    [Fact]
    public void ACertificateRejectionIsNamedRatherThanCalledIncomplete()
    {
        var rejected = NotifyHttpClient.Describe(new System.Net.Http.HttpRequestException(
            "handshake", new System.Security.Authentication.AuthenticationException("rejected")));

        rejected.Error.ShouldContain("server_cert_thumbprint");

        var refused = NotifyHttpClient.Describe(new System.Net.Http.HttpRequestException(
            "connect", new IOException("io", new System.Net.Sockets.SocketException(
                (int)System.Net.Sockets.SocketError.ConnectionRefused))));

        refused.Error.ShouldContain("refused");
        refused.NativeError.ShouldNotBeNull("the socket error travels with it, two levels down");

        NotifyHttpClient.Describe(new System.Net.Http.HttpRequestException("opaque"))
            .Error.ShouldBe("the request did not complete");
    }

    // ---- the proxy ---------------------------------------------------------------------------

    [Fact]
    public void AnUnsetProxyLeavesTheMachineDefaultAlone()
    {
        ProxyPolicy.Resolve(NotifySettings.Default, out var bypass).ShouldBeNull();
        bypass.ShouldBeFalse();
    }

    [Fact]
    public void ProxyNoneDisablesTheSystemProxy()
    {
        // Different from an empty proxy: the handler has to be told to use none at all.
        ProxyPolicy.Resolve(NotifySettings.Default with { Proxy = "None" }, out var bypass)
            .ShouldBeNull();

        bypass.ShouldBeTrue();
    }

    [Fact]
    public void AConfiguredProxyIsUsedForTheHostsItShould()
    {
        var proxy = (WebProxy)ProxyPolicy.Resolve(
            NotifySettings.Default with
            {
                Proxy = "http://proxy.example.test:3128",
                NoProxy = ["internal.test"],
            },
            out _)!;

        proxy.IsBypassed(new Uri("https://hooks.slack.com/services/x")).ShouldBeFalse();
        proxy.IsBypassed(new Uri("https://internal.test/hook")).ShouldBeTrue();
        proxy.IsBypassed(new Uri("https://api.internal.test/hook")).ShouldBeTrue();
    }

    /// <summary>
    /// <c>WebProxy.BypassList</c> takes regular expressions, and nothing about it says so.
    /// </summary>
    /// <remarks>
    /// An operator writes <c>no_proxy = ["example.com"]</c> meaning a hostname. Unescaped, that
    /// pattern also matches <c>exampleXcom</c> and, far worse, <c>notexample.com.evil.test</c> -
    /// so traffic the operator meant to route through the proxy goes direct to an attacker's host
    /// instead, silently.
    /// </remarks>
    [Fact]
    public void NoProxyEntriesAreEscapedRatherThanTreatedAsRegexes()
    {
        var proxy = (WebProxy)ProxyPolicy.Resolve(
            NotifySettings.Default with
            {
                Proxy = "http://proxy.example.test:3128",
                NoProxy = ["example.com"],
            },
            out _)!;

        proxy.IsBypassed(new Uri("https://example.com/x")).ShouldBeTrue();

        // Both of these match an UNESCAPED "example.com" - the first because "." matches any
        // character, the second because an unanchored pattern matches anywhere in the string.
        // Note the probe hosts are dotted on purpose: WebProxy bypasses single-label hosts as
        // local before it ever consults the list, which would make a dotless probe pass for the
        // wrong reason.
        proxy.IsBypassed(new Uri("https://exampleXcom.evil.test/x")).ShouldBeFalse();
        proxy.IsBypassed(new Uri("https://notexample.com.evil.test/x")).ShouldBeFalse();
    }

    // ---- resolving credentials ---------------------------------------------------------------

    private SecretResolver Resolver() =>
        new(new UnsupportedSecretPlatform(), Path.Combine(_dir.FullName, "secrets.dat"));

    [Fact]
    public void AtEnvResolvesFromTheEnvironment()
    {
        // Parsed and round-tripped by tests since milestone 7, and read by nothing until now.
        var name = "WLR_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, "s3cret");

        try
        {
            var resolved = Resolver().Resolve(SecretRef.Parse($"@env:{name}"));

            resolved.Ok.ShouldBeTrue();
            resolved.Value.Length.ShouldBe(6);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void AnEmptyEnvironmentVariableIsTreatedAsAbsentRatherThanAsAnEmptyPassword()
    {
        // A variable that exists but is blank is what a deployment that forgot to substitute it
        // looks like. Authenticating with "" against a relay that accepts anonymous mail would
        // silently succeed, and nobody would find out until the relay tightened.
        var name = "WLR_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, "");

        try
        {
            Resolver().Resolve(SecretRef.Parse($"@env:{name}")).Ok.ShouldBeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void AtCommandIsRefusedNotIgnored()
    {
        // Refused permanently, and not for want of the configuration-directory gate - that
        // exists now. The grammar is the problem: the reference IS the whole command line, so the
        // vault's own credentials would sit in a config file the docs call safe to paste into a
        // support ticket. A reference that silently resolved to nothing would look like a broken
        // relay instead of a refusal.
        var resolved = Resolver().Resolve(SecretRef.Parse("@command:vault read -field=pw kv/smtp"));

        resolved.Ok.ShouldBeFalse();
        resolved.Error.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ALiteralResolvesToItself()
    {
        Resolver().Resolve(SecretRef.Parse("hunter2")).Value.Length.ShouldBe(7);
    }

    [Fact]
    public void NothingConfiguredIsNotAFailure()
    {
        // Most providers need no credential at all - a pickup directory, an IP-authorised relay.
        Resolver().Resolve(SecretRef.None).Ok.ShouldBeTrue();
    }

    // ---- resolving channels ------------------------------------------------------------------

    private static SenderTable Table(params HookScheme[] schemes) =>
        new(schemes.ToDictionary(s => s, INotifySender (_) => new UndeliveredScheme("test")));

    [Fact]
    public void AMissingCredentialDropsTheChannelAndSaysWhy()
    {
        // Dropped, never attempted anonymously. A relay that accepts unauthenticated mail would
        // let the send succeed, and the operator would believe authentication was working.
        var settings = NotifySettings.Default with { To = ["email.relay"] };

        var resolved = ChannelResolver.Resolve(
            settings,
            [
                new NotifyProvider
                {
                    Name = "email.relay",
                    Kind = NotifyProviderKind.Email,
                    Host = "smtp.example.test",
                    Auth = SmtpAuth.Login,
                    Password = SecretRef.Parse("@env:WLR_DEFINITELY_NOT_SET"),
                },
            ],
            Resolver(), Table(HookScheme.Smtp));

        resolved.Channels.ShouldBeEmpty();
        resolved.Diagnostics.ShouldContain(d => d.Code == DiagnosticCode.NotifyMisconfigured);
    }

    [Fact]
    public void AHookSchemeInNotifyIsReportedRatherThanIgnored()
    {
        // command:, service: and event: are hooks, not notification targets, and permanently so.
        // Silence would be indistinguishable from a target that worked.
        var resolved = ChannelResolver.Resolve(
            NotifySettings.Default with { To = [@"command:C:\tools\page.exe"] },
            [], Resolver(), Table(HookScheme.Http));

        resolved.Channels.ShouldBeEmpty();

        var refusal = resolved.Diagnostics.ShouldHaveSingleItem();
        refusal.Message.ShouldContain("command:");

        // And it says where the scheme does belong, rather than promising a later build. The
        // remedy used to read "targets that run code on this machine land with the
        // configuration-directory check they require" - that check landed in milestone 13, so the
        // sentence became a promise of something that is not coming.
        refusal.Remedy.ShouldNotBeNull().ShouldContain("postrotate");
        refusal.Remedy.ShouldNotContain("land");
    }

    /// <summary>
    /// A scheme that carries no message says so, rather than being lumped in with command:.
    /// </summary>
    /// <remarks>
    /// The permanent rule, and the mirror of the one HookPlan applies in the other direction:
    /// service: acts, and a target reports. command: is refused too but for a different reason - a
    /// program could carry a message - so the two must not share a sentence.
    /// </remarks>
    [Theory]
    [InlineData("service:paramchange:W3SVC")]
    [InlineData(@"event:Global\AppReload")]
    public void ASchemeThatCarriesNoMessageSaysThatIsWhy(string target)
    {
        var resolved = ChannelResolver.Resolve(
            NotifySettings.Default with { To = [target] },
            [], Resolver(), Table(HookScheme.Http));

        resolved.Diagnostics.ShouldHaveSingleItem()
            .Remedy.ShouldNotBeNull().ShouldContain("carry nothing");
    }

    [Fact]
    public void TwoTargetsForTheSamePlaceAreOnlySentToOnce()
    {
        // They share a breaker key, so attempting both would double every message and count two
        // failures for one outage.
        var resolved = ChannelResolver.Resolve(
            NotifySettings.Default with { To = ["eventlog:", "eventlog:"] },
            [], Resolver(), Table(HookScheme.EventLog));

        resolved.Channels.ShouldHaveSingleItem();
    }

    [Fact]
    public void ADisabledProviderIsSilentlySkipped()
    {
        var resolved = ChannelResolver.Resolve(
            NotifySettings.Default with { To = ["webhook.hook"] },
            [
                new NotifyProvider
                {
                    Name = "webhook.hook",
                    Kind = NotifyProviderKind.Webhook,
                    Enabled = false,
                    Url = SecretRef.Parse("https://example.test/x"),
                },
            ],
            Resolver(), Table(HookScheme.Http));

        resolved.Channels.ShouldBeEmpty();
        resolved.Diagnostics.ShouldBeEmpty("disabling something on purpose is not a problem to report");
    }

    [Fact]
    public void AWebhookProviderCarriesItsUrlAsACredential()
    {
        var resolved = ChannelResolver.Resolve(
            NotifySettings.Default with { To = ["webhook.hook"], ServerCertThumbprint = "AABB" },
            [
                new NotifyProvider
                {
                    Name = "webhook.hook",
                    Kind = NotifyProviderKind.Webhook,
                    Url = SecretRef.Parse("https://hooks.example.test/services/T000/B000/xxxx"),
                    MaxMessage = MessageComposer.DiscordLimit,
                },
            ],
            Resolver(), Table(HookScheme.Http));

        var channel = resolved.Channels.ShouldHaveSingleItem();
        channel.Target.HasValue.ShouldBeTrue();
        channel.Limit.ShouldBe(MessageComposer.DiscordLimit);
        channel.Pin.ShouldBe("AABB");

        // The display is the provider's name, never the URL - its entropy is in the path.
        channel.Display.ShouldBe("webhook.hook");
    }

    [Fact]
    public void PushoverIsTruncatedToWhatPushoverAccepts()
    {
        // It discards anything longer silently, so a truncated message beats a message the
        // operator believes was sent.
        var resolved = ChannelResolver.Resolve(
            NotifySettings.Default with { To = ["pushover:uQiRzpo4"] },
            [PushoverApp()], Resolver(), Table(HookScheme.Pushover));

        resolved.Channels.ShouldHaveSingleItem().Limit.ShouldBe(MessageComposer.PushoverLimit);
    }

    // ---- inline targets that borrow a provider ------------------------------------------------

    private static NotifyProvider EmailRelay(string name = "email.relay", bool enabled = true) => new()
    {
        Name = name,
        Kind = NotifyProviderKind.Email,
        Enabled = enabled,
        Host = "smtp.example.test",
        From = "winlogrotate@example.test",
        To = ["ops@example.test"],
    };

    private static NotifyProvider PushoverApp(string name = "pushover.app") => new()
    {
        Name = name,
        Kind = NotifyProviderKind.Pushover,
        Token = SecretRef.Parse("azGDORePK8gMaC0QOYAMyEEuzJnyUi"),
    };

    /// <summary>
    /// The documented <c>smtp:ops@example.com</c> target, next to the provider it needs.
    /// </summary>
    /// <remarks>
    /// docs/notifications.md has said since milestone 10 that an <c>smtp:</c> target "needs a
    /// <c>[notify.email.*]</c> provider for the relay", and that the provider's <c>to</c> combines
    /// with the address in the target. The resolver never handed an inline target a provider, so the
    /// channel reached the sender with <c>Provider = null</c>, the sender answered 400, and the
    /// dispatcher recorded the message as reported. The documented target had never once worked.
    /// </remarks>
    [Fact]
    public void AnInlineSmtpTargetPairsWithTheOnlyEmailProvider()
    {
        var resolved = ChannelResolver.Resolve(
            NotifySettings.Default with { To = ["smtp:oncall@example.test"] },
            [EmailRelay()], Resolver(), Table(HookScheme.Smtp));

        var channel = resolved.Channels.ShouldHaveSingleItem();
        channel.Provider.ShouldNotBeNull().Name.ShouldBe("email.relay");
        channel.Action.Scheme.ShouldBe(HookScheme.Smtp);

        // Its own identity, not the provider's: a breaker opened by this target must not suppress
        // the provider named beside it, and notify status must be able to tell them apart. The
        // display is the address, as it is for every inline target; the key carries the scheme.
        channel.Key.ShouldBe("smtp:oncall@example.test");
        channel.Display.ShouldBe("oncall@example.test");

        // The provider's standing list, plus the address the target named.
        SmtpNotifySender.Recipients(channel.Provider!, channel)
            .ShouldBe(["ops@example.test", "oncall@example.test"]);

        resolved.Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void AnInlineSmtpTargetWithNoEmailProviderIsDroppedAndSaysWhatItNeeds()
    {
        // Dropped rather than attempted: the sender would answer 400 with no relay to talk to, and
        // until C3 that answer was recorded as the message having been reported.
        var resolved = ChannelResolver.Resolve(
            NotifySettings.Default with { To = ["smtp:oncall@example.test"] },
            [], Resolver(), Table(HookScheme.Smtp));

        resolved.Channels.ShouldBeEmpty();

        var dropped = resolved.Diagnostics.ShouldHaveSingleItem();
        dropped.Code.ShouldBe(DiagnosticCode.NotifyMisconfigured);
        dropped.Message.ShouldContain("[notify.email");
    }

    [Fact]
    public void AnInlineSmtpTargetWithTwoEmailProvidersIsDroppedAndNamesThem()
    {
        // "The relay" is unambiguous with one email provider and a guess with two. Taking the one
        // written higher up would route mail through whichever table happened to come first, and
        // nothing would say so.
        var resolved = ChannelResolver.Resolve(
            NotifySettings.Default with { To = ["smtp:oncall@example.test"] },
            [EmailRelay("email.a"), EmailRelay("email.b")], Resolver(), Table(HookScheme.Smtp));

        resolved.Channels.ShouldBeEmpty();

        var dropped = resolved.Diagnostics.ShouldHaveSingleItem();
        dropped.Code.ShouldBe(DiagnosticCode.NotifyMisconfigured);
        dropped.Message.ShouldContain("email.a");
        dropped.Message.ShouldContain("email.b");
    }

    [Fact]
    public void ADisabledEmailProviderDoesNotCountTowardsThePairing()
    {
        var resolved = ChannelResolver.Resolve(
            NotifySettings.Default with { To = ["smtp:oncall@example.test"] },
            [EmailRelay("email.old", enabled: false), EmailRelay("email.new")], Resolver(), Table(HookScheme.Smtp));

        resolved.Channels.ShouldHaveSingleItem().Provider.ShouldNotBeNull().Name.ShouldBe("email.new");
    }

    [Fact]
    public void AnInlinePushoverTargetTakesItsTokenFromTheOnlyPushoverProvider()
    {
        // pushover:KEY carries the user key; the application token has to come from somewhere, and
        // the docs say the provider table. It came from nowhere, so the sender answered 401.
        var resolved = ChannelResolver.Resolve(
            NotifySettings.Default with { To = ["pushover:uQiRzpo4DXghDmr9QzzfQu27cmVRsG"] },
            [PushoverApp()], Resolver(), Table(HookScheme.Pushover));

        var channel = resolved.Channels.ShouldHaveSingleItem();
        channel.Provider.ShouldNotBeNull().Name.ShouldBe("pushover.app");
        channel.Credential.HasValue.ShouldBeTrue("the application token comes from the provider");
        channel.Target.HasValue.ShouldBeTrue("the user key is the target itself");
        channel.Key.ShouldBe("pushover:uqirzpo4dxghdmr9qzzfqu27cmvrsg");
    }

    /// <summary>
    /// A provider that lacks what its transport needs is dropped here, before anything is sent.
    /// </summary>
    /// <remarks>
    /// A webhook table with no <c>url</c> resolved to a channel whose target was an empty string;
    /// the sender built a request with a null URI, caught the resulting exception as a 400, and the
    /// dispatcher recorded the message as reported. The only check the resolver made was
    /// login-without-password. Every transport's prerequisites are now checked in one place, and
    /// <c>ConfigLoader</c> applies the same list at load time so <c>config check</c> agrees.
    /// </remarks>
    [Theory]
    [InlineData("webhook-without-url", "url")]
    [InlineData("pushover-without-token", "token")]
    [InlineData("pushover-without-user-key", "user_key")]
    [InlineData("email-without-host", "host")]
    [InlineData("pickup-without-directory", "pickup_directory")]
    public void AProviderMissingWhatItsTransportNeedsIsDroppedBeforeAnythingIsSent(string shape, string field)
    {
        var provider = Incomplete(shape);

        var resolved = ChannelResolver.Resolve(
            NotifySettings.Default with { To = [provider.Name] },
            [provider], Resolver(), Table(HookScheme.Http, HookScheme.Pushover, HookScheme.Smtp));

        resolved.Channels.ShouldBeEmpty();

        var dropped = resolved.Diagnostics.ShouldHaveSingleItem();
        dropped.Code.ShouldBe(DiagnosticCode.NotifyMisconfigured);
        dropped.Message.ShouldContain(field);
        dropped.Message.ShouldContain(provider.Name);
    }

    private static NotifyProvider Incomplete(string shape) => shape switch
    {
        "webhook-without-url" => new NotifyProvider { Name = "webhook.hook", Kind = NotifyProviderKind.Webhook },
        "pushover-without-token" => new NotifyProvider
        {
            Name = "pushover.app",
            Kind = NotifyProviderKind.Pushover,
            UserKey = SecretRef.Parse("uQiRzpo4DXghDmr9QzzfQu27cmVRsG"),
        },
        "pushover-without-user-key" => PushoverApp(),
        "email-without-host" => new NotifyProvider { Name = "email.relay", Kind = NotifyProviderKind.Email },
        "pickup-without-directory" => new NotifyProvider
        {
            Name = "email.pickup",
            Kind = NotifyProviderKind.Email,
            Delivery = SmtpDelivery.PickupDirectory,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };
}
