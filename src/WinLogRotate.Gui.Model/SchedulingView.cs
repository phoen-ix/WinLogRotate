using System.Text.Json;

namespace WinLogRotate.Gui.Cli;

/// <summary>Which run host a page is offering, and which one is registered.</summary>
public enum RunHostChoice
{
    /// <summary>A scheduled task running as SYSTEM.</summary>
    Task,

    /// <summary>A resident service with its own timer.</summary>
    Service,

    /// <summary>Nothing runs rotations on its own.</summary>
    None,
}

/// <summary>Everything the Scheduling page shows, and nothing that needs a window.</summary>
public sealed record SchedulingView
{
    /// <summary>The option that must be selected when the page is drawn.</summary>
    public required RunHostChoice Selected { get; init; }

    /// <summary>The sentence above the options.</summary>
    public required string Current { get; init; }

    /// <summary>True when that sentence is a warning rather than a statement of fact.</summary>
    public required bool Warn { get; init; }

    /// <summary>
    /// False when the answer could not be established, so nothing may be inferred from
    /// <see cref="Selected"/>.
    /// </summary>
    /// <remarks>
    /// The page disables Apply on this. A selection the operator did not make must never become
    /// an instruction, and when the current host is unknown every selection is one of those.
    /// </remarks>
    public required bool Known { get; init; }
}

/// <summary>
/// Turns a <c>doctor --json</c> response into what the Scheduling page shows and offers.
/// </summary>
/// <remarks>
/// <para>
/// The page set <c>_task.Checked = true</c> in its constructor and never changed it - the only
/// assignment to any of the three radio buttons in the file - while the registered host went into
/// a text label beside them. <c>ApplyAsync</c> then read the radio group as the operator's
/// intent.
/// </para>
/// <para>
/// So on a service-hosted machine the page opened showing "Scheduled task" selected, with a line
/// above it reading "Currently: Service", and pressing Apply ran <c>host use task</c> - which
/// removes the service. The operator changed nothing and lost their run host.
/// </para>
/// <para>
/// Here rather than on the page for the reason <see cref="JournalHistory"/> is: a project that
/// references the GUI carries a Microsoft.WindowsDesktop.App framework reference and cannot be
/// built on the Linux leg at all, so a defect in that constructor was unreachable by any test
/// that runs where the tests run.
/// </para>
/// </remarks>
public static class SchedulingProjection
{
    /// <summary>What to show when the CLI could not be asked, or answered badly.</summary>
    /// <remarks>
    /// <c>None</c> is deliberately not the fallback, even though it is the safe-sounding one:
    /// "none" is a real answer that Apply would act on by removing whatever is registered.
    /// <see cref="SchedulingView.Known"/> is how not-knowing is said.
    /// </remarks>
    private static SchedulingView Unknown(string sentence) => new()
    {
        Selected = RunHostChoice.None,
        Current = sentence,
        Warn = true,
        Known = false,
    };

    public static SchedulingView From(CliResult result)
    {
        if (!result.Ok)
        {
            return Unknown(result.Describe());
        }

        string? host;
        string? detail;

        try
        {
            using var document = JsonDocument.Parse(result.StdOut);
            var payload = document.RootElement.GetProperty("result");

            host = payload.GetProperty("runHost").GetString();
            detail = payload.GetProperty("runHostDetail").GetString();
        }
        catch (Exception e) when (e is JsonException
                                      or KeyNotFoundException
                                      or InvalidOperationException)
        {
            // GetProperty throws KeyNotFoundException and the typed accessors throw
            // InvalidOperationException, neither of which a JsonException filter catches - so an
            // envelope that parsed but was missing a field used to escape into an async void
            // handler.
            return Unknown("Could not read the current status.");
        }

        if (Parse(host) is not { } choice)
        {
            return Unknown($"The current run host was reported as '{host}', which this version "
                         + "does not recognise.");
        }

        return new SchedulingView
        {
            Selected = choice,
            Current = choice == RunHostChoice.None
                ? $"Currently: {host} ({detail})  -  nothing is running rotations, so the "
                  + "configuration will never be applied."
                : $"Currently: {host} ({detail})",
            Warn = choice == RunHostChoice.None,
            Known = true,
        };
    }

    /// <summary>
    /// The wire spelling, which is <c>RunHostKind.ToString()</c> and therefore PascalCase.
    /// </summary>
    /// <remarks>
    /// Case-insensitive on purpose. The GUI ships separately and may be driven by an older or
    /// newer winlogrotate.exe, and a casing change on the wire must not silently become "select
    /// the first option and remove their service".
    /// </remarks>
    private static RunHostChoice? Parse(string? host) => host?.ToUpperInvariant() switch
    {
        "TASK" => RunHostChoice.Task,
        "SERVICE" => RunHostChoice.Service,
        "NONE" => RunHostChoice.None,
        _ => null,
    };
}
