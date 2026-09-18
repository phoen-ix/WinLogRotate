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

    // Never wrapping: the panel is one row high, so a wrapped button was a hidden one - which
    // is what Check configuration and Open folder were at the narrowest window. The widths come
    // from JobsToolbar, and WindowLayoutTests adds them up against that window.
    private readonly FlowLayoutPanel _toolbar = new()
    {
        Dock = DockStyle.Top,
        Height = 40,
        AutoSize = false,
        WrapContents = false,
    };

    /// <summary>Whether an action is in flight, so a second click during it does nothing.</summary>
    private bool _busy;

    public JobsPage(CliRunner cli, string? configDir)
    {
        _cli = cli;
        _configDir = configDir;

        // Scaled with the monitor, in the order MainForm explains: layout suspended, the mode
        // and the dimensions, the controls, and then the one scale. A page is created after
        // that window has scaled, so it is not scaled by it and does this for itself.
        SuspendLayout();
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);

        _grid.Columns.Add("name", "Job");

        // Between the name and the kind, because it changes what everything after it means: a
        // disabled job's schedule and retention describe what would happen, not what will.
        _grid.Columns.Add("state", "State");
        _grid.Columns.Add("kind", "Kind");
        _grid.Columns.Add("paths", "Paths");
        _grid.Columns.Add("policy", "Policy");

        var refresh = Tool(JobsToolbar.Refresh);
        refresh.Click += async (_, _) => await OneAtATimeAsync(() => LoadAsync()).ConfigureAwait(true);

        var check = Tool(JobsToolbar.Check);
        check.Click += async (_, _) => await OneAtATimeAsync(CheckAsync).ConfigureAwait(true);

        var open = Tool(JobsToolbar.Open);
        open.Click += async (_, _) => await OneAtATimeAsync(OpenFolderAsync).ConfigureAwait(true);

        var add = Tool(JobsToolbar.New);
        add.Click += async (_, _) => await OneAtATimeAsync(() => EditAsync(null)).ConfigureAwait(true);

        var edit = Tool(JobsToolbar.Edit);
        edit.Click += async (_, _) => await OneAtATimeAsync(EditSelectedAsync).ConfigureAwait(true);

        var toggle = Tool(JobsToolbar.Toggle);
        toggle.Click += async (_, _) => await OneAtATimeAsync(ToggleAsync).ConfigureAwait(true);

        var remove = Tool(JobsToolbar.Remove);
        remove.Click += async (_, _) => await OneAtATimeAsync(RemoveAsync).ConfigureAwait(true);

        // A double-click opens the row under the pointer rather than whatever was selected
        // before it, which is not the same row when the click also moves the selection.
        _grid.CellDoubleClick += async (_, e) =>
        {
            if (e.RowIndex >= 0)
            {
                var name = _grid.Rows[e.RowIndex].Cells["name"].Value as string;
                await OneAtATimeAsync(() => EditAsync(name)).ConfigureAwait(true);
            }
        };

        _toolbar.Controls.AddRange([add, edit, toggle, remove, refresh, check, open]);

        Controls.Add(_grid);
        Controls.Add(_toolbar);
        Controls.Add(_status);

        Load += async (_, _) => await OneAtATimeAsync(() => LoadAsync()).ConfigureAwait(true);

        ResumeLayout(false);
        PerformAutoScale();
    }

    /// <summary>A toolbar button as the layout describes it.</summary>
    private static Button Tool(ToolbarButton button) => new()
    {
        Text = button.Text,
        Width = button.Width,
        FlatStyle = FlatStyle.System,
    };

    /// <summary>
    /// Runs one action at a time, with the toolbar disabled while it runs.
    /// </summary>
    /// <remarks>
    /// Every button stayed enabled for the length of its await, and a Remove or Enable/Disable
    /// awaits a UAC prompt: a double-click was two prompts and two writes, and the second dialog
    /// opened on a page that might by then have been navigated away from. One flag and one
    /// disabled panel, rather than a guard per button, because a guard copied seven times is a
    /// guard that will be copied an eighth.
    /// </remarks>
    private async Task OneAtATimeAsync(Func<Task> action)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        _toolbar.Enabled = false;

        try
        {
            await action().ConfigureAwait(true);
        }
        finally
        {
            _busy = false;

            if (!IsDisposed)
            {
                _toolbar.Enabled = true;
            }
        }
    }

    /// <param name="said">
    /// What a write just did, in the verb's words, to show beside the count. Null on a plain
    /// refresh.
    /// </param>
    private async Task LoadAsync(string? said = null)
    {
        _status.Text = "Loading...";
        var result = await _cli.RunAsync(CliArgs.For(_configDir, "config", "show", "--json"))
            .ConfigureAwait(true);

        // Navigated away from while the verb ran. There is no grid left to fill, and a dialog
        // owned by a disposed page is itself the exception this guard exists to prevent.
        if (IsDisposed)
        {
            return;
        }

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

        _status.Text = said is null ? view.Summary : $"{said}  {view.Summary}";
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
        var result = await _cli.RunAsync(CliArgs.For(_configDir, "config", "check", "--json"))
            .ConfigureAwait(true);

        if (IsDisposed)
        {
            return;
        }

        var view = ConfigCheckProjection.From(result, Core.ExitCode.ConfigInvalid);

        LrDialog.Show(
            this,
            view.Tone switch
            {
                CheckTone.Clean => DialogKind.Info,
                CheckTone.Warning => DialogKind.Warning,
                _ => DialogKind.Error,
            },
            "Configuration",
            view.Message,
            view.Details);
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
                // Disposed rather than discarded: the Process object is a handle, and opening
                // a folder through the shell may or may not hand one back.
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(root)
                {
                    UseShellExecute = true,
                })?.Dispose();
            }
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // Opening a folder is a convenience and failing to work out which one is not worth a
            // dialog. Caught at all only so that a missing field cannot end the process from an
            // async void handler.
        }
    }

    /// <summary>The job the grid has selected, or null when it has none.</summary>
    private string? Selected() =>
        _grid.SelectedRows.Count > 0
            ? _grid.SelectedRows[0].Cells["name"].Value as string
            : null;

    /// <summary>Opens the selected job, or says that there is not one.</summary>
    /// <remarks>
    /// Edit with nothing selected used to open "New job", because the editor takes a null name
    /// to mean a job that does not exist yet. That is the right meaning for New and the wrong
    /// answer to Edit, which the other two buttons that need a row already give.
    /// </remarks>
    private Task EditSelectedAsync()
    {
        if (Selected() is not { } job)
        {
            _status.Text = "Select a job first.";
            _status.ForeColor = Theme.Current.Muted;
            return Task.CompletedTask;
        }

        return EditAsync(job);
    }

    /// <summary>Opens the editor, and reloads only if it wrote something.</summary>
    /// <remarks>
    /// A null name is a new job. Reloading unconditionally would be harmless but slower and, on a
    /// cancelled edit, would look to somebody watching like their cancel did something. What was
    /// written is said under the grid, because the editor that said it has just closed.
    /// </remarks>
    private async Task EditAsync(string? job)
    {
        var said = await JobEditor.ShowAsync(this, _cli, _configDir, job).ConfigureAwait(true);

        if (said is not null && !IsDisposed)
        {
            await LoadAsync(said).ConfigureAwait(true);
        }
    }

    /// <summary>Switches the selected job off, or back on.</summary>
    /// <remarks>
    /// One button rather than two, because the grid already says which state a job is in and a
    /// pair of buttons of which one is always wrong for the selected row is a pair somebody has
    /// to read twice.
    /// </remarks>
    private async Task ToggleAsync()
    {
        if (Selected() is not { } job)
        {
            _status.Text = "Select a job first.";
            _status.ForeColor = Theme.Current.Muted;
            return;
        }

        var on = _grid.SelectedRows[0].Cells["state"].Value as string == "disabled";

        var result = await _cli.RunElevatedAsync(
            CliArgs.For(_configDir, "job", on ? "enable" : "disable", job)).ConfigureAwait(true);

        if (IsDisposed)
        {
            return;
        }

        await AfterWriteAsync(result, on ? "Enable" : "Disable").ConfigureAwait(true);
    }

    /// <summary>Deletes the selected job, having said what that costs.</summary>
    /// <remarks>
    /// Confirmed here rather than by the CLI, which is not interactive. The wording names
    /// Disable, because the file is about to be deleted and disabling is the reversible thing the
    /// operator may have meant.
    /// </remarks>
    private async Task RemoveAsync()
    {
        if (Selected() is not { } job)
        {
            _status.Text = "Select a job first.";
            _status.ForeColor = Theme.Current.Muted;
            return;
        }

        // The wording names Disable, because the file is about to be deleted and disabling is
        // the reversible thing the operator may have meant. needsAdmin puts the shield on Yes,
        // so the UAC prompt that follows is not a surprise.
        if (!LrDialog.Confirm(this, "Remove job",
                $"Delete '{job}' and its file? This cannot be undone.\n\n"
                + "Disable keeps the file and can be reversed exactly.",
                needsAdmin: true))
        {
            return;
        }

        var result = await _cli.RunElevatedAsync(CliArgs.For(_configDir, "job", "remove", job))
            .ConfigureAwait(true);

        if (IsDisposed)
        {
            return;
        }

        await AfterWriteAsync(result, "Remove").ConfigureAwait(true);
    }

    /// <summary>What every elevated write here does with its answer.</summary>
    /// <remarks>
    /// <para>
    /// One place, because the three of them differ only in the word in the title - and a guard
    /// copied three times is a guard that will be copied a fourth.
    /// </para>
    /// <para>
    /// Read with <see cref="JobEditProjection"/>. These verbs answer with a <c>JobEditResult</c>,
    /// or with no payload at all when they refuse, and the check projection this used to call
    /// reported both as an unreadable response - so a refusal to remove a job that is not there
    /// said nothing about which jobs are.
    /// </para>
    /// </remarks>
    private async Task AfterWriteAsync(CliResult result, string what)
    {
        if (result.Failure == CliFailure.UacDeclined)
        {
            _status.Text = "Elevation was cancelled. Nothing was written.";
            _status.ForeColor = Theme.Current.Muted;
            return;
        }

        if (result.IsDefect)
        {
            LrDialog.Error(this, what, result.Describe(), result.Details);
            return;
        }

        var view = JobEditProjection.From(result);

        if (!result.Ok)
        {
            LrDialog.Show(this, DialogKind.Error, what, view.Message, view.Details);
            return;
        }

        await LoadAsync(view.Message).ConfigureAwait(true);
    }
}
