using Shouldly;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Safety;
using WinLogRotate.Core.Secrets;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// What <c>config check</c> says about credentials.
/// </summary>
/// <remarks>
/// Driven through ConfigLoader rather than the binder, because the interesting behaviour is the
/// interaction between what was written and who is asking.
/// </remarks>
public sealed class NotifyCredentialTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-cred-");

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    /// <summary>Answers for a fixed set of names, the way an elevated caller can.</summary>
    private sealed class KnownSecrets(params string[] names) : ISecretLookup
    {
        public bool? Exists(string name) => names.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    private LoadedConfig Load(string configToml, ISecretLookup? secrets)
    {
        File.WriteAllText(Path.Combine(_dir.FullName, "config.toml"), configToml);
        Directory.CreateDirectory(Path.Combine(_dir.FullName, "conf.d"));

        return ConfigLoader.Load(
            InstallPaths.Resolve(_dir.FullName), new PathGuard(new GuardOptions()),
            quarantineBadFiles: false, secrets: secrets);
    }

    private const string WithLiteral = """
        schema = 1
        [notify.email.ses]
        host = "smtp.example.com"
        password = "hunter2"
        """;

    private const string WithReference = """
        schema = 1
        [notify.email.ses]
        host = "smtp.example.com"
        password = "@secret:ses-smtp"
        """;

    [Fact]
    public void APlaintextPasswordIsWarnedAboutAtItsOwnColumn()
    {
        var config = Load(WithLiteral, new KnownSecrets());

        var d = config.Diagnostics.FirstOrDefault(x => x.Code == DiagnosticCode.SecretInPlainConfig)
            .ShouldNotBeNull();
        d.Severity.ShouldBe(Severity.Warning);
        d.Line.ShouldBe(4);

        // Points at the value, not at column 1. An editor that jumps to the start of the line
        // makes the operator find the password themselves.
        d.Column.ShouldBe(12);
        d.Remedy.ShouldNotBeNull().ShouldContain("secret set");
    }

    [Fact]
    public void APlaintextPasswordIsNeverAnError()
    {
        // Warning, not error: refusing to run because a credential is in the clear turns a
        // notification problem into a disk-space problem, and drives people to hide the password
        // somewhere even less visible.
        Load(WithLiteral, new KnownSecrets()).HasErrors.ShouldBeFalse();
    }

    [Fact]
    public void TheWarningDoesNotRepeatTheValue()
    {
        var config = Load(WithLiteral, new KnownSecrets());

        foreach (var d in config.Diagnostics)
        {
            d.Message.ShouldNotContain("hunter2");
            (d.Remedy ?? string.Empty).ShouldNotContain("hunter2");
        }
    }

    [Fact]
    public void AStoredReferenceThatExistsIsSilent()
    {
        Load(WithReference, new KnownSecrets("ses-smtp"))
            .Diagnostics.ShouldNotContain(d =>
                d.Code == DiagnosticCode.SecretMissing || d.Code == DiagnosticCode.SecretInPlainConfig);
    }

    [Fact]
    public void AStoredReferenceThatIsMissingIsReportedButDoesNotStopTheRun()
    {
        // A warning, not an error, and the two validators in ConfigLoader have to agree on that.
        // An error would mean that the moment a real secret lookup is wired in, one mistyped
        // Pushover token name sets HasErrors and stops every rotation on the machine - turning a
        // notification problem into a disk-space problem, which is exactly what the sibling
        // ValidateNotifyTargets says in its own comment that it exists to avoid.
        var config = Load(WithReference, new KnownSecrets("something-else"));

        var d = config.Diagnostics.FirstOrDefault(x => x.Code == DiagnosticCode.SecretMissing)
            .ShouldNotBeNull();

        d.Severity.ShouldBe(Severity.Warning);
        d.Remedy.ShouldNotBeNull().ShouldContain("secret set ses-smtp");
        config.HasErrors.ShouldBeFalse();
    }

    [Fact]
    public void ANotifyMisconfigurationNeverChangesTheExitCode()
    {
        // Everything that can be wrong about notifications, at once. None of it may stop logs
        // being rotated: the rotation is the product, and the alerting is how you hear about it.
        var config = Load("""
            schema = 1
            [notify]
            to = ["email.typo", "htp://nope"]
            [notify.email.ses]
            host = "smtp.example.com"
            password = "@secret:not-stored"
            [notify.email.plain]
            host = "smtp.example.com"
            password = "in-the-clear"
            """, new KnownSecrets());

        config.Diagnostics.ShouldNotBeEmpty();
        config.HasErrors.ShouldBeFalse();
    }

    [Fact]
    public void ACallerThatCannotReadTheStoreReportsNothing()
    {
        // The regression this guards. secrets.dat grants Users nothing, so the unelevated GUI
        // running config check cannot answer - and a two-valued check would make it report a
        // missing secret on a perfectly healthy machine, to the person least able to evaluate
        // that claim.
        Load(WithReference, new UnknownSecretLookup())
            .Diagnostics.ShouldNotContain(d => d.Code == DiagnosticCode.SecretMissing);
    }

    [Fact]
    public void NoLookupAtAllReportsNothingEither()
    {
        Load(WithReference, secrets: null)
            .Diagnostics.ShouldNotContain(d => d.Code == DiagnosticCode.SecretMissing);
    }

    [Fact]
    public void ACredentialFreeProviderIsNeverWarnedAbout()
    {
        // The shape the documentation recommends first must not produce a warning, or the
        // recommendation reads as a compromise.
        var config = Load("""
            schema = 1
            [notify.email.relay]
            host = "smtp.corp.example"
            auth = "none"
            from = "winlogrotate@corp.example"
            to = ["oncall@corp.example"]
            """, new KnownSecrets());

        config.Diagnostics.ShouldNotContain(d =>
            d.Code == DiagnosticCode.SecretInPlainConfig || d.Code == DiagnosticCode.SecretMissing);
        config.NotifyProviders.ShouldHaveSingleItem().IsCredentialFree.ShouldBeTrue();
    }

    [Fact]
    public void ATargetNamingNoProviderIsReportedWithTheOnesThatExist()
    {
        var config = Load("""
            schema = 1
            [notify]
            to = ["email.typo"]
            [notify.email.relay]
            host = "smtp.corp.example"
            """, new KnownSecrets());

        var d = config.Diagnostics.FirstOrDefault(x => x.Code == DiagnosticCode.NotifyMisconfigured)
            .ShouldNotBeNull();
        d.Remedy.ShouldNotBeNull().ShouldContain("email.relay");
    }

    [Fact]
    public void AnInlineSchemeTargetIsNotMistakenForAProviderName()
    {
        Load("""
            schema = 1
            [notify]
            to = ["eventlog:", "https://hooks.example.com/x"]
            """, new KnownSecrets())
            .Diagnostics.ShouldNotContain(d => d.Code == DiagnosticCode.NotifyMisconfigured);
    }

    [Fact]
    public void TheBoundSettingsReachTheCaller()
    {
        // Carried on LoadedConfig so config check validates them and the run command does not
        // have to read config.toml a second time to find them.
        var config = Load("""
            schema = 1
            [notify]
            threshold = "critical"
            to = ["eventlog:"]
            """, new KnownSecrets());

        config.Notify.Threshold.ShouldBe(Severity.Critical);
        config.Notify.WouldSend.ShouldBeTrue();
    }
}
