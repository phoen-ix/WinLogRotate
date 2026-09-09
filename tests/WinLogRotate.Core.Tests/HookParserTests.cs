using Shouldly;
using WinLogRotate.Core.Import;
using WinLogRotate.Core.Notify;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// The scheme grammar. One parser serves both <c>postrotate</c> and <c>[notify]</c>, so these
/// cases pin what either of them will accept.
/// </summary>
public sealed class HookParserTests
{
    private static HookAction Parse(string raw)
    {
        var result = HookParser.Parse(raw);
        result.IsOk.ShouldBeTrue($"'{raw}' should parse, got {result.Error} {result.Scheme}");
        return result.Action!;
    }

    // ---- the case the whole grammar is designed around -------------------------------------

    [Fact]
    public void ADriveLetterIsNotAScheme()
    {
        // A scheme is two or more characters; a drive letter is one. Without this rule the most
        // ordinary hook anybody could write parses as scheme "C" with target "\tools\x.exe".
        var action = Parse(@"C:\tools\reopen.exe");

        action.Scheme.ShouldBe(HookScheme.Command);
        action.Target.ShouldBe(@"C:\tools\reopen.exe");
    }

    [Fact]
    public void AnExplicitCommandSchemeKeepsTheWholeDriveQualifiedPath()
    {
        var action = Parse(@"command:C:\tools\reopen.exe --now");

        action.Scheme.ShouldBe(HookScheme.Command);
        action.Target.ShouldBe(@"C:\tools\reopen.exe --now");
    }

    [Theory]
    [InlineData(@"cmd /c copy C:\a C:\b")]
    [InlineData(@"""C:\Program Files\app\reopen.exe"" -q")]
    [InlineData(@"\\server\share\tool.exe")]
    [InlineData("./relative.sh")]
    public void AnythingWithASeparatorOrASpaceBeforeTheColonIsACommandLine(string raw)
    {
        // Restricting the prefix to scheme characters covers this more thoroughly than looking
        // for path separators would - a space is not a scheme character either.
        Parse(raw).Scheme.ShouldBe(HookScheme.Command);
    }

    // ---- schemes ---------------------------------------------------------------------------

    [Fact]
    public void AUrlKeepsItsSchemeInTheTarget()
    {
        // For an address the scheme IS part of the address, so stripping it would leave
        // something that cannot be requested.
        var action = Parse("https://hooks.example.com/services/T000/B000/xxxx");

        action.Scheme.ShouldBe(HookScheme.Http);
        action.Target.ShouldBe("https://hooks.example.com/services/T000/B000/xxxx");
    }

    [Fact]
    public void PlainHttpParsesToTheSameScheme()
    {
        Parse("http://internal/hook").Scheme.ShouldBe(HookScheme.Http);
    }

    [Fact]
    public void TheEventLogAndANamedKernelEventAreDifferentThings()
    {
        // README.md documents event:Global\Name as signalling a named kernel event. Re-using it
        // for the Windows Event Log would silently change the meaning of a published directive,
        // so both spellings are pinned here side by side.
        Parse("eventlog:").Scheme.ShouldBe(HookScheme.EventLog);
        Parse(@"event:Global\MyApp.Reopen").Scheme.ShouldBe(HookScheme.Event);
        Parse(@"event:Global\MyApp.Reopen").Target.ShouldBe(@"Global\MyApp.Reopen");
    }

    [Fact]
    public void EventLogNeedsNoTarget()
    {
        HookSchemes.RequiresTarget(HookScheme.EventLog).ShouldBeFalse();
        Parse("eventlog:").Target.ShouldBe(string.Empty);
    }

    [Fact]
    public void ServiceTakesAVerbAndAName()
    {
        var action = Parse("service:paramchange:nginx");

        action.Scheme.ShouldBe(HookScheme.Service);
        action.Verb.ShouldBe("paramchange");
        action.Target.ShouldBe("nginx");
    }

    [Fact]
    public void ServiceWithoutAVerbMeansTheDefaultControlCode()
    {
        var action = Parse("service:nginx");

        action.Scheme.ShouldBe(HookScheme.Service);
        action.Verb.ShouldBeNull();
        action.Target.ShouldBe("nginx");
    }

    [Theory]
    [InlineData("smtp:ops@example.com", HookScheme.Smtp, "ops@example.com")]
    [InlineData("pushover:uQiRzpo4", HookScheme.Pushover, "uQiRzpo4")]
    public void TheProviderSchemesCarryTheirTarget(string raw, HookScheme scheme, string target)
    {
        var action = Parse(raw);
        action.Scheme.ShouldBe(scheme);
        action.Target.ShouldBe(target);
    }

    // ---- refusals ---------------------------------------------------------------------------

    [Fact]
    public void AnUnknownSchemeIsRefusedRatherThanExecuted()
    {
        // The one that matters. A typo must never become an attempt to run a program called
        // "htp://hooks.example.com".
        var result = HookParser.Parse("htp://hooks.example.com/x");

        result.IsOk.ShouldBeFalse();
        result.Error.ShouldBe(HookParseError.UnknownScheme);
        result.Scheme.ShouldBe("htp");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyTargetIsRefused(string raw)
    {
        HookParser.Parse(raw).Error.ShouldBe(HookParseError.Empty);
    }

    [Fact]
    public void AnEmptyServiceNameStillParses()
    {
        // "systemctl reload ''" is a thing the importer can meet, and the result must reach the
        // validator with a line and column rather than dying in the parser - that is what keeps
        // "every string the importer emits parses" literally true.
        var result = HookParser.Parse("service:paramchange:");

        result.IsOk.ShouldBeTrue();
        result.Action!.Target.ShouldBe(string.Empty);
    }

    // ---- the importer contract ---------------------------------------------------------------

    [Theory]
    [InlineData("nginx", "postrotate\n  /bin/kill -USR1 `cat /var/run/nginx.pid`\nendscript")]
    [InlineData("apache", "postrotate\n  /bin/kill -HUP `cat /var/run/apache2.pid`\nendscript")]
    [InlineData("systemd", "postrotate\n  systemctl reload my-app.service\nendscript")]
    [InlineData("quoted", "postrotate\n  systemctl reload 'weird name'\nendscript")]
    public void WhateverTheImporterActuallyWritesParses(string name, string script)
    {
        // Driven through the real importer rather than against strings copied out of it, so this
        // keeps holding if TranslateScript learns a new translation. If the parser ever rejected
        // one of these, every imported nginx job would become invalid the day hooks land - and
        // the import happened months earlier, so nobody would connect the two.
        var jobs = LogrotateImporter.Import(
            $"/var/log/{name}/*.log {{\n  daily\n  rotate 7\n{script}\n}}\n", $"{name}.conf");

        var emitted = jobs
            .SelectMany(j => j.Toml.Split('\n'))
            .Where(l => l.Contains("service:", StringComparison.Ordinal))
            .SelectMany(ExtractQuoted)
            .ToArray();

        emitted.ShouldNotBeEmpty($"the importer produced no service: hook for {name}");

        foreach (var target in emitted)
        {
            var action = Parse(target);
            action.Scheme.ShouldBe(HookScheme.Service);
        }
    }

    /// <summary>Pulls the double-quoted values out of a TOML line.</summary>
    private static IEnumerable<string> ExtractQuoted(string line)
    {
        var parts = line.Split('"');
        for (var i = 1; i < parts.Length; i += 2)
        {
            if (parts[i].StartsWith("service:", StringComparison.Ordinal))
            {
                yield return parts[i];
            }
        }
    }

    // ---- identity ----------------------------------------------------------------------------

    [Fact]
    public void TwoWebhooksOnOneHostAreDifferentChannels()
    {
        // The reason Key is not the masked target. Masking removes a webhook's path, and the
        // path is the only thing telling two Slack hooks apart - keyed on the mask they would
        // collapse into one channel, so a breaker opened by one would suppress the other.
        var a = Parse("https://hooks.slack.com/services/T000/B111/aaaa");
        var b = Parse("https://hooks.slack.com/services/T000/B222/bbbb");

        a.Key.ShouldNotBe(b.Key);
    }

    [Fact]
    public void TheKeyRevealsNothingBeyondTheHost()
    {
        var action = Parse("https://hooks.slack.com/services/T000/B111/super-secret-path");

        action.Key.ShouldNotContain("super-secret-path");
        action.Key.ShouldContain("hooks.slack.com");
    }

    [Fact]
    public void TheKeyIsStableForTheSameTarget()
    {
        Parse("https://h/x").Key.ShouldBe(Parse("https://h/x").Key);
    }

    [Fact]
    public void AProviderNamedTargetIsKeyedByItsName()
    {
        // What an operator types into "notify reset" and reads in "notify status".
        var action = HookAction.Create(
            HookScheme.Smtp, "smtp:ops@example.com", "ops@example.com", provider: "email.relay");

        action.Key.ShouldBe("email.relay");
    }

    // ---- redaction ----------------------------------------------------------------------------

    [Fact]
    public void AWebhookUrlIsMaskedToSchemeAndHost()
    {
        // The path IS the credential for Slack and Teams, so it cannot appear in a diagnostic.
        var action = Parse("https://hooks.slack.com/services/T000/B111/xoxb-secret");

        action.ToString().ShouldBe("https://hooks.slack.com/...");
        action.ToString().ShouldNotContain("xoxb-secret");
    }

    [Fact]
    public void UserinfoIsRemoved()
    {
        Parse("https://user:pass@example.com/hook").ToString().ShouldNotContain("pass");
    }

    [Fact]
    public void ACommandTargetIsNotMasked()
    {
        // Over-masking is not a safe default: nobody can fix "service:paramchange:***", and a
        // diagnostic that hides the thing it is complaining about just gets redaction disabled.
        Parse("service:paramchange:nginx").ToString().ShouldContain("nginx");
        Parse(@"command:C:\tools\x.exe").ToString().ShouldContain(@"C:\tools\x.exe");
    }

    [Fact]
    public void AnUnparseableUrlIsMaskedEntirely()
    {
        // Its shape is unknown, so no part of it can be called safe.
        Redaction.MaskUrl("not a url at all").ShouldBe("***");
    }

    [Fact]
    public void UrlsInsideFreeTextAreMasked()
    {
        var masked = Redaction.MaskText(
            "POST to https://hooks.slack.com/services/T/B/secret failed with 404");

        masked.ShouldNotContain("secret");
        masked.ShouldContain("404");
    }

    [Fact]
    public void OperatorConfiguredWordsAreMaskedToo()
    {
        Redaction.MaskText("job for BigCustomer failed", ["BigCustomer"])
            .ShouldNotContain("BigCustomer");
    }

    // ---- the execution gate ---------------------------------------------------------------------

    [Fact]
    public void OnlyExecutingSchemesNeedAHardenedDirectory()
    {
        // Refusing the reporting schemes on a loose directory would remove the only channel able
        // to say that the directory is loose.
        HookSchemes.NeedsHardenedConfDir(HookScheme.Command).ShouldBeTrue();
        HookSchemes.NeedsHardenedConfDir(HookScheme.Service).ShouldBeTrue();
        HookSchemes.NeedsHardenedConfDir(HookScheme.Event).ShouldBeTrue();

        HookSchemes.NeedsHardenedConfDir(HookScheme.Http).ShouldBeFalse();
        HookSchemes.NeedsHardenedConfDir(HookScheme.Smtp).ShouldBeFalse();
        HookSchemes.NeedsHardenedConfDir(HookScheme.Pushover).ShouldBeFalse();
        HookSchemes.NeedsHardenedConfDir(HookScheme.EventLog).ShouldBeFalse();
    }
}
