using Shouldly;
using WinLogRotate.Contracts;
using WinLogRotate.Core.Configuration;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// What config.toml says that nothing reads.
/// </summary>
/// <remarks>
/// docs/configuration.md promises that a key which is not a setting is "reported and ignored,
/// never silently dropped", and <c>[job]</c>, <c>[defaults]</c>, <c>[notify]</c> and <c>[host]</c>
/// kept it. A table name was never checked at all, so <c>[defualts]</c> took every default with
/// it and said nothing - a <c>rotate = 30</c> there became 7, and the next run deleted generations
/// 8 to 30, which is the very outcome <c>ConfigLoader</c> refuses to quarantine config.toml over.
/// <c>[journal]</c> and the provider tables did not check their keys either.
/// </remarks>
public sealed class ConfigTablesTests
{
    private static DiagnosticBag Root(string toml)
    {
        var bag = new DiagnosticBag();
        ConfigBinder.BindDefaults(TomlFile.Parse(toml, "config.toml"), bag);
        return bag;
    }

    [Theory]
    [InlineData("defualts", "defaults")]
    [InlineData("jounral", "journal")]
    [InlineData("hosts", "host")]
    public void AMisspelledTableIsReportedWithTheOneItMeant(string written, string meant)
    {
        var d = Root($"schema = 1\n\n[{written}]\nrotate = 30\n").Items.ShouldHaveSingleItem();

        d.Severity.ShouldBe(Severity.Warning);
        d.Code.ShouldBe(DiagnosticCode.ConfigInvalid);
        d.Message.ShouldContain($"[{written}]");
        d.Remedy.ShouldNotBeNull().ShouldContain($"[{meant}]");
        d.Line.ShouldBe(3);
    }

    [Fact]
    public void EveryTableThisFileReadsIsQuiet()
    {
        var bag = Root("""
            schema = 1

            [defaults]
            rotate = 14

            [notify]
            to = ["email.relay"]

            [notify.email.relay]
            host = "smtp.example.com"

            [notify.pushover.oncall]
            priority = 1

            [notify.webhook.slack]
            method = "POST"

            ["journal"]
            retain = 7

            [host]
            time = "22:30"
            """);

        bag.Items.ShouldBeEmpty();
    }

    /// <summary>
    /// <c>["journal"]</c> is the same table as <c>[journal]</c> in TOML, and is read like it.
    /// </summary>
    /// <remarks>
    /// The binder found a table by comparing its header's text, so the quoted spelling was read by
    /// nothing - and a check for unknown tables that parsed the header properly would have called
    /// it known. Both now read the header the same way.
    /// </remarks>
    [Fact]
    public void AQuotedTableNameIsReadLikeAnyOther() =>
        ConfigBinder.BindJournal(TomlFile.Parse("[\"journal\"]\nretain = 7\n", "config.toml"), new DiagnosticBag())
            .Retain.ShouldBe(7);

    /// <summary>A provider has a name, and a header without one is a provider nothing reads.</summary>
    [Theory]
    [InlineData("notify.email")]
    [InlineData("notify.teams.ops")]
    public void AProviderTableNothingReadsIsReported(string header)
    {
        var d = Root($"schema = 1\n\n[{header}]\nhost = \"x\"\n").Items.ShouldHaveSingleItem();

        d.Severity.ShouldBe(Severity.Warning);
        d.Message.ShouldContain($"[{header}]");
    }

    /// <summary>An array of provider tables is already an error of its own, and is said once.</summary>
    [Fact]
    public void AnArrayOfProvidersIsNotReportedTwice() =>
        Root("schema = 1\n\n[[notify.email]]\nhost = \"x\"\n").Items.ShouldBeEmpty();

    /// <summary>A setting above every table belongs in one, and says which.</summary>
    [Fact]
    public void ASettingAtTheTopIsReportedWithTheTableItBelongsIn()
    {
        var d = Root("schema = 1\nrotate = 30\n").Items.ShouldHaveSingleItem();

        d.Severity.ShouldBe(Severity.Warning);
        d.Message.ShouldContain("'rotate'");
        d.Remedy.ShouldNotBeNull().ShouldContain("[defaults]");
    }

    [Fact]
    public void AMisspelledJournalKeyIsReported()
    {
        var bag = new DiagnosticBag();
        ConfigBinder.BindJournal(TomlFile.Parse("[journal]\nretian = 7\nretain = 14\n", "config.toml"), bag);

        var d = bag.Items.ShouldHaveSingleItem();
        d.Severity.ShouldBe(Severity.Warning);
        d.Message.ShouldContain("'retian'");
        d.Remedy.ShouldNotBeNull().ShouldContain("'retain'");
    }

    [Theory]
    [InlineData("email.relay", "hots = \"x\"", "'host'")]
    [InlineData("pushover.oncall", "priorty = 1", "'priority'")]
    [InlineData("webhook.slack", "contenttype = \"application/json\"", "'content_type'")]
    public void AMisspelledProviderKeyIsReported(string provider, string line, string meant)
    {
        var bag = new DiagnosticBag();
        ConfigBinder.BindProviders(TomlFile.Parse($"[notify.{provider}]\n{line}\n", "config.toml"), bag);

        var d = bag.Items.ShouldHaveSingleItem();
        d.Severity.ShouldBe(Severity.Warning);
        d.Code.ShouldBe(DiagnosticCode.NotifyMisconfigured);
        d.Remedy.ShouldNotBeNull().ShouldContain(meant);
    }
}
