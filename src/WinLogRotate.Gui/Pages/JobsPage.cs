using System.Text.Json;
using WinLogRotate.Gui.Cli;
using WinLogRotate.Gui.Ui;

namespace WinLogRotate.Gui.Pages;

/// <summary>Lists the configured jobs and what each one will do.</summary>
public sealed class JobsPage : UserControl
{
    private readonly CliRunner _cli;
    private readonly string? _configDir;
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        RowHeadersVisible = false,
        MultiSelect = false,
    };

    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 24 };

    public JobsPage(CliRunner cli, string? configDir)
    {
        _cli = cli;
        _configDir = configDir;

        _grid.Columns.Add("name", "Job");

        // Between the name and the kind, because it changes what everything after it means: a
        // disabled job's schedule and retention describe what would happen, not what will.
        _grid.Columns.Add("state", "State");
        _grid.Columns.Add("kind", "Kind");
        _grid.Columns.Add("paths", "Paths");
        _grid.Columns.Add("policy", "Policy");

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, AutoSize = false };

        var refresh = new Button { Text = "Refresh", Width = 90, FlatStyle = FlatStyle.System };
        refresh.Click += async (_, _) => await LoadAsync().ConfigureAwait(true);

        var check = new Button { Text = "Check configuration", Width = 160, FlatStyle = FlatStyle.System };
        check.Click += async (_, _) => await CheckAsync().ConfigureAwait(true);

        var open = new Button { Text = "Open folder", Width = 110, FlatStyle = FlatStyle.System };
        open.Click += async (_, _) => await OpenFolderAsync().ConfigureAwait(true);

        toolbar.Controls.AddRange([refresh, check, open]);

        Controls.Add(_grid);
        Controls.Add(toolbar);
        Controls.Add(_status);

        Load += async (_, _) => await LoadAsync().ConfigureAwait(true);
    }

    private async Task LoadAsync()
    {
        _status.Text = "Loading...";
        var result = await _cli.RunAsync(CliArgs.For(_configDir, "config", "show", "--json"))
            .ConfigureAwait(true);

        _grid.Rows.Clear();

        if (!result.Ok)
        {
            _status.Text = result.Describe();
            _status.ForeColor = Theme.Current.Danger;

            // Exit 4 is the only code that says nothing about what was or was not done can be
            // relied on, so it is the only one worth interrupting for - a configuration error is
            // exit 2 and belongs in the status line underneath.
            if (result.IsDefect)
            {
                LrDialog.Error(this, "Jobs", result.Describe(), result.Details);
            }

            return;
        }

        var view = JobsProjection.From(result.StdOut);

        foreach (var job in view.Rows)
        {
            var row = _grid.Rows[_grid.Rows.Add(
                job.Name, job.Enabled ? "on" : "disabled", job.Kind, job.Paths, job.Policy)];

            if (!job.Enabled)
            {
                // Greyed as well as labelled. A word in a column is easy to read past on a page
                // whose whole purpose is answering "what runs tonight".
                row.DefaultCellStyle.ForeColor = Theme.Current.Muted;
            }
        }

        _status.Text = view.Summary;
        _status.ForeColor = view.Unreadable ? Theme.Current.Danger : Theme.Current.Muted;

        if (view.Unreadable)
        {
            // A malformed envelope means a version mismatch far more often than a bug, so say
            // something more useful than "unexpected character".
            LrDialog.Error(this, "Unexpected response",
                "winlogrotate.exe returned something this window could not read. " +
                "This usually means the two are different versions.", result.StdOut);
        }
    }

    private async Task CheckAsync()
    {
        var result = await _cli.RunAsync(CliArgs.For(_configDir, "config", "check"))
            .ConfigureAwait(true);

        if (result.Ok)
        {
            LrDialog.Info(this, "Configuration", "No problems found.", result.StdOut);
            return;
        }

        // Exit 2 means nothing was attempted; exit 1 means a job was skipped and the rest
        // rotated. Milestone 17 introduced the second, and this sentence - written when 2 was
        // the only failure - then told an operator nothing would run when almost everything
        // would. The distinction is the whole point of having two codes.
        var everythingStopped = result.ExitCode == Core.ExitCode.ConfigInvalid;

        LrDialog.Show(this, DialogKind.Warning, "Configuration",
            everythingStopped
                ? "The configuration has problems. Nothing will run until they are fixed."
                : "One or more jobs have problems and will be skipped. The rest will still run.",
            result.StdOut + result.StdErr);
    }

    private async Task OpenFolderAsync()
    {
        var result = await _cli.RunAsync(CliArgs.For(_configDir, "doctor", "--json"))
            .ConfigureAwait(true);

        if (!result.Ok)
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(result.StdOut);
            var root = document.RootElement.GetProperty("result").GetProperty("root").GetString();
            if (root is not null && Directory.Exists(root))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(root)
                {
                    UseShellExecute = true,
                });
            }
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // Opening a folder is a convenience and failing to work out which one is not worth a
            // dialog. Caught at all only so that a missing field cannot end the process from an
            // async void handler.
        }
    }
}
