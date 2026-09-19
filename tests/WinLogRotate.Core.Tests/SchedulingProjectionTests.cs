using Shouldly;
using WinLogRotate.Cli.Commands;
using WinLogRotate.Core;
using WinLogRotate.Gui.Cli;
using WinLogRotate.Hosting.Hosts;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// What the Scheduling page shows, and which option it offers, for a given machine.
/// </summary>
/// <remarks>
/// <para>
/// The page set <c>_task.Checked = true</c> in its constructor and never changed it - the only
/// assignment to any of the three radio buttons in the file - while the registered host went into
/// a label beside them. <c>ApplyAsync</c> then read the radio group as the operator's intent.
/// </para>
/// <para>
/// So on a service-hosted machine the page opened showing "Scheduled task" selected above a line
/// reading "Currently: Service", and pressing Apply ran <c>host use task</c>, which removes the
/// service. The operator changed nothing and lost their run host.
/// </para>
/// <para>
/// None of that was reachable by a test: a project referencing the GUI carries a
/// Microsoft.WindowsDesktop.App framework reference and cannot be built on the Linux leg at all.
/// Moving the projection is what makes it assertable, which is the same move
/// <c>JournalHistory</c> made and the reason three defects fell out of that one.
/// </para>
/// </remarks>
public sealed class SchedulingProjectionTests
{
    private static CliResult Doctor(string host, string detail = "registered", string? time = null) => new()
    {
        ExitCode = ExitCode.Ok,
        StdOut = time is null
            ? $$"""{ "result": { "runHost": "{{host}}", "runHostDetail": "{{detail}}" } }"""
            : $$"""{ "result": { "runHost": "{{host}}", "runHostDetail": "{{detail}}", "runHostTime": "{{time}}" } }""",
        StdErr = "",
    };

    /// <summary>The picker shows what config.toml says, which is what doctor reports.</summary>
    [Fact]
    public void TheConfiguredTimeIsShown() =>
        SchedulingProjection.From(Doctor("Task", time: "22:30")).Time.ShouldBe(new TimeSpan(22, 30, 0));

    /// <summary>
    /// An older CLI sends no time, and one whose config.toml would not give one sends nothing
    /// readable. The page opens either way, showing the registrar's own default.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("banana")]
    [InlineData("")]
    public void ATimeDoctorDidNotGiveIsThreeInTheMorning(string? time)
    {
        var view = SchedulingProjection.From(Doctor("Task", time: time));

        view.Time.ShouldBe(TimeSpan.FromHours(3));
        view.Known.ShouldBeTrue("a missing time is not a missing answer about the host");
    }

    /// <summary>
    /// An option is offered exactly when the verb would accept it.
    /// </summary>
    /// <remarks>
    /// <c>host use service</c> refuses unconditionally - the service host is not implemented -
    /// and the page offered it as a radio button anyway, so it was one click and a UAC prompt
    /// away from an error dialog. Tied to the verb's own judgement, so that a build which gains
    /// the host offers it without anybody remembering the page.
    /// </remarks>
    [Theory]
    [InlineData(RunHostChoice.Task, RunHostKind.Task)]
    [InlineData(RunHostChoice.Service, RunHostKind.Service)]
    [InlineData(RunHostChoice.None, RunHostKind.None)]
    public void AnOptionIsOfferedExactlyWhenTheVerbWouldAcceptIt(RunHostChoice choice, RunHostKind kind)
    {
        SchedulingProjection.Offered(choice).ShouldBe(HostCommand.Unsupported(kind) is null);

        // And the note under an option is there exactly when the option is not.
        (SchedulingProjection.Unavailable(choice) is null).ShouldBe(SchedulingProjection.Offered(choice));
    }

    /// <summary>The service host is not offered, and the note names the verb that would refuse it.</summary>
    [Fact]
    public void TheServiceHostIsNotOfferedAndTheNoteNamesTheVerb()
    {
        SchedulingProjection.Offered(RunHostChoice.Service).ShouldBeFalse();
        SchedulingProjection.Unavailable(RunHostChoice.Service).ShouldNotBeNull()
            .ShouldContain("host use service");
    }

    /// <summary>
    /// The option offered is the one that is registered.
    /// </summary>
    /// <remarks>
    /// Every host is a row, including Task. A projection that always answered Task would pass a
    /// test written only for the service case, and Task is precisely the answer the defect gave.
    /// </remarks>
    [Theory]
    [InlineData("Task", RunHostChoice.Task)]
    [InlineData("Service", RunHostChoice.Service)]
    [InlineData("None", RunHostChoice.None)]
    public void TheOptionOfferedIsTheOneThatIsRegistered(string host, RunHostChoice expected)
    {
        var view = SchedulingProjection.From(Doctor(host));

        view.Selected.ShouldBe(expected);
        view.Known.ShouldBeTrue();
        view.Current.ShouldContain(host);
    }

    /// <summary>A machine with no run host is warned about, and says why.</summary>
    [Fact]
    public void NoRunHostIsAWarningThatSaysWhatItMeans()
    {
        var view = SchedulingProjection.From(Doctor("None", "not registered"));

        view.Warn.ShouldBeTrue();
        view.Current.ShouldContain("nothing is running rotations");
    }

    /// <summary>A registered host is stated, not warned about.</summary>
    [Theory]
    [InlineData("Task")]
    [InlineData("Service")]
    public void ARegisteredHostIsNotAWarning(string host) =>
        SchedulingProjection.From(Doctor(host)).Warn.ShouldBeFalse();

    /// <summary>
    /// When the answer cannot be established, nothing may be inferred from the selection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Known</c> is what the page disables Apply on. It exists because <c>None</c> is not a
    /// safe fallback: it is a real answer, and Apply would act on it by removing whatever is
    /// registered - turning a failed status read into the very loss this whole change is about.
    /// </para>
    /// <para>
    /// Four ways to not know, and they arrive by different routes: the verb failed; the output
    /// was not JSON; the envelope parsed but had no such field, which throws
    /// <c>KeyNotFoundException</c> rather than <c>JsonException</c>; and the field held a word
    /// this version does not recognise, which is what an older or newer CLI produces.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(ExitCode.Errors, """{"result":{"runHost":"Task","runHostDetail":"x"}}""")]
    [InlineData(ExitCode.Ok, "not json at all")]
    [InlineData(ExitCode.Ok, """{"result":{"somethingElse":1}}""")]
    [InlineData(ExitCode.Ok, """{"result":{"runHost":"Kubernetes","runHostDetail":"x"}}""")]
    public void AnAnswerThatCannotBeEstablishedIsNotASelection(int exitCode, string stdout)
    {
        var view = SchedulingProjection.From(
            new CliResult { ExitCode = exitCode, StdOut = stdout, StdErr = "" });

        view.Known.ShouldBeFalse();
        view.Warn.ShouldBeTrue();
        view.Current.ShouldNotBeNullOrWhiteSpace("the page has to say something");
    }

    /// <summary>
    /// The wire spelling is matched without regard to case.
    /// </summary>
    /// <remarks>
    /// The GUI ships separately and may be driven by an older or newer winlogrotate.exe. A casing
    /// change on the wire must not silently become "select the first option and remove their
    /// service" - it would land in the unrecognised branch above, which is safe, but matching
    /// case-insensitively means it does not land there at all.
    /// </remarks>
    [Theory]
    [InlineData("service")]
    [InlineData("SERVICE")]
    [InlineData("Service")]
    public void TheWireSpellingIsMatchedWithoutRegardToCase(string host) =>
        SchedulingProjection.From(Doctor(host)).Selected.ShouldBe(RunHostChoice.Service);
}
