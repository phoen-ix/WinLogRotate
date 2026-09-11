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
        var result = await _cli.RunAsync(MainForm.BuildArgs(_configDir, "config", "show", "--json"))
            .ConfigureAwait(true);

        _grid.Rows.Clear();

        if (!result.Ok)
        {
            _status.Text = result.Describe();
            _status.ForeColor = Theme.Current.Danger;
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(result.StdOut);
            var jobs = document.RootElement.GetProperty("result").GetProperty("jobs");

            foreach (var job in jobs.EnumerateArray())
            {
                var kind = job.GetProperty("kind").GetString() ?? "";
                var policy = kind.Equals("Manage", StringComparison.OrdinalIgnoreCase)
                    ? $"keep {job.GetProperty("rotate").GetInt32()}, never touch the newest {job.GetProperty("liveFiles").GetInt32()}"
                    : $"{job.GetProperty("schedule").GetString()?.ToLowerInvariant()}, keep {job.GetProperty("rotate").GetInt32()}";

                _grid.Rows.Add(
                    job.GetProperty("name").GetString() ?? "",
                    kind.ToLowerInvariant(),
                    string.Join("; ", job.GetProperty("paths").EnumerateArray()
                        .Select(p => p.GetString() ?? "")),
                    policy);
            }

            _status.ForeColor = Theme.Current.Muted;
            _status.Text = _grid.Rows.Count == 0
                ? "No jobs configured yet."
                : $"{_grid.Rows.Count} job(s).";
        }
        catch (JsonException e)
        {
            // A malformed envelope means a version mismatch far more often than a bug, so say
            // something more useful than "unexpected character".
            _status.ForeColor = Theme.Current.Danger;
            _status.Text = "Could not read the response from winlogrotate.exe.";
            LrDialog.Error(this, "Unexpected response",
                "winlogrotate.exe returned something this window could not read. " +
                "This usually means the two are different versions.", e.Message);
        }
    }

    private async Task CheckAsync()
    {
        var result = await _cli.RunAsync(MainForm.BuildArgs(_configDir, "config", "check"))
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
        var result = await _cli.RunAsync(MainForm.BuildArgs(_configDir, "doctor", "--json"))
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
        catch (JsonException)
        {
        }
    }
}
