using System.Text.Json;
using WinLogRotate.Gui.Cli;
using WinLogRotate.Gui.Ui;

namespace WinLogRotate.Gui.Pages;

/// <summary>
/// What was actually compressed, moved or deleted.
/// </summary>
/// <remarks>
/// Read from the journal, never from Task Scheduler's Last Run Result. The registered task
/// passes <c>--lock-held-exit 0</c> so an overlapping run does not look like a failure in
/// <c>taskschd.msc</c> - which means 0x0 no longer proves work happened, and the journal is the
/// only honest source.
/// </remarks>
public sealed class HistoryPage : UserControl
{
    private readonly CliRunner _cli;
    private readonly string? _configDir;

    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
    };

    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 24 };

    public HistoryPage(CliRunner cli, string? configDir)
    {
        _cli = cli;
        _configDir = configDir;

        _grid.Columns.Add("when", "When");
        _grid.Columns.Add("job", "Job");
        _grid.Columns.Add("what", "What");
        _grid.Columns.Add("file", "File");
        _grid.Columns.Add("why", "Why");
        _grid.Columns["when"]!.FillWeight = 60;
        _grid.Columns["job"]!.FillWeight = 40;
        _grid.Columns["what"]!.FillWeight = 30;
        _grid.Columns["file"]!.FillWeight = 120;
        _grid.Columns["why"]!.FillWeight = 90;

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40 };
        var refresh = new Button { Text = "Refresh", Width = 90, FlatStyle = FlatStyle.System };
        refresh.Click += async (_, _) => await LoadAsync().ConfigureAwait(true);
        toolbar.Controls.Add(refresh);

        Controls.Add(_grid);
        Controls.Add(toolbar);
        Controls.Add(_status);

        Load += async (_, _) => await LoadAsync().ConfigureAwait(true);
    }

    private async Task LoadAsync()
    {
        var result = await _cli.RunAsync(MainForm.BuildArgs(_configDir, "journal", "--json"))
            .ConfigureAwait(true);

        _grid.Rows.Clear();

        if (!result.Ok)
        {
            _status.ForeColor = Theme.Current.Danger;
            _status.Text = result.Describe();
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(result.StdOut);
            var payload = document.RootElement.GetProperty("result");

            foreach (var entry in payload.GetProperty("entries").EnumerateArray())
            {
                var operation = entry.GetProperty("operation").GetString() ?? "";

                // Bookkeeping events are noise in a history view; the file operations are what
                // somebody came here to see. Guard verdicts and NUL-fill findings are decisions
                // rather than operations - milestone 17 gave them emitters, and without this they
                // became rows in a grid that says it shows what was compressed, moved or deleted,
                // and were counted as operations underneath it.
                if (operation.StartsWith("run.", StringComparison.Ordinal)
                    || operation.StartsWith("job.", StringComparison.Ordinal)
                    || operation.StartsWith("guard.", StringComparison.Ordinal)
                    || operation == "nulfill"

                    // Storing a credential is not something that happened to a log file. A hook
                    // is kept: it ran as part of a rotation, and "did the postrotate script
                    // fire?" is a question people come to a history for.
                    || operation == "secret")
                {
                    continue;
                }

                _grid.Rows.Add(
                    Field(entry, "ts"),
                    Field(entry, "job"),
                    operation,
                    Field(entry, "src"),
                    Field(entry, "reason"));
            }

            var skipped = payload.GetProperty("skippedLines").GetInt32();
            _status.ForeColor = Theme.Current.Muted;
            _status.Text = _grid.Rows.Count == 0
                ? "Nothing has been rotated yet."
                : $"{_grid.Rows.Count} operation(s)."
                  + (skipped > 0
                      ? $" {skipped} unreadable line(s) skipped - a previous run was probably terminated."
                      : "");
        }
        catch (JsonException)
        {
            _status.ForeColor = Theme.Current.Danger;
            _status.Text = "Could not read the journal.";
        }
    }

    private static string Field(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";
}
