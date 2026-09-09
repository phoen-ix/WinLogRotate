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
                // matters: the finally is what has to put the callback back.
                Host = "127.0.0.1",
                Port = 9,
                Tls = SmtpTls.Required,
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
        // Running a program named in a configuration file to fetch a password needs the same
        // hardened directory as a command: hook, and lands with it. A reference that silently
        // resolved to nothing would look like a broken relay instead of an unbuilt feature.
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
    public void ASchemeThisBuildDoesNotDeliverIsReportedRatherThanIgnored()
    {
        // command:, service: and event: parse today and land with the configuration-directory
        // gate they need. Silence would be indistinguishable from a hook that ran.
        var resolved = ChannelResolver.Resolve(
            NotifySettings.Default with { To = [@"command:C:\tools\page.exe"] },
            [], Resolver(), Table(HookScheme.Http));

        resolved.Channels.ShouldBeEmpty();
        resolved.Diagnostics.ShouldHaveSingleItem()
            .Message.ShouldContain("command:");
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
            [], Resolver(), Table(HookScheme.Pushover));

        resolved.Channels.ShouldHaveSingleItem().Limit.ShouldBe(MessageComposer.PushoverLimit);
    }
}
