using Shouldly;
using WinLogRotate.Contracts;
using WinLogRotate.Core.Configuration;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// The <c>[host]</c> table: when the scheduled task fires.
/// </summary>
/// <remarks>
/// The registrar has always taken a time of day and nothing ever set it, so every task fired at
/// three in the morning and there was nowhere to say otherwise. The time is configuration rather
/// than an argument because <c>host repair</c> re-registers from it: a time that lived only in the
/// task was put back to 03:00 by the next repair, silently.
/// </remarks>
public sealed class HostSettingsTests
{
    private readonly DiagnosticBag _bag = new();

    private HostSettings Bind(string toml) =>
        ConfigBinder.BindHost(TomlFile.Parse(toml, "config.toml"), _bag);

    [Fact]
    public void ThreeInTheMorningWhenTheTableIsAbsent()
    {
        var settings = Bind("schema = 1\n");

        settings.Time.ShouldBe(TimeSpan.FromHours(3));
        settings.TimeText.ShouldBe("03:00");
        _bag.Items.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("22:30", 22, 30)]
    [InlineData("7:05", 7, 5)]
    [InlineData("00:00", 0, 0)]
    public void ATimeOfDayBinds(string written, int hour, int minute)
    {
        var settings = Bind($"[host]\ntime = \"{written}\"\n");

        settings.Time.ShouldBe(new TimeSpan(hour, minute, 0));
        _bag.Items.ShouldBeEmpty();
    }

    /// <summary>The spelling the page, doctor and the command line share is two digits each.</summary>
    [Fact]
    public void TheTextFormIsAlwaysTwoDigitsEach() =>
        Bind("[host]\ntime = \"7:05\"\n").TimeText.ShouldBe("07:05");

    /// <summary>
    /// A value the registrar could not use is refused with its line and a remedy, and the
    /// default stands - the same shape as any other bad value in config.toml.
    /// </summary>
    [Theory]
    [InlineData("\"25:00\"")]
    [InlineData("\"3pm\"")]
    [InlineData("\"03:00:00\"")]
    [InlineData("\"\"")]
    public void AValueThatIsNotATimeOfDayIsAnError(string written)
    {
        var settings = Bind($"schema = 1\n\n[host]\ntime = {written}\n");

        settings.Time.ShouldBe(TimeSpan.FromHours(3));

        var d = _bag.Items.ShouldHaveSingleItem();
        d.Severity.ShouldBe(Severity.Error);
        d.Code.ShouldBe(DiagnosticCode.ConfigInvalid);
        d.Line.ShouldBe(4);
        d.Remedy.ShouldNotBeNull().ShouldContain("24-hour");
    }

    [Fact]
    public void AnUnquotedValueIsAnError()
    {
        Bind("[host]\ntime = 300\n");

        var d = _bag.Items.ShouldHaveSingleItem();
        d.Severity.ShouldBe(Severity.Error);
        d.Message.ShouldContain("quoted string");
    }

    /// <summary>
    /// A mistyped key is said, not skipped: <c>tme = "22:30"</c> registering a task at 03:00 in
    /// silence is the failure this table exists to prevent.
    /// </summary>
    [Fact]
    public void AnUnknownKeyIsWarnedAbout()
    {
        var settings = Bind("[host]\ntme = \"22:30\"\n");

        settings.Time.ShouldBe(TimeSpan.FromHours(3));

        var d = _bag.Items.ShouldHaveSingleItem();
        d.Severity.ShouldBe(Severity.Warning);
        d.Message.ShouldContain("'tme'");
    }

    /// <summary>
    /// One grammar, shared by the file and by <c>--at</c>: a 24-hour clock, minutes required,
    /// nothing after them.
    /// </summary>
    [Theory]
    [InlineData("03:00", true)]
    [InlineData("3:00", true)]
    [InlineData(" 22:30 ", true)]
    [InlineData("23:59", true)]
    [InlineData("24:00", false)]
    [InlineData("12:60", false)]
    [InlineData("3", false)]
    [InlineData("3pm", false)]
    [InlineData("03:00:00", false)]
    [InlineData("", false)]
    public void TheTimeGrammarIsATwentyFourHourClock(string text, bool accepted) =>
        HostSettings.TryParseTime(text, out _).ShouldBe(accepted);
}
