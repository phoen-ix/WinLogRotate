using System.Reflection;
using Shouldly;
using WinLogRotate.Contracts;
using WinLogRotate.Core.Engine;
using WinLogRotate.Core.Safety;
using WinLogRotate.Hosting.Diagnostics;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// The event-ID table is a published contract, not an implementation detail: an alert rule in
/// SCOM, Zabbix or a scheduled-task action names an event ID, and nothing tells its author when
/// that ID stops being produced. These tests are what make the table safe to publish.
/// </summary>
public sealed class EventIdsTests
{
    private static IReadOnlyList<(string Name, string Value)> AllCodes() =>
        [.. typeof(DiagnosticCode)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (f.Name, (string)f.GetRawConstantValue()!))];

    [Fact]
    public void ThereAreCodesToMap()
    {
        // Guards the three tests below: reflection returning nothing would make them all pass
        // while asserting nothing at all.
        AllCodes().Count.ShouldBeGreaterThan(15);
    }

    [Fact]
    public void EveryDiagnosticCodeHasItsOwnEventId()
    {
        // Falling back to Unclassified is the safety net for a code added tomorrow. A code that
        // exists today reaching it means the table was not updated alongside it.
        var unmapped = AllCodes()
            .Where(c => EventIds.For(c.Value) == EventIds.Unclassified)
            .Select(c => $"{c.Name} ({c.Value})")
            .ToArray();

        unmapped.ShouldBeEmpty(
            "every DiagnosticCode needs a row in EventIds.For and in docs/diagnostics.md");
    }

    [Fact]
    public void NoTwoCodesShareAnEventId()
    {
        // A shared ID makes an alert fire for a condition its author never chose.
        var collisions = AllCodes()
            .GroupBy(c => EventIds.For(c.Value))
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(c => c.Name))}")
            .ToArray();

        collisions.ShouldBeEmpty();
    }

    [Fact]
    public void EveryEventIdIsWithinTheRangeEventCreateCanRender()
    {
        // The installer registers EventMessageFile = %SystemRoot%\System32\EventCreate.exe,
        // whose message table covers 1..1000. Outside it, Event Viewer shows "The description
        // for Event ID N ... cannot be found" instead of the message.
        foreach (var (name, value) in AllCodes())
        {
            var id = EventIds.For(value);
            id.ShouldBeInRange(EventIds.MinId, EventIds.MaxId, name);
        }

        EventIds.Unclassified.ShouldBeInRange(EventIds.MinId, EventIds.MaxId);
        EventIds.RunCompletedQuiet.ShouldBeInRange(EventIds.MinId, EventIds.MaxId);
        EventIds.RunCompletedWithChanges.ShouldBeInRange(EventIds.MinId, EventIds.MaxId);
        EventIds.RunCompletedWithFailures.ShouldBeInRange(EventIds.MinId, EventIds.MaxId);
    }

    /// <remarks>
    /// Built from a real guard refusal rather than a hand-written diagnostic. It used to
    /// construct its own CliDiagnostic, render it, and assert the output echoed what it had just
    /// put in - so it proved that Render concatenates its input and would have passed with
    /// PathGuard deleted entirely. It was also the last place in the repo where the wording
    /// "Add the exact pattern to allowDangerous" survived, a remedy milestone 16 removed because
    /// it named a key that reached nothing.
    /// </remarks>
    [Fact]
    public void TheRenderedMessageCarriesEverythingAnOperatorNeeds()
    {
        var guard = new PathGuard(new GuardOptions
        {
            ProtectedRoots = ["C:\\Windows"],
            Overrides = OverrideGate.Open,
        });

        var refused = Diagnose.Refusal(
            guard.CheckPattern("C:\\Windows\\*.log", GuardScope.None), job: "bad job");

        var text = EventIds.Render(refused);

        text.ShouldContain("[bad job]");
        text.ShouldContain("resolves inside");
        text.ShouldContain("C:\\Windows\\*.log");

        // The remedy really reaches the operator, whatever it currently says.
        text.ShouldContain(refused.Remedy.ShouldNotBeNull());

        // The code is repeated in the body even though it decided the event ID: an operator
        // reading the message should not have to consult a table to identify the condition.
        text.ShouldContain(DiagnosticCode.DangerousPathRefused);
    }

    [Fact]
    public void RenderOmitsWhatIsNotThereRatherThanPrintingEmptyLabels()
    {
        var text = EventIds.Render(new CliDiagnostic
        {
            Severity = Severity.Warning,
            Code = DiagnosticCode.NoRunHost,
            Message = "No run host is registered.",
        });

        text.ShouldNotContain("Path:");
        text.ShouldStartWith("No run host is registered.");
    }
}
