using WinLogRotate.Core.Configuration;
using WinLogRotate.Gui.Cli;
using WinLogRotate.Gui.Ui;

namespace WinLogRotate.Gui.Pages;

/// <summary>
/// The form that adds and changes a job, which for twenty-nine milestones did not exist.
/// </summary>
/// <remarks>
/// <para>
/// <c>ConfigLoader</c> told operators to "use the GUI to add one" from the first release, and the
/// Jobs page offered Refresh, Check configuration, and Open folder - which launches Explorer so
/// you can go and write the TOML yourself. This is the capability that message was promising.
/// </para>
/// <para>
/// Tiered rather than one long list. The dozen keys people actually change are on the form; the
/// rest are a grid of key, value, and whether the job sets it here or inherits it. A grid rather
/// than thirty-three more controls because it can show that third state, which separate controls
/// cannot, and because the grid <b>is</b> the key list - so a key added to
/// <see cref="JobSchema"/> appears here without anybody remembering to add it.
/// </para>
/// <para>
/// Under <c>Pages/</c> and not <c>Ui/</c> deliberately, even though it is a dialog: the
/// architecture rules that say a window running the CLI must report a defect, and must catch
/// every way an envelope can fail, are scoped to the GUI project - and a dialog is the easiest
/// place to forget both.
/// </para>
/// <para>
/// Every decision worth testing is in <see cref="JobEditorModel"/>, which is in a project the
/// Linux leg can build. What is left here is layout and event wiring.
/// </para>
/// </remarks>
public sealed class JobEditor : Form
{
    /// <summary>The keys the form itself carries, in the order it shows them.</summary>
    /// <remarks>
    /// Chosen rather than derived: these are the ones somebody opens the editor to change. The
    /// grid below holds everything else, so nothing is unreachable either way.
    /// </remarks>
    private static readonly string[] Common =
    [
        "schedule", "rotate", "maxage", "maxsize", "compress", "compresstype", "olddir", "missingok",
    ];

    private readonly CliRunner _cli;
    private readonly string? _configDir;
    private readonly bool _isNew;
    private readonly JobEditorView _original;
    private readonly Dictionary<string, Control> _editors = new(StringComparer.OrdinalIgnoreCase);

    private readonly TextBox _name = new() { Bounds = new Rectangle(120, 16, 260, 24) };
    private readonly TextBox _paths = new()
    {
        Bounds = new Rectangle(120, 48, 420, 64),
        Multiline = true,
        ScrollBars = ScrollBars.Vertical,
        AcceptsReturn = true,
    };

    private readonly ComboBox _kind = new()
    {
        Bounds = new Rectangle(120, 120, 160, 24),
        DropDownStyle = ComboBoxStyle.DropDownList,
    };

    private readonly CheckBox _enabled = new()
    {
        Text = "Enabled",
        Bounds = new Rectangle(300, 120, 100, 24),
        Checked = true,
    };

    private readonly DataGridView _advanced = new()
    {
        Bounds = new Rectangle(16, 176, 524, 200),
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.CellSelect,
        MultiSelect = false,
    };

    private readonly Label _status = new()
    {
        Bounds = new Rectangle(16, 384, 524, 36),
        ForeColor = Theme.Current.Muted,
    };

    private JobEditor(CliRunner cli, string? configDir, JobEditorView view, bool isNew)
    {
        _cli = cli;
        _configDir = configDir;
        _original = view;
        _isNew = isNew;

        Text = isNew ? "New job" : $"Job: {view.Job}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(556, 470);
        BackColor = Theme.Current.Window;
        ForeColor = Theme.Current.Text;

        Build();
        Fill();
    }

    /// <summary>
    /// Opens the editor for a job, or for one that does not exist yet.
    /// </summary>
    /// <remarks>
    /// Returns true when something was written, which is the only thing the caller needs: it
    /// refreshes on true and leaves its grid alone on false, so a cancelled edit does not look
    /// like a change.
    /// </remarks>
    public static async Task<bool> ShowAsync(
        IWin32Window owner, CliRunner cli, string? configDir, string? job)
    {
        var view = JobEditorModel.Blank();

        if (job is not null)
        {
            var read = await cli.RunAsync(CliArgs.For(configDir, "job", "show", job, "--json"))
                .ConfigureAwait(true);

            // Exit 2 is a job whose file will not load, which is the commonest reason to open
            // this window at all - so it is read, not refused. Only a defect stops us.
            if (read.IsDefect)
            {
                LrDialog.Error(owner, "Job", read.Describe(), read.Details);
                return false;
            }

            view = JobEditorModel.From(read.StdOut);

            if (view.Unreadable)
            {
                LrDialog.Error(owner, "Unexpected response",
                    "winlogrotate.exe returned something this window could not read. "
                    + "This usually means the two are different versions.", read.StdOut);
                return false;
            }
        }

        using var form = new JobEditor(cli, configDir, view, job is null);
        return form.ShowDialog(owner) == DialogResult.OK;
    }

    private void Build()
    {
        var colors = Theme.Current;

        Label Caption(string text, int y) => new()
        {
            Text = text,
            Bounds = new Rectangle(16, y + 4, 100, 18),
            ForeColor = colors.Muted,
        };

        Controls.Add(Caption("Name", 16));
        Controls.Add(_name);
        Controls.Add(Caption("Paths", 48));
        Controls.Add(_paths);
        Controls.Add(Caption("Kind", 120));
        Controls.Add(_kind);
        Controls.Add(_enabled);

        // One line per pattern, said where somebody will read it rather than in the manual.
        Controls.Add(new Label
        {
            Text = "One glob per line.",
            Bounds = new Rectangle(390, 52, 150, 18),
            ForeColor = colors.Muted,
        });

        var y = 152;

        foreach (var key in Common)
        {
            if (Field(key) is not { } row)
            {
                continue;
            }

            Controls.Add(Caption(key, y));
            Controls.Add(Editor(row, y));
            y += 30;
        }

        _advanced.Bounds = new Rectangle(16, y + 8, 524, 470 - y - 130);
        _advanced.Columns.Add("key", "Key");
        _advanced.Columns.Add("value", "Value");

        var state = new DataGridViewTextBoxColumn
        {
            Name = "state",
            HeaderText = "Source",
            ReadOnly = true,
        };

        _advanced.Columns.Add(state);
        _advanced.Columns["key"]!.ReadOnly = true;
        _advanced.CellEndEdit += (_, _) => Restate();
        Controls.Add(_advanced);

        _status.Bounds = new Rectangle(16, _advanced.Bottom + 8, 524, 34);
        Controls.Add(_status);

        var check = new Button
        {
            Text = "Check",
            Bounds = new Rectangle(280, ClientSize.Height - 42, 84, 26),
            FlatStyle = FlatStyle.System,
        };

        var save = new Button
        {
            Text = "Save",
            Bounds = new Rectangle(372, ClientSize.Height - 42, 84, 26),
            FlatStyle = FlatStyle.System,
        };

        var cancel = new Button
        {
            Text = "Cancel",
            Bounds = new Rectangle(464, ClientSize.Height - 42, 84, 26),
            FlatStyle = FlatStyle.System,
            DialogResult = DialogResult.Cancel,
        };

        // The shield goes on Save and not on Check: Check runs --dry-run, which writes nothing
        // and needs no rights at all. A shield on it would promise a prompt that never comes,
        // and train somebody to expect one where it matters less.
        LrDialog.AddShield(save);

        check.Click += async (_, _) => await CheckAsync().ConfigureAwait(true);
        save.Click += async (_, _) => await SaveAsync().ConfigureAwait(true);

        Controls.Add(check);
        Controls.Add(save);
        Controls.Add(cancel);
        CancelButton = cancel;
    }

    private Control Editor(JobField row, int y)
    {
        Control control;

        if (row.Kind == JobKeyKind.Enum && row.Choices.Count > 0)
        {
            var combo = new ComboBox
            {
                Bounds = new Rectangle(120, y, 200, 24),
                DropDownStyle = ComboBoxStyle.DropDownList,
            };

            // The empty entry is not decoration: it is how the form says "inherit again", and
            // without it an enum is the one kind of field somebody could set and never clear.
            combo.Items.Add(string.Empty);
            foreach (var choice in row.Choices)
            {
                combo.Items.Add(choice);
            }

            control = combo;
        }
        else if (row.Kind == JobKeyKind.Flag)
        {
            var combo = new ComboBox
            {
                Bounds = new Rectangle(120, y, 200, 24),
                DropDownStyle = ComboBoxStyle.DropDownList,
            };

            // Three states, not a checkbox. A checkbox has two, and a job key has three: true,
            // false, and inherited - which is the distinction the whole editor turns on.
            combo.Items.AddRange([string.Empty, "true", "false"]);
            control = combo;
        }
        else
        {
            control = new TextBox
            {
                Bounds = new Rectangle(120, y, 200, 24),
                PlaceholderText = row.Sample.Trim('"'),
            };
        }

        _editors[row.Key] = control;
        return control;
    }

    private JobField? Field(string key) =>
        _original.Fields.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));

    private void Fill()
    {
        _name.Text = Field("name")?.Value ?? string.Empty;

        // The name is the job's identity, so it is read-only on an existing job: renaming means
        // a new file and a journal that no longer matches, which is an add and a remove.
        _name.ReadOnly = !_isNew;

        _paths.Text = Field("paths")?.Value ?? string.Empty;

        _kind.Items.Clear();
        foreach (var choice in Field("kind")?.Choices ?? [])
        {
            _kind.Items.Add(choice);
        }

        _kind.SelectedItem = Field("kind")?.Value ?? "rotate";
        _enabled.Checked = !string.Equals(Field("enabled")?.Value, "false", StringComparison.OrdinalIgnoreCase);

        foreach (var (key, control) in _editors)
        {
            Set(control, Field(key)?.Value ?? string.Empty);
        }

        _advanced.Rows.Clear();

        foreach (var field in _original.Fields)
        {
            if (Common.Contains(field.Key, StringComparer.OrdinalIgnoreCase)
                || JobEditorModel.Structural.Contains(field.Key, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var row = _advanced.Rows[_advanced.Rows.Add(field.Key, field.Value ?? string.Empty, State(field))];

            if (!field.Known)
            {
                // This build has no row for it, so it can be cleared and not written. Saying so
                // in the cell is the only warning an editor gets before typing into it.
                row.Cells["state"].Value = "not a setting this product reads";
                row.DefaultCellStyle.ForeColor = Theme.Current.Warning;
            }
        }

        Restate();

        if (_original.Problems.Count > 0)
        {
            _status.Text = string.Join("  ", _original.Problems);
            _status.ForeColor = Theme.Current.Warning;
        }
    }

    /// <summary>Whether a value is the job's own or comes from somewhere above it.</summary>
    private static string State(JobField field) =>
        field.IsSet ? $"set here (line {field.Line})" : "inherited";

    /// <summary>
    /// Re-labels the Source column after an edit.
    /// </summary>
    /// <remarks>
    /// Because the label is the point of the column: somebody who types into an inherited row has
    /// just decided to stop inheriting that key, and should see that before they press Save
    /// rather than afterwards.
    /// </remarks>
    private void Restate()
    {
        foreach (DataGridViewRow row in _advanced.Rows)
        {
            var key = row.Cells["key"].Value as string ?? string.Empty;
            var field = Field(key);

            if (field is null || !field.Known)
            {
                continue;
            }

            var now = row.Cells["value"].Value as string ?? string.Empty;
            var was = field.Value ?? string.Empty;

            row.Cells["state"].Value = string.Equals(now.Trim(), was, StringComparison.Ordinal)
                ? State(field)
                : now.Trim().Length == 0 ? "will inherit again" : "will be set here";
        }
    }

    /// <summary>What the form currently says, as the model wants it.</summary>
    private Dictionary<string, string?> Edited()
    {
        var edited = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = _name.Text.Trim(),
            ["paths"] = _paths.Text,
            ["kind"] = _kind.SelectedItem as string,

            // Absent means enabled, so the box being ticked says nothing rather than "true".
            // Writing enabled = true would put a second spelling of the default in the file.
            ["enabled"] = _enabled.Checked ? null : "false",
        };

        foreach (var (key, control) in _editors)
        {
            edited[key] = Read(control);
        }

        foreach (DataGridViewRow row in _advanced.Rows)
        {
            if (row.Cells["key"].Value is string key)
            {
                edited[key] = (row.Cells["value"].Value as string)?.Trim();
            }
        }

        return edited;
    }

    private IReadOnlyList<string> Args(bool dryRun)
    {
        var job = _isNew ? _name.Text.Trim() : _original.Job;

        return dryRun
            ? JobEditorModel.ValidateArgs(_configDir, job, _isNew, _original, Edited())
            : JobEditorModel.SaveArgs(_configDir, job, _isNew, _original, Edited());
    }

    private async Task CheckAsync()
    {
        if (Refused() is { } refused)
        {
            Say(refused, Theme.Current.Danger);
            return;
        }

        var result = await _cli.RunAsync(Args(dryRun: true)).ConfigureAwait(true);

        if (result.IsDefect)
        {
            LrDialog.Error(this, "Check", result.Describe(), result.Details);
            return;
        }

        Report(result, checking: true);
    }

    private async Task SaveAsync()
    {
        if (Refused() is { } refused)
        {
            Say(refused, Theme.Current.Danger);
            return;
        }

        // Checked before it is elevated, and through the same arguments plus one flag. A form
        // that validated one way and saved another would raise a UAC prompt for a change that
        // was never going to work.
        var dry = await _cli.RunAsync(Args(dryRun: true)).ConfigureAwait(true);

        if (dry.IsDefect)
        {
            LrDialog.Error(this, "Save", dry.Describe(), dry.Details);
            return;
        }

        if (!dry.Ok)
        {
            Report(dry, checking: false);
            return;
        }

        if (!JobEditorModel.Changes(Args(dryRun: false)))
        {
            Say("Nothing to change.", Theme.Current.Muted);
            return;
        }

        var result = await _cli.RunElevatedAsync(Args(dryRun: false)).ConfigureAwait(true);

        if (result.Failure == CliFailure.UacDeclined)
        {
            Say("Elevation was cancelled. Nothing was written.", Theme.Current.Muted);
            return;
        }

        if (result.IsDefect)
        {
            LrDialog.Error(this, "Save", result.Describe(), result.Details);
            return;
        }

        if (!result.Ok)
        {
            Report(result, checking: false);
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>What the form can refuse without asking the CLI.</summary>
    /// <remarks>
    /// Only the two things that would otherwise produce a command line with a missing argument.
    /// Everything else is the CLI's to judge, because a second opinion about what a valid job is
    /// is the thing this whole design exists to avoid having.
    /// </remarks>
    private string? Refused()
    {
        if (_isNew && _name.Text.Trim().Length == 0)
        {
            return "Give the job a name first. It appears in the journal and in every diagnostic.";
        }

        return _isNew && _paths.Text.Trim().Length == 0
            ? "A new job needs at least one path to rotate."
            : null;
    }

    private void Report(CliResult result, bool checking)
    {
        var view = ConfigCheckProjection.From(result, Core.ExitCode.ConfigInvalid);

        if (result.Ok && checking)
        {
            Say(JobEditorModel.Changes(Args(dryRun: false))
                ? "This would be accepted. Nothing has been written."
                : "This is already what the file says. Nothing to change.",
                Theme.Current.Muted);

            return;
        }

        Say(view.Message, Theme.Current.Danger);

        if (view.Details is { Length: > 0 })
        {
            LrDialog.Show(this, DialogKind.Error, checking ? "Check" : "Save", view.Message, view.Details);
        }
    }

    private void Say(string text, Color colour)
    {
        _status.Text = text;
        _status.ForeColor = colour;
    }

    private static void Set(Control control, string value)
    {
        if (control is ComboBox combo)
        {
            combo.SelectedItem = value;
            return;
        }

        control.Text = value;
    }

    private static string? Read(Control control) =>
        control is ComboBox combo ? combo.SelectedItem as string : control.Text;
}
