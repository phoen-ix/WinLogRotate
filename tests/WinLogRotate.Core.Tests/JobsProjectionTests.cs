using Shouldly;
using WinLogRotate.Gui.Cli;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// What the Jobs page lists, for a configuration with a job switched off in it.
/// </summary>
/// <remarks>
/// <para>
/// The page never read <c>enabled</c> - the word appears nowhere in either GUI project - so a
/// disabled job rendered identically to a live one and was counted in "2 job(s)". An operator
/// looking at this page to find out what runs tonight was told that a job which is switched off
/// runs tonight. The CLI's own text output has printed it all along, and the field has been on
/// the wire all along.
/// </para>
/// <para>
/// Unreachable by any test while it lived beside a <c>DataGridView</c>: a project referencing the
/// GUI carries a Microsoft.WindowsDesktop.App framework reference and cannot be built on the
/// Linux leg.
/// </para>
/// </remarks>
public sealed class JobsProjectionTests
{
    private const string TwoJobsOneOff = """
        {
          "result": {
            "jobs": [
              { "name": "iis", "kind": "Manage", "enabled": true,
                "paths": ["C:/inetpub/logs/*.log"], "rotate": 7, "liveFiles": 1,
                "schedule": "Daily" },
              { "name": "legacy", "kind": "Rotate", "enabled": false,
                "paths": ["C:/old/*.log"], "rotate": 4, "liveFiles": 1,
                "schedule": "Weekly" }
            ]
          }
        }
        """;

    [Fact]
    public void ADisabledJobIsSaidToBeDisabled()
    {
        var view = JobsProjection.From(TwoJobsOneOff);

        view.Rows.Count.ShouldBe(2);
        view.Rows[0].Name.ShouldBe("iis");
        view.Rows[0].Enabled.ShouldBeTrue();
        view.Rows[1].Name.ShouldBe("legacy");
        view.Rows[1].Enabled.ShouldBeFalse();
    }

    /// <summary>
    /// The count under the grid says how many will not run.
    /// </summary>
    /// <remarks>
    /// A bare "2 job(s)" over a list in which one is switched off is the same untruth the missing
    /// column was, moved one line down - so the summary is asserted as well as the rows.
    /// </remarks>
    [Fact]
    public void TheSummaryCountsWhatWillNotRun()
    {
        JobsProjection.From(TwoJobsOneOff).Summary.ShouldBe("2 job(s), 1 disabled.");

        JobsProjection.From("""
            { "result": { "jobs": [ { "name": "iis", "kind": "Manage", "enabled": true,
              "paths": ["C:/logs/*.log"], "rotate": 7, "liveFiles": 1, "schedule": "Daily" } ] } }
            """)
            .Summary.ShouldBe("1 job(s).", "nothing is disabled, so nothing is said about it");

        JobsProjection.From("""{ "result": { "jobs": [] } }""")
            .Summary.ShouldBe("No jobs configured yet.");
    }

    /// <summary>
    /// A field that is not there means enabled, not disabled.
    /// </summary>
    /// <remarks>
    /// The GUI ships separately from the CLI and may be driven by an older winlogrotate.exe. A
    /// missing field must not make this page report every job on the machine as switched off -
    /// which is the failure mode of reading it with a plain <c>GetProperty</c>, and of defaulting
    /// a bool the other way.
    /// </remarks>
    [Fact]
    public void AJobWithNoEnabledFieldIsEnabled()
    {
        var view = JobsProjection.From("""
            { "result": { "jobs": [ { "name": "iis", "kind": "Manage",
              "paths": ["C:/logs/*.log"], "rotate": 7, "liveFiles": 1, "schedule": "Daily" } ] } }
            """);

        view.Rows.ShouldHaveSingleItem().Enabled.ShouldBeTrue();
        view.Summary.ShouldBe("1 job(s).");
    }

    /// <summary>
    /// An envelope this window cannot read says so, rather than throwing out of an async handler.
    /// </summary>
    /// <remarks>
    /// Wider than <c>JsonException</c> on purpose: <c>GetProperty</c> throws
    /// <c>KeyNotFoundException</c> and the typed accessors throw <c>InvalidOperationException</c>,
    /// so an envelope that parses but is missing a field escaped into an <c>async void</c>
    /// handler in a process that installs no unhandled-exception handler.
    /// </remarks>
    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{ "result": { } }""")]
    [InlineData("""{ "result": { "jobs": [ { "name": "iis" } ] } }""")]
    [InlineData("""{ "result": { "jobs": [ { "name": "iis", "kind": "Rotate", "paths": [], "rotate": "seven", "liveFiles": 1, "schedule": "Daily" } ] } }""")]
    public void AnUnreadableEnvelopeSaysSo(string json)
    {
        var view = JobsProjection.From(json);

        view.Unreadable.ShouldBeTrue();
        view.Rows.ShouldBeEmpty();
        view.Summary.ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>A manage job and a rotate job describe their policy differently.</summary>
    [Fact]
    public void EachKindDescribesItsOwnPolicy()
    {
        var view = JobsProjection.From(TwoJobsOneOff);

        view.Rows[0].Policy.ShouldBe("keep 7, never touch the newest 1");
        view.Rows[1].Policy.ShouldBe("weekly, keep 4");
    }
}
