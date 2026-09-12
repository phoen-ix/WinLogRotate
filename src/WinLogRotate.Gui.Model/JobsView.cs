using System.Text.Json;

namespace WinLogRotate.Gui.Cli;

/// <summary>One job, as the Jobs page lists it.</summary>
public sealed record JobRow
{
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public required string Paths { get; init; }
    public required string Policy { get; init; }

    /// <summary>False for a job the run will not consider at all.</summary>
    public required bool Enabled { get; init; }
}

/// <summary>Everything the Jobs page lists, and nothing that needs a window.</summary>
public sealed record JobsView
{
    public required IReadOnlyList<JobRow> Rows { get; init; }

    /// <summary>The line under the grid.</summary>
    public required string Summary { get; init; }

    /// <summary>The response made no sense, so the rows say nothing rather than nothing much.</summary>
    public required bool Unreadable { get; init; }
}

/// <summary>
/// Turns a <c>config show --json</c> response into rows.
/// </summary>
/// <remarks>
/// <para>
/// The page never read <c>enabled</c> - the word appears nowhere in either GUI project - so a
/// disabled job rendered identically to a live one and was counted in "2 job(s)". An operator
/// looking at this page to find out what runs tonight was told that a job which is switched off
/// runs tonight. The CLI's own text output has printed it all along.
/// </para>
/// <para>
/// Here rather than on the page because a project referencing the GUI carries a
/// Microsoft.WindowsDesktop.App framework reference and cannot be built on the Linux leg, so
/// nothing about this grid was reachable by a test.
/// </para>
/// </remarks>
public static class JobsProjection
{
    public static JobsView From(string json)
    {
        List<JobRow> rows = [];

        try
        {
            using var document = JsonDocument.Parse(json);
            var jobs = document.RootElement.GetProperty("result").GetProperty("jobs");

            foreach (var job in jobs.EnumerateArray())
            {
                var kind = job.GetProperty("kind").GetString() ?? "";

                rows.Add(new JobRow
                {
                    Name = job.GetProperty("name").GetString() ?? "",
                    Kind = kind.ToLowerInvariant(),
                    Paths = string.Join("; ", job.GetProperty("paths").EnumerateArray()
                        .Select(p => p.GetString() ?? "")),
                    Policy = Policy(job, kind),

                    // Absent means enabled. A GUI driven by an older winlogrotate.exe must not
                    // decide that every job is switched off.
                    Enabled = !job.TryGetProperty("enabled", out var enabled)
                              || enabled.ValueKind != JsonValueKind.False,
                });
            }
        }
        catch (Exception e) when (e is JsonException
                                      or KeyNotFoundException
                                      or InvalidOperationException)
        {
            // Wider than JsonException, because GetProperty throws KeyNotFoundException and the
            // typed accessors throw InvalidOperationException - so an envelope that parsed but
            // was missing a field used to escape into an async void handler, in a process that
            // installs no unhandled-exception handler.
            return new JobsView
            {
                Rows = [],
                Summary = "Could not read the response from winlogrotate.exe.",
                Unreadable = true,
            };
        }

        return new JobsView { Rows = rows, Summary = Summarise(rows), Unreadable = false };
    }

    /// <summary>
    /// The line under the grid, which counts what will run rather than what is written down.
    /// </summary>
    /// <remarks>
    /// A bare "2 job(s)" over a list in which one is switched off is the same untruth the missing
    /// column was, moved one line down.
    /// </remarks>
    private static string Summarise(IReadOnlyList<JobRow> rows)
    {
        if (rows.Count == 0)
        {
            return "No jobs configured yet.";
        }

        var disabled = rows.Count(r => !r.Enabled);

        return disabled == 0
            ? $"{rows.Count} job(s)."
            : $"{rows.Count} job(s), {disabled} disabled.";
    }

    private static string Policy(JsonElement job, string kind) =>
        kind.Equals("Manage", StringComparison.OrdinalIgnoreCase)
            ? $"keep {job.GetProperty("rotate").GetInt32()}, never touch the newest "
              + $"{job.GetProperty("liveFiles").GetInt32()}"
            : $"{job.GetProperty("schedule").GetString()?.ToLowerInvariant()}, "
              + $"keep {job.GetProperty("rotate").GetInt32()}";
}
