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
        var result = await _cli.RunAsync(CliArgs.For(_configDir, "journal", "--json"))
            .ConfigureAwait(true);

        _grid.Rows.Clear();

        if (!result.Ok)
        {
            _status.ForeColor = Theme.Current.Danger;
            _status.Text = result.Describe();

            // Exit 4 is the only code that says nothing about what was or was not done can be
            // relied on, so it is the only one worth interrupting for - a configuration error is
            // exit 2 and belongs in the status line underneath.
            if (result.IsDefect)
            {
                LrDialog.Error(this, "History", result.Describe(), result.Details);
            }

            return;
        }

        // Read by JournalHistory, which lives in a project a test can reference. What was here
        // was a JsonDocument walk that added one row per record - so every operation that really
        // happened appeared twice, its plan half and its apply half, and the count underneath
        // said the doubled number. None of that was reachable by a test while it lived beside a
        // DataGridView, which is the reason it survived three milestones.
        var view = JournalHistory.From(result.StdOut);

        if (view.Unreadable)
        {
            _status.ForeColor = Theme.Current.Danger;
            _status.Text = "Could not read the journal.";
            return;
        }

        foreach (var row in view.Rows)
        {
            _grid.Rows.Add(row.When, row.Job, row.What, row.File, row.Why);
        }

        _status.ForeColor = Theme.Current.Muted;
        _status.Text = view.Rows.Count == 0
            ? "Nothing has been rotated yet."
            : $"{view.Rows.Count} operation(s)."
              + (view.SkippedLines > 0
                  ? $" {view.SkippedLines} unreadable line(s) skipped - a previous run was probably terminated."
                  : "");
    }
}
