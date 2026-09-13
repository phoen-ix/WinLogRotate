using Shouldly;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Notify;
using WinLogRotate.Core.Safety;
using WinLogRotate.Core.Secrets;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// Binding the <c>[notify]</c> table. TomlFile.Parse is public, so none of this needs a temp
/// file - the same shape as JournalSettingsBindingTests.
/// </summary>
public sealed class NotifyBindingTests
{
    private readonly DiagnosticBag _bag = new();

    private NotifySettings Bind(string toml) =>
        ConfigBinder.BindNotify(TomlFile.Parse(toml, "config.toml"), _bag);

    private IReadOnlyList<ConfigDiagnostic> Diagnostics => _bag.Items;

    [Fact]
    public void DefaultsApplyWhenTheTableIsAbsent()
    {
        var settings = Bind("schema = 1\n");

        settings.Enabled.ShouldBeTrue();
        settings.On.ShouldBe(NotifyOn.Change);
        settings.Threshold.ShouldBe(Severity.Warning);
        settings.RemindAfter.ShouldBe(TimeSpan.FromDays(7));
        settings.To.ShouldBeEmpty();
    }

    [Fact]
    public void AnEmptyTableSendsNothingWithoutBeingDisabled()
    {
        // Enabled defaults to true so a half-written block is not silently inert; it is the
        // absence of targets that makes it do nothing, and notify show says exactly that.
        var settings = Bind("[notify]\n");

        settings.Enabled.ShouldBeTrue();
        settings.WouldSend.ShouldBeFalse();
    }

    [Fact]
    public void EverySettingBinds()
    {
        var settings = Bind("""
            [notify]
            enabled = true
            on = "every"
            threshold = "error"
            remind_after = "3d"
            budget = "45s"
            breaker_after = 9
            breaker_cooldown = 4
            to = ["email.relay", "eventlog:"]
            redact = ["BigCustomer"]
            proxy = "http://proxy.corp:3128"
            no_proxy = [".corp.example.com"]
            server_cert_thumbprint = "9AF1"
            """);

        settings.On.ShouldBe(NotifyOn.Every);
        settings.Threshold.ShouldBe(Severity.Error);
        settings.RemindAfter.ShouldBe(TimeSpan.FromDays(3));
        settings.Budget.ShouldBe(TimeSpan.FromSeconds(45));
        settings.BreakerAfter.ShouldBe(9);
        settings.BreakerCooldown.ShouldBe(4);
        settings.To.ShouldBe(["email.relay", "eventlog:"]);
        settings.Redact.ShouldBe(["BigCustomer"]);
        settings.Proxy.ShouldBe("http://proxy.corp:3128");
        settings.NoProxy.ShouldBe([".corp.example.com"]);
        settings.ServerCertThumbprint.ShouldBe("9AF1");

        Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void TheThresholdBindsToTheExistingSeverity()
    {
        // Not a second notion of importance. The same enum the whole product already reasons
        // about, so "at or above warning" means one thing everywhere.
        Bind("[notify]\nthreshold = \"critical\"\n").Threshold.ShouldBe(Severity.Critical);
        Bind("[notify]\nthreshold = \"info\"\n").Threshold.ShouldBe(Severity.Info);
    }

    [Fact]
    public void AnInvalidThresholdIsReportedWithTheValidValues()
    {
        Bind("[notify]\nthreshold = \"loud\"\n");

        var d = Diagnostics.ShouldHaveSingleItem();
        d.Severity.ShouldBe(Severity.Error);
        d.Line.ShouldBe(2);
    }

    [Theory]
    [InlineData("30s", 30)]
    [InlineData("15m", 900)]
    [InlineData("2h", 7200)]
    [InlineData("1d", 86400)]
    [InlineData("00:01:30", 90)]
    public void DurationsAcceptBothForms(string written, int seconds)
    {
        // "01:00:00" because host pause already takes it; "2h" because nobody writing a reminder
        // interval wants to count hours into a colon-separated triple.
        Bind($"[notify]\nbudget = \"{written}\"\n").Budget.ShouldBe(TimeSpan.FromSeconds(seconds));
    }

    [Theory]
    [InlineData("soon")]
    [InlineData("7x")]
    [InlineData("-3d")]
    public void AnUnparseableDurationIsReported(string written)
    {
        Bind($"[notify]\nremind_after = \"{written}\"\n");

        Diagnostics.ShouldContain(d => d.Severity == Severity.Error && d.Line == 2);
    }

    [Fact]
    public void AMistypedKeyIsReported()
    {
        // The deliberate divergence from every other table. A mistyped rotation directive falls
        // back to a documented default; a mistyped notification key silently disables the
        // alerting, and you find out during the incident it was meant to warn you about.
        Bind("[notify]\ntresh0ld = \"error\"\n");

        var d = Diagnostics.ShouldHaveSingleItem();
        d.Code.ShouldBe(DiagnosticCode.NotifyMisconfigured);
        d.Severity.ShouldBe(Severity.Warning);
        d.Message.ShouldContain("tresh0ld");
        d.Remedy.ShouldNotBeNull().ShouldContain("threshold");
    }

    [Fact]
    public void ThereIsNoInsecureSwitchAndSayingSoPointsAtThePin()
    {
        // Shipping one would undo the point of server_cert_thumbprint: a switch that disables
        // verification gets set once during an incident and is never unset.
        Bind("[notify]\ninsecure = true\n");

        Diagnostics.ShouldContain(d =>
            d.Code == DiagnosticCode.NotifyMisconfigured
            && d.Remedy!.Contains("server_cert_thumbprint", StringComparison.Ordinal));
    }

    [Fact]
    public void BindingDoesNotRepeatSyntaxErrorsTheDefaultsPassAlreadyReported()
    {
        // BindDefaults runs over the same file first. Reporting them here too would print every
        // syntax error in config.toml twice, which is why BindJournal skips it as well.
        Bind("[notify]\nthis is not toml\n");

        Diagnostics.ShouldNotContain(d => d.Message.Contains("expecting", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// The named <c>[notify.KIND.NAME]</c> provider tables.
/// </summary>
public sealed class NotifyProviderBindingTests
{
    private readonly DiagnosticBag _bag = new();

    private IReadOnlyList<NotifyProvider> Bind(string toml) =>
        ConfigBinder.BindProviders(TomlFile.Parse(toml, "config.toml"), _bag);

    [Fact]
    public void AnEmailProviderBinds()
    {
        var provider = Bind("""
            [notify.email.ses]
            host = "email-smtp.example.com"
            port = 587
            auth = "login"
            tls = "required"
            username = "AKIA"
            password = "@secret:ses-smtp"
            from = "alerts@example.com"
            to = ["ops@example.com"]
            subject_prefix = "[WinLogRotate]"
            """).ShouldHaveSingleItem();

        provider.Name.ShouldBe("email.ses");
        provider.Kind.ShouldBe(NotifyProviderKind.Email);
        provider.Port.ShouldBe(587);
        provider.Auth.ShouldBe(SmtpAuth.Login);
        provider.Tls.ShouldBe(SmtpTls.Required);
        provider.To.ShouldBe(["ops@example.com"]);
        provider.Password.Source.ShouldBe(SecretSource.Store);
        provider.Password.Key.ShouldBe("ses-smtp");
    }

    [Theory]
    [InlineData("delivery = \"pickupdirectory\"\npickup_directory = \"C:/inetpub/mailroot/Pickup\"")]
    [InlineData("auth = \"none\"")]
    [InlineData("auth = \"integrated\"")]
    public void TheCredentialFreeShapesAreRecognisedAsSuch(string body)
    {
        // The best password is the one you never store, and notify show says which providers
        // have already managed it.
        Bind($"[notify.email.x]\nhost = \"relay\"\n{body}\n")
            .ShouldHaveSingleItem().IsCredentialFree.ShouldBeTrue();
    }

    [Fact]
    public void ALoginProviderIsNotCredentialFree()
    {
        Bind("[notify.email.x]\nhost = \"relay\"\nauth = \"login\"\n")
            .ShouldHaveSingleItem().IsCredentialFree.ShouldBeFalse();
    }

    [Fact]
    public void AWebhookUrlIsCarriedAsACredential()
    {
        // A Slack or Teams incoming-webhook URL grants whoever holds it the right to post, so it
        // lives in the store like any other password rather than in the config like an address.
        var provider = Bind("""
            [notify.webhook.teams]
            url = "@secret:teams-hook"
            content_type = "application/json"
            body = "{}"
            """).ShouldHaveSingleItem();

        provider.Kind.ShouldBe(NotifyProviderKind.Webhook);
        provider.Url.Source.ShouldBe(SecretSource.Store);
        provider.Credentials().ShouldContain(c => c.Field == "url");
    }

    /// <summary>
    /// A <c>method</c> that is not an HTTP method is reported here, not thrown there.
    /// </summary>
    /// <remarks>
    /// The sender built <c>new HttpMethod(provider.Method)</c> before its <c>try</c>, and
    /// <c>HttpMethod</c> throws <c>FormatException</c> for anything that is not a token. A typo in
    /// the configuration was therefore exit 4, nightly, with the notification state unsaved. The
    /// value is judged where it is read, on its own line, and the default stands in for it so the
    /// alert still goes.
    /// </remarks>
    [Fact]
    public void AMethodThatIsNotAnHttpMethodIsReportedAndPostIsUsed()
    {
        var provider = Bind("""
            [notify.webhook.hook]
            url = "@secret:hook"
            method = "P OST"
            """).ShouldHaveSingleItem();

        provider.Method.ShouldBe("POST");

        var d = _bag.Items.ShouldHaveSingleItem();
        d.Code.ShouldBe(DiagnosticCode.NotifyMisconfigured);
        d.Severity.ShouldBe(Severity.Warning, "a notification problem must not stop a rotation");
        d.Message.ShouldContain("method");
        d.Line.ShouldBe(3);
    }

    [Fact]
    public void AContentTypeThatIsNotAMediaTypeIsReportedAndJsonIsUsed()
    {
        // "json" is the obvious thing to write, and StringContent threw on it.
        var provider = Bind("""
            [notify.webhook.hook]
            url = "@secret:hook"
            content_type = "json"
            """).ShouldHaveSingleItem();

        provider.ContentType.ShouldBe("application/json");

        var d = _bag.Items.ShouldHaveSingleItem();
        d.Code.ShouldBe(DiagnosticCode.NotifyMisconfigured);
        d.Message.ShouldContain("content_type");
        d.Line.ShouldBe(3);
    }

    [Theory]
    [InlineData("PUT", "text/plain")]
    [InlineData("post", "application/x-www-form-urlencoded")]
    [InlineData("PATCH", "text/plain; charset=utf-8")]
    public void AUsableMethodAndContentTypeBindAsWritten(string method, string contentType)
    {
        var provider = Bind($"""
            [notify.webhook.hook]
            url = "@secret:hook"
            method = "{method}"
            content_type = "{contentType}"
            """).ShouldHaveSingleItem();

        provider.Method.ShouldBe(method);
        provider.ContentType.ShouldBe(contentType);
        _bag.Items.ShouldBeEmpty();
    }

    [Fact]
    public void PushoverCarriesBothOfItsKeys()
    {
        var provider = Bind("""
            [notify.pushover.oncall]
            token = "@secret:pushover-app"
            user_key = "@secret:pushover-user"
            priority = 1
            """).ShouldHaveSingleItem();

        provider.Priority.ShouldBe(1);
        provider.Credentials().Select(c => c.Field).ShouldBe(["token", "user_key"], ignoreOrder: true);
    }

    [Fact]
    public void EveryKindIsFound()
    {
        Bind("""
            [notify.email.a]
            [notify.pushover.b]
            [notify.webhook.c]
            """).Select(p => p.Name).ShouldBe(["email.a", "pushover.b", "webhook.c"], ignoreOrder: true);
    }

    [Theory]
    [InlineData("[notify . email . relay]")]
    [InlineData("[\"notify\".email.relay]")]
    [InlineData("[notify.email.\"relay\"]")]
    public void UnusualButLegalHeadersBindTheSame(string header)
    {
        // All three are legal TOML for the same table, and none of them survives splitting the
        // header's text on '.'. Walking the key nodes is what makes them behave alike.
        Bind($"{header}\nhost = \"relay\"\n")
            .ShouldHaveSingleItem().Name.ShouldBe("email.relay");
    }

    [Fact]
    public void ASubTableIsNotMistakenForAProvider()
    {
        // [notify.webhook.teams.headers] is four parts. Counting them is what stops a provider
        // called "teams.headers" from appearing out of nowhere.
        var providers = Bind("""
            [notify.webhook.teams]
            url = "https://example.com/x"

            [notify.webhook.teams.headers]
            X-Source = "winlogrotate"
            """);

        providers.ShouldHaveSingleItem().Name.ShouldBe("webhook.teams");
    }

    [Fact]
    public void AnArrayOfTablesIsRefusedRatherThanIgnored()
    {
        // It parses as a different node type, so without an explicit check the provider simply
        // never exists and nothing says why.
        Bind("[[notify.email]]\nhost = \"relay\"\n");

        _bag.Items.ShouldContain(d =>
            d.Code == DiagnosticCode.NotifyMisconfigured
            && d.Message.Contains("array of tables", StringComparison.Ordinal));
    }

    [Fact]
    public void NoProvidersMeansNoDiagnostics()
    {
        Bind("schema = 1\n[notify]\nto = []\n").ShouldBeEmpty();
        _bag.Items.ShouldBeEmpty();
    }
}

/// <summary>
/// What loading a configuration says about <c>[notify] to</c>, before anything is resolved.
/// </summary>
/// <remarks>
/// The loader and the resolver apply one list of prerequisites, and these pin the loader's half:
/// a provider a target names but that its transport could not use, and an inline target with no
/// provider to borrow from or too many, are reported at <c>config check</c> rather than discovered
/// on the night the message does not go.
/// </remarks>
public sealed class NotifyTargetValidationTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-targets-");

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    private IReadOnlyList<ConfigDiagnostic> Load(string toml)
    {
        File.WriteAllText(Path.Combine(_dir.FullName, "config.toml"), toml);
        Directory.CreateDirectory(Path.Combine(_dir.FullName, "conf.d"));

        return ConfigLoader.Load(
            InstallPaths.Resolve(_dir.FullName),
            new PathGuard(new GuardOptions()),
            new UnknownSecretLookup(),
            quarantineBadFiles: false).Diagnostics;
    }

    private const string Relay = """
        [notify.email.relay]
        host = "smtp.example.test"
        to = ["ops@example.test"]
        """;

    [Fact]
    public void AnInlineSmtpTargetWithNoEmailProviderIsReportedAtLoadTime()
    {
        var found = Load("""
            schema = 1
            [notify]
            to = ["smtp:oncall@example.test"]
            """).Where(d => d.Code == DiagnosticCode.NotifyMisconfigured).ShouldHaveSingleItem();

        found.Severity.ShouldBe(Severity.Warning, "a notification problem must not stop a rotation");
        found.Message.ShouldContain("[notify.email");
    }

    [Fact]
    public void AnInlineSmtpTargetWithTwoEmailProvidersIsReportedAtLoadTime()
    {
        var found = Load($"""
            schema = 1
            [notify]
            to = ["smtp:oncall@example.test"]
            {Relay}
            [notify.email.other]
            host = "smtp2.example.test"
            """).Where(d => d.Code == DiagnosticCode.NotifyMisconfigured).ShouldHaveSingleItem();

        found.Message.ShouldContain("email.relay");
        found.Message.ShouldContain("email.other");
    }

    [Fact]
    public void AnInlineSmtpTargetWithOneEmailProviderIsNotAProblem()
    {
        Load($"""
            schema = 1
            [notify]
            to = ["smtp:oncall@example.test"]
            {Relay}
            """).ShouldNotContain(d => d.Code == DiagnosticCode.NotifyMisconfigured);
    }

    [Fact]
    public void AWebhookProviderWithoutAUrlIsReportedAtLoadTime()
    {
        var found = Load("""
            schema = 1
            [notify]
            to = ["webhook.slack"]
            [notify.webhook.slack]
            body = '{"text": "{body}"}'
            """).Where(d => d.Code == DiagnosticCode.NotifyMisconfigured).ShouldHaveSingleItem();

        found.Severity.ShouldBe(Severity.Warning);
        found.Message.ShouldContain("url");
        found.Line.ShouldBeGreaterThan(0, "it points at the provider table");
    }

    [Fact]
    public void ADisabledProviderIsNotJudgedOnWhatItLacks()
    {
        // Switched off on purpose, so whatever it is missing is not tonight's problem.
        Load("""
            schema = 1
            [notify]
            to = ["webhook.slack"]
            [notify.webhook.slack]
            enabled = false
            """).ShouldNotContain(d => d.Code == DiagnosticCode.NotifyMisconfigured);
    }
}

/// <summary>
/// The <c>@secret:</c> grammar, and the three-valued existence check behind it.
/// </summary>
public sealed class SecretRefTests
{
    [Theory]
    [InlineData("@secret:ses-smtp", SecretSource.Store, "ses-smtp")]
    [InlineData("@env:WLR_SMTP_PW", SecretSource.Environment, "WLR_SMTP_PW")]
    [InlineData("@command:vault read -field=pw kv/smtp", SecretSource.Command, "vault read -field=pw kv/smtp")]
    public void EachPrefixIsRecognised(string raw, SecretSource source, string key)
    {
        var reference = SecretRef.Parse(raw);

        reference.Source.ShouldBe(source);
        reference.Key.ShouldBe(key);
    }

    [Fact]
    public void APlainValueIsALiteral()
    {
        // Supported deliberately. Forbidding literals drives people to worse workarounds; the
        // warning at the exact line, every run, does not.
        var reference = SecretRef.Parse("hunter2");

        reference.Source.ShouldBe(SecretSource.Literal);
        reference.Literal.Reveal().ShouldBe("hunter2");
    }

    [Fact]
    public void DoubledAtSignsEscapeALiteralThatStartsWithOne()
    {
        // So a password of "@secret:x" remains expressible.
        var reference = SecretRef.Parse("@@secret:x");

        reference.Source.ShouldBe(SecretSource.Literal);
        reference.Literal.Reveal().ShouldBe("@secret:x");
    }

    [Theory]
    [InlineData("@notaprefix:x")]
    [InlineData("@nocolon")]
    public void AnUnrecognisedPrefixIsALiteralRatherThanAnError(string raw)
    {
        // A password can begin with '@'. Treating an unknown prefix as a mistake would refuse a
        // perfectly legal credential.
        SecretRef.Parse(raw).Source.ShouldBe(SecretSource.Literal);
    }

    [Fact]
    public void DescribeNeverReturnsTheValue()
    {
        // Safe by construction: there is no branch that could return one.
        SecretRef.Parse("hunter2").Describe().ShouldNotContain("hunter2");
        SecretRef.Parse("@secret:ses-smtp").Describe().ShouldBe("secret:ses-smtp");
        SecretRef.None.Describe().ShouldBe("none");
    }

    [Fact]
    public void AnAbsentReferenceHasNoValue()
    {
        SecretRef.Parse(null).ShouldBe(SecretRef.None);
        SecretRef.Parse("").HasValue.ShouldBeFalse();
    }

    [Fact]
    public void ACallerThatCannotSeeTheStoreAnswersNull()
    {
        // The reason Exists is bool? and not bool. The unelevated GUI runs config check and
        // cannot read secrets.dat; a two-valued answer would make it report a missing secret on
        // a perfectly healthy machine, to the person least able to evaluate that claim.
        new UnknownSecretLookup().Exists("ses-smtp").ShouldBeNull();
    }
}
