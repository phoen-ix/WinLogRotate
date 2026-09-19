using WinLogRotate.Gui.Cli;
using WinLogRotate.Gui.Ui;

namespace WinLogRotate.Gui.Pages;

/// <summary>
/// What was actually compressed, moved or deleted.
/// </summary>
/// <remarks>
/// <para>
/// Read from the journal, never from Task Scheduler's Last Run Result. The registered task
/// passes <c>--lock-held-exit 0</c> so an overlapping run does not look like a failure in
/// <c>taskschd.msc</c> - which means 0x0 no longer proves work happened, and the journal is the
/// only honest source.
/// </para>
/// <para>
/// The File and Why cells wrap and the rows grow to fit. The columns share the width of the
/// window, and a sentence such as "first time this log has been seen; its clock starts now" was
/// clipped to an ellipsis in exactly the column that answers "why was nothing rotated?". The
/// status line names the journal's directory, taken from the verb's own answer rather than
/// worked out here, and Open folder opens it - so "where is this saved?" is answered on the page
/// that shows it.
/// </para>
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
        AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
    };

    private readonly Button _openFolder = new()
    {
        Text = "Open folder",
        Width = 100,
        FlatStyle = FlatStyle.System,

        // Until a load has said where the journal is, and that the directory exists.
        Enabled = false,
    };

    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 24 };

    /// <summary>The journal directory the last load reported, or empty.</summary>
    private string _directory = string.Empty;

    public HistoryPage(CliRunner cli, string? configDir)
    {
        _cli = cli;
        _configDir = configDir;

        // Scaled with the monitor, in the order MainForm explains: layout suspended, the mode
        // and the dimensions, the controls, and then the one scale. A page is created after
        // that window has scaled, so it is not scaled by it and does this for itself.
        SuspendLayout();
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);

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

        // The two columns that carry a sentence or a path. When, Job and What are short by
        // construction and stay on one line.
        _grid.Columns["file"]!.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _grid.Columns["why"]!.DefaultCellStyle.WrapMode = DataGridViewTriState.True;

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40 };
        var refresh = new Button { Text = "Refresh", Width = 90, FlatStyle = FlatStyle.System };
        refresh.Click += async (_, _) => await LoadAsync().ConfigureAwait(true);
        _openFolder.Click += (_, _) => OpenFolder();
        toolbar.Controls.AddRange([refresh, _openFolder]);

        Controls.Add(_grid);
        Controls.Add(toolbar);
        Controls.Add(_status);

        Load += async (_, _) => await LoadAsync().ConfigureAwait(true);

        ResumeLayout(false);
        PerformAutoScale();
    }

    private async Task LoadAsync()
    {
        var result = await _cli.RunAsync(CliArgs.For(_configDir, "journal", "--json"))
            .ConfigureAwait(true);

        // Navigated away from while the verb ran. The page is disposed and there is nothing to
        // fill; a dialog owned by it would be the exception this guard exists to prevent.
        if (IsDisposed)
        {
            return;
        }

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

        _directory = view.Directory;
        _openFolder.Enabled = _directory.Length > 0 && Directory.Exists(_directory);

        _status.ForeColor = Theme.Current.Muted;
        _status.Text = (view.Rows.Count == 0
                ? "Nothing has been rotated yet."
                : $"{view.Rows.Count} operation(s).")
            + (view.SkippedLines > 0
                ? $" {view.SkippedLines} unreadable line(s) skipped - a previous run was probably terminated."
                : "")
            + (_directory.Length > 0 ? $" Journal: {_directory}" : "");
    }

    /// <summary>
    /// Opens the journal directory in Explorer.
    /// </summary>
    /// <remarks>
    /// The same shape as the Jobs page's Open folder: through the shell, the handle disposed
    /// rather than discarded, and a failure swallowed - opening a folder is a convenience, and the
    /// path is on the status line for anybody who would rather type it.
    /// </remarks>
    private void OpenFolder()
    {
        if (_directory.Length == 0 || !Directory.Exists(_directory))
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_directory)
            {
                UseShellExecute = true,
            })?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Nothing to do that the status line does not already do.
        }
    }
}
