using WinLogRotate.Core.Configuration;
using WinLogRotate.Gui.Cli;
using WinLogRotate.Gui.Ui;

namespace WinLogRotate.Gui.Pages;

/// <summary>
/// The form that adds and changes a job.
/// </summary>
/// <remarks>
/// <para>
/// Two views of one job. <b>Basics</b> asks what somebody adding a log file has to decide -
/// which files, how the log is taken away from the program writing it, how often, how many old
/// copies to keep and for how long, how they are named and compressed, and where they go - in
/// their own words, and restates the answer as one sentence. <b>Advanced</b> is every key the schema knows, each a typed
/// control with the schema's caption, its default, where its value comes from, and one line
/// saying what it does; there is no grid of raw keys, and nothing is called by its TOML name
/// alone. The header - the name and the files - is shared, with a Browse button that turns a
/// picked file into the line that names its series, and a preview of what that line matches.
/// </para>
/// <para>
/// Under <c>Pages/</c> and not <c>Ui/</c> deliberately, even though it is a dialog: the
/// architecture rules that say a window running the CLI must report a defect, and must catch
/// every way an envelope can fail, are scoped to the GUI project - and a dialog is the easiest
/// place to forget both.
/// </para>
/// <para>
/// Every decision worth testing is in <see cref="JobEditorModel"/>, <see cref="EditorLayout"/>,
/// <see cref="GlobPreviewProjection"/>, <see cref="PatternSuggestion"/> and
/// <see cref="JobSummary"/>, all in a project the Linux leg can build. What is left here is
/// event wiring.
/// </para>
/// </remarks>
public sealed class JobEditor : Form
{
    private const int PreviewLines = 8;

    private readonly CliRunner _cli;
    private readonly string? _configDir;
    private readonly bool _isNew;
    private readonly JobEditorView _original;
    private readonly string? _hookWarning;
    private readonly EditorLayout _layout;

    /// <summary>What the verb said when it wrote, for the Jobs page to show.</summary>
    private string? _said;

    private bool _busy;
    private bool _showingAdvanced;
    private bool _filling;
    private int _previewSerial;
    private string _lastSuggestedName = string.Empty;

    // ---- header ---------------------------------------------------------------------------

    private readonly TextBox _name = new() { PlaceholderText = JobEditorText.NamePlaceholder };
    private readonly CheckBox _enabled = new() { Text = "Enabled", Checked = true };
    private readonly TextBox _files = new()
    {
        Multiline = true,
        ScrollBars = ScrollBars.Vertical,
        AcceptsReturn = true,
    };

    private readonly Button _browse = new() { Text = JobEditorText.Browse, FlatStyle = FlatStyle.System };
    private readonly Label _preview = new() { ForeColor = Theme.Current.Muted, AutoEllipsis = true };

    // ---- the Basics body --------------------------------------------------------------------

    private readonly List<Control> _basics = [];
    private readonly RadioButton[] _how =
    [
        new() { Text = JobEditorText.HowAuto, Checked = true },
        new() { Text = JobEditorText.HowCopyTruncate },
        new() { Text = JobEditorText.HowRename },
        new() { Text = JobEditorText.HowManage },
    ];

    private readonly Label _howHint = new() { ForeColor = Theme.Current.Muted, AutoEllipsis = true };
    private readonly ComboBox _schedule = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _earlyLead = new() { Text = JobEditorText.EarlyLead, ForeColor = Theme.Current.Muted };
    private readonly TextBox _maxSize = new() { PlaceholderText = "100M" };
    private readonly Label _whenHint = new() { Text = JobEditorText.WhenHint, ForeColor = Theme.Current.Muted, AutoEllipsis = true };
    private readonly TextBox _rotate = new() { PlaceholderText = "7" };
    private readonly Label _keepLead = new() { Text = JobEditorText.KeepLead, ForeColor = Theme.Current.Muted };
    private readonly TextBox _maxAge = new() { PlaceholderText = "never" };
    private readonly Label _keepUnit = new() { Text = JobEditorText.KeepUnit, ForeColor = Theme.Current.Muted };
    private readonly ComboBox _names = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _compression = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _oldDir = new() { PlaceholderText = JobEditorText.OldDirPlaceholder };
    private readonly Button _browseFolder = new() { Text = JobEditorText.Browse, FlatStyle = FlatStyle.System };
    private readonly CheckBox _createOldDir = new() { Text = JobEditorText.CreateOldDir };
    private readonly Label _summary = new() { ForeColor = Theme.Current.Muted };

    // ---- the Advanced body ------------------------------------------------------------------

    private readonly Panel _advanced = new() { AutoScroll = true, BackColor = Theme.Current.Window };
    private readonly Panel _content = new() { BackColor = Theme.Current.Window };
    private readonly Dictionary<string, Row> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ForeignKey> _foreign = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _problems = new(StringComparer.OrdinalIgnoreCase);
    private readonly ToolTip _tips = new();

    // ---- under both -------------------------------------------------------------------------

    private readonly Label _status = new() { ForeColor = Theme.Current.Muted };
    private readonly LinkLabel _toggle = new();
    private readonly Button _whatWouldHappen = new() { Text = JobEditorText.PreviewButton, FlatStyle = FlatStyle.System };
    private readonly Button _check = new() { Text = "Check", FlatStyle = FlatStyle.System };
    private readonly Button _save = new() { Text = "Save", FlatStyle = FlatStyle.System };

    /// <summary>One Advanced row's controls, and whether a tick box was touched.</summary>
    private sealed class Row
    {
        public required JobField Field { get; init; }
        public required FieldPresentation Shown { get; init; }
        public required Control Editor { get; init; }
        public required Label Caption { get; init; }
        public required Label Source { get; init; }

        /// <summary>For a tick box: whether the person set it, as opposed to it showing the inherited value.</summary>
        public bool Explicit { get; set; }
    }

    private sealed class ForeignKey
    {
        public required Label Text { get; init; }
        public required LinkLabel Remove { get; init; }
        public bool Removed { get; set; }
    }

    private JobEditor(
        CliRunner cli, string? configDir, JobEditorView view, bool isNew, string? hookWarning)
    {
        _cli = cli;
        _configDir = configDir;
        _original = view;
        _isNew = isNew;
        _hookWarning = hookWarning;
        _layout = EditorLayout.Compute(
            JobEditorModel.Sections(view),
            [.. JobEditorModel.Foreign(view).Select(f => f.Key)]);

        Text = isNew ? "New job" : $"Job: {view.Job}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        BackColor = Theme.Current.Window;
        ForeColor = Theme.Current.Text;

        // Scaled with the monitor, in the order MainForm explains: layout suspended, the mode
        // and the dimensions, the controls, and then the one scale. The layout's numbers are
        // logical pixels at 100 per cent, and PerformAutoScale is what turns them into the
        // monitor's. Both bodies are built now, so the toggle never places a control after
        // the scale.
        SuspendLayout();
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);

        Build();
        Fill();

        ResumeLayout(false);
        PerformAutoScale();

        // After the controls exist, because a dialog is built long after the main window was
        // themed.
        Theme.Apply(this);

        Shown += async (_, _) => await PreviewAsync().ConfigureAwait(true);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tips.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Opens the editor and returns what the verb said when it wrote, or null when nothing was.
    /// </summary>
    public static async Task<string?> ShowAsync(
        IWin32Window owner, CliRunner cli, string? configDir, string? job)
    {
        var view = JobEditorModel.Blank();

        if (job is not null)
        {
            var read = await cli.RunAsync(CliArgs.For(configDir, "job", "show", job, "--json"))
                .ConfigureAwait(true);

            // The page that asked has been navigated away from. A dialog owned by a disposed
            // control is itself an exception, and there is nobody looking.
            if (owner is Control { IsDisposed: true })
            {
                return null;
            }

            // Exit 2 is a job whose file will not load, which is the commonest reason to open
            // this window at all - so it is read, not refused. Only a defect stops us.
            if (read.IsDefect)
            {
                LrDialog.Error(owner, "Job", read.Describe(), read.Details);
                return null;
            }

            view = JobEditorModel.From(read.StdOut);

            if (view.Refusal is { } refusal)
            {
                LrDialog.Error(owner, "Job", refusal, read.Details);
                return null;
            }

            if (view.Unreadable)
            {
                LrDialog.Error(owner, "Unexpected response",
                    "winlogrotate.exe returned something this window could not read. "
                    + "This usually means the two are different versions.", read.StdOut);
                return null;
            }
        }

        // Asked once, when the window opens. The hook gate judges the owner of every file it
        // would execute from, and on a per-user installation that owner is the installing user
        // by design - so hooks are refused for most people who open this window, and a field
        // that accepted them in silence is how somebody finds out at three in the morning that
        // their service-restart hook has never fired.
        var doctor = await cli.RunAsync(CliArgs.For(configDir, "doctor", "--json"))
            .ConfigureAwait(true);

        if (owner is Control { IsDisposed: true })
        {
            return null;
        }

        var warning = doctor.IsDefect
            ? null
            : JobEditorModel.HookWarning(JobEditorModel.HooksAllowed(doctor.StdOut));

        using var form = new JobEditor(cli, configDir, view, job is null, warning);
        return form.ShowDialog(owner) == DialogResult.OK ? form._said : null;
    }

    // ---- building ---------------------------------------------------------------------------

    private void Build()
    {
        var colors = Theme.Current;
        var layout = _layout;

        ClientSize = new Size(layout.ClientWidth, layout.ClientHeight);

        // Header.
        Controls.Add(Caption("Name", layout.NameCaption));
        Place(_name, layout.Name);
        Place(_enabled, layout.Enabled);
        Controls.Add(Caption("Which files", layout.FilesCaption));
        Place(_files, layout.Files);
        Place(_browse, layout.Browse);
        Controls.Add(Caption(JobEditorText.FilesHint, layout.FilesHint));
        Place(_preview, layout.Preview);

        _browse.Click += (_, _) => BrowseForFile();
        _files.Leave += async (_, _) => await PreviewAsync().ConfigureAwait(true);
        _files.TextChanged += (_, _) => UpdateSummary();

        // Basics.
        Basic(Caption("How", layout.HowCaption));
        for (var i = 0; i < _how.Length; i++)
        {
            Basic(_how[i], layout.How[i]);
            _how[i].CheckedChanged += (_, _) => HowChanged();
        }

        Basic(_howHint, layout.HowHint);
        Basic(Caption("How often", layout.WhenCaption));
        Basic(_schedule, layout.Schedule);
        Basic(_earlyLead, layout.EarlyLead);
        Basic(_maxSize, layout.MaxSize);
        Basic(_whenHint, layout.WhenHint);
        Basic(Caption("Keep", layout.KeepCaption));
        Basic(_rotate, layout.Rotate);
        Basic(_keepLead, layout.KeepLead);
        Basic(_maxAge, layout.MaxAge);
        Basic(_keepUnit, layout.KeepUnit);
        Basic(Caption(JobEditorText.KeepHint, layout.KeepHint));
        Basic(Caption("Old copies", layout.CopiesCaption));
        Basic(_names, layout.Names);
        Basic(_compression, layout.Compression);
        Basic(Caption("Put them in", layout.FolderCaption));
        Basic(_oldDir, layout.OldDir);
        Basic(_browseFolder, layout.BrowseFolder);
        Basic(_createOldDir, layout.CreateOldDir);
        Basic(_summary, layout.Summary);

        _schedule.Items.Add(JobEditorModel.DefaultChoice(JobSchema.Find("schedule")?.Default));
        foreach (var choice in JobSchema.Find("schedule")?.Choices ?? [])
        {
            if (!JobEditorModel.SizeApplies(choice))
            {
                _schedule.Items.Add(choice);
            }
        }

        _names.Items.AddRange([JobEditorText.NamesNumbered, JobEditorText.NamesDated]);
        _compression.Items.AddRange([JobEditorText.CompressZip, JobEditorText.CompressGzip, JobEditorText.CompressNone]);

        _schedule.SelectedIndexChanged += (_, _) => ScheduleChanged();
        _maxSize.TextChanged += (_, _) => UpdateSummary();
        _rotate.TextChanged += (_, _) => UpdateSummary();
        _maxAge.TextChanged += (_, _) => UpdateSummary();
        _names.SelectedIndexChanged += (_, _) => UpdateSummary();
        _compression.SelectedIndexChanged += (_, _) => UpdateSummary();
        _oldDir.TextChanged += (_, _) => UpdateSummary();
        _browseFolder.Click += (_, _) => BrowseForFolder();
        JudgeOnLeave(_maxSize, "maxsize");
        JudgeOnLeave(_rotate, "rotate");
        JudgeOnLeave(_maxAge, "maxage");

        // Advanced: a scrolling panel showing a content panel the layout sized.
        _advanced.Bounds = layout.Viewport.ToRectangle();
        _content.Bounds = new Box(0, 0, layout.ContentWidth, layout.ContentHeight).ToRectangle();
        _advanced.Controls.Add(_content);
        Controls.Add(_advanced);

        foreach (var section in layout.Sections)
        {
            _content.Controls.Add(new Label
            {
                Text = section.Title,
                Bounds = section.Header.ToRectangle(),
                ForeColor = colors.Accent,
            });

            foreach (var row in section.Rows)
            {
                BuildRow(row);
            }
        }

        if (layout.ForeignHeader is { } foreignHeader)
        {
            _content.Controls.Add(new Label
            {
                Text = JobEditorText.ForeignSection,
                Bounds = foreignHeader.ToRectangle(),
                ForeColor = colors.Accent,
            });
        }

        foreach (var row in layout.Foreign)
        {
            BuildForeignRow(row);
        }

        // Under both.
        Place(_status, layout.Status);
        Controls.Add(Caption(JobEditorText.ButtonsHint, layout.ButtonsHint));
        Place(_toggle, layout.Toggle);
        Place(_whatWouldHappen, layout.WhatWouldHappen);
        Place(_check, layout.Check);
        Place(_save, layout.Save);

        var cancel = new Button
        {
            Text = "Cancel",
            Bounds = layout.Cancel.ToRectangle(),
            FlatStyle = FlatStyle.System,
            DialogResult = DialogResult.Cancel,
        };

        Controls.Add(cancel);
        CancelButton = cancel;

        // The shield goes on Save and not on Check: Check runs --dry-run, which writes nothing
        // and needs no rights at all. A shield on it would promise a prompt that never comes.
        LrDialog.AddShield(_save);

        _toggle.LinkClicked += (_, _) => ShowAdvanced(!_showingAdvanced);
        _whatWouldHappen.Click += async (_, _) => await OneAtATimeAsync(PreviewRunAsync).ConfigureAwait(true);
        _check.Click += async (_, _) => await OneAtATimeAsync(CheckAsync).ConfigureAwait(true);
        _save.Click += async (_, _) => await OneAtATimeAsync(SaveAsync).ConfigureAwait(true);
    }

    private Label Caption(string text, Box box) => new()
    {
        Text = text,
        Bounds = box.ToRectangle(),
        ForeColor = Theme.Current.Muted,
        AutoEllipsis = true,
    };

    private void Place(Control control, Box box)
    {
        control.Bounds = box.ToRectangle();
        Controls.Add(control);
    }

    private void Basic(Control control, Box box)
    {
        Place(control, box);
        _basics.Add(control);
    }

    private void Basic(Control control)
    {
        Controls.Add(control);
        _basics.Add(control);
    }

    private void BuildRow(AdvancedRow row)
    {
        var field = _original.Fields.First(f => string.Equals(f.Key, row.Key, StringComparison.OrdinalIgnoreCase));
        var shown = JobEditorModel.Presentation(field);
        var colors = Theme.Current;

        var caption = new Label { Text = shown.Caption, Bounds = row.Caption.ToRectangle(), AutoEllipsis = true };
        var key = new Label { Text = shown.Key, Bounds = row.KeyLabel.ToRectangle(), ForeColor = colors.Muted };
        var source = new Label { Bounds = row.Source.ToRectangle(), ForeColor = colors.Muted, AutoEllipsis = true };
        var description = new Label
        {
            Text = shown.Description,
            Bounds = row.Description.ToRectangle(),
            ForeColor = colors.Muted,
            AutoEllipsis = true,
        };
        _tips.SetToolTip(description, shown.Description);

        Control editor = shown.Editor switch
        {
            FieldEditor.Check => new CheckBox { Bounds = row.Editor.ToRectangle() },
            FieldEditor.Choice => Choice(row.Editor, shown.Choices),
            FieldEditor.Lines => new TextBox
            {
                Bounds = row.Editor.ToRectangle(),
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                AcceptsReturn = true,
                PlaceholderText = shown.Placeholder,
            },
            _ => new TextBox { Bounds = row.Editor.ToRectangle(), PlaceholderText = shown.Placeholder },
        };

        var inherit = new LinkLabel { Text = JobEditorText.InheritLink, Bounds = row.Inherit.ToRectangle() };

        var made = new Row { Field = field, Shown = shown, Editor = editor, Caption = caption, Source = source };
        _rows[field.Key] = made;

        switch (editor)
        {
            case CheckBox box:
                // Click, not CheckedChanged: only the person's own click makes the row explicit.
                box.Click += (_, _) => { made.Explicit = true; Relabel(made); UpdateSummary(); };
                break;
            case ComboBox combo:
                combo.SelectedIndexChanged += (_, _) => { Relabel(made); ScheduleChanged(); };
                break;
            default:
                editor.TextChanged += (_, _) => { Relabel(made); UpdateSummary(); };
                editor.Leave += (_, _) => JudgeRow(made);
                break;
        }

        inherit.LinkClicked += (_, _) => Inherit(made);

        _content.Controls.Add(caption);
        _content.Controls.Add(key);
        _content.Controls.Add(editor);
        if (row.Unit is { } unitBox && shown.Unit is { } unit)
        {
            _content.Controls.Add(new Label { Text = unit, Bounds = unitBox.ToRectangle(), ForeColor = colors.Muted });
        }

        _content.Controls.Add(source);
        _content.Controls.Add(inherit);
        _content.Controls.Add(description);
    }

    private static ComboBox Choice(Box box, IReadOnlyList<string> choices)
    {
        var combo = new ComboBox { Bounds = box.ToRectangle(), DropDownStyle = ComboBoxStyle.DropDownList };
        combo.Items.Add(JobEditorModel.InheritChoice);
        foreach (var choice in choices)
        {
            combo.Items.Add(choice);
        }

        return combo;
    }

    private void BuildForeignRow(ForeignRow row)
    {
        var field = _original.Fields.First(f => string.Equals(f.Key, row.Key, StringComparison.OrdinalIgnoreCase));

        var text = new Label
        {
            Text = $"{field.Key} = {field.Value}   (line {field.Line}, not a setting this product reads)",
            Bounds = row.Text.ToRectangle(),
            ForeColor = Theme.Current.Warning,
            AutoEllipsis = true,
        };
        var remove = new LinkLabel { Text = JobEditorText.RemoveLink, Bounds = row.Remove.ToRectangle() };
        var made = new ForeignKey { Text = text, Remove = remove };
        _foreign[field.Key] = made;

        remove.LinkClicked += (_, _) =>
        {
            made.Removed = true;
            text.ForeColor = Theme.Current.Danger;
            text.Text = $"{field.Key} will be removed";
            remove.Enabled = false;
        };

        _content.Controls.Add(text);
        _content.Controls.Add(remove);
    }

    // ---- filling ----------------------------------------------------------------------------

    private JobField? Field(string key) =>
        _original.Fields.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));

    private void Fill()
    {
        _filling = true;

        _name.Text = Field("name")?.Value ?? string.Empty;
        _name.ReadOnly = !_isNew;
        _files.Text = Field("paths")?.Value ?? string.Empty;
        _enabled.Checked = !string.Equals(Field("enabled")?.Value, "false", StringComparison.OrdinalIgnoreCase);

        // Basics, from the file.
        var how = JobEditorModel.HowRotatedShown(_original);
        if (_isNew)
        {
            how = HowRotated.Auto;
        }

        foreach (var radio in _how)
        {
            radio.Enabled = how is not null;
        }

        if (how is { } answer)
        {
            _how[(int)answer].Checked = true;
        }

        var schedule = Field("schedule")?.Value;
        if (JobEditorModel.SizeApplies(schedule))
        {
            _schedule.SelectedIndex = 0;
        }
        else
        {
            Select(_schedule, schedule ?? JobEditorModel.DefaultChoice(JobSchema.Find("schedule")?.Default));
        }

        _maxSize.Text = Field("maxsize")?.Value ?? string.Empty;
        _rotate.Text = Field("rotate")?.Value ?? string.Empty;
        _maxAge.Text = Field("maxage")?.Value ?? string.Empty;
        _names.SelectedIndex = string.Equals(Field("dateext")?.Value, "true", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        _compression.SelectedIndex = (int)JobEditorModel.CompressionShown(Field("compress")?.Value, Field("compresstype")?.Value);
        _oldDir.Text = Field("olddir")?.Value ?? string.Empty;
        _createOldDir.Checked = string.Equals(Field("createolddir")?.Value, "true", StringComparison.OrdinalIgnoreCase);

        _whatWouldHappen.Enabled = !_isNew;
        _tips.SetToolTip(_whatWouldHappen, _isNew
            ? "Save the job first; the preview runs the job as it is on disk."
            : "Runs a dry run of this job, as if it were due tonight, and lists what it would do. Nothing is changed.");

        // Advanced, from the file.
        foreach (var row in _rows.Values)
        {
            SetRow(row, row.Field.Value);
        }

        _filling = false;

        HowChanged();
        ScheduleChanged();
        UpdateSummary();
        ShowAdvanced(false);

        if (_original.Problems.Count > 0)
        {
            Say(string.Join("  ", _original.Problems), Theme.Current.Warning);
        }
        else if (_hookWarning is not null && !_isNew)
        {
            Say(_hookWarning, Theme.Current.Warning);
        }
        else
        {
            Say(_isNew ? JobEditorText.OpeningStatus : string.Empty, Theme.Current.Muted);
        }
    }

    private void SetRow(Row row, string? value)
    {
        switch (row.Editor)
        {
            case CheckBox box:
                row.Explicit = value is not null;
                box.Checked = value is null
                    ? row.Shown.InheritedChecked
                    : string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
                break;
            case ComboBox combo:
                Select(combo, value ?? JobEditorModel.InheritChoice);
                break;
            default:
                row.Editor.Text = value ?? string.Empty;
                break;
        }

        Relabel(row);
    }

    private string? ReadRow(Row row) => row.Editor switch
    {
        CheckBox box => row.Explicit ? (box.Checked ? "true" : "false") : null,
        ComboBox combo => JobEditorModel.ChoiceValue(combo.SelectedItem as string),
        _ => row.Editor.Text,
    };

    private void Relabel(Row row)
    {
        if (_filling)
        {
            return;
        }

        var hooked = _hookWarning is not null
                     && JobEditorModel.Hooks.Contains(row.Field.Key, StringComparer.OrdinalIgnoreCase);

        row.Source.Text = hooked ? "will never run here" : JobEditorModel.SourceLabel(row.Field, ReadRow(row));
        row.Source.ForeColor = hooked ? Theme.Current.Warning : Theme.Current.Muted;
        _tips.SetToolTip(row.Source, hooked
            ? _hookWarning
            : row.Source.Text.StartsWith("inherited", StringComparison.Ordinal) ? JobEditorText.InheritedTooltip : string.Empty);
    }

    private void Inherit(Row row)
    {
        SetRow(row, null);
        Judged(row.Field.Key, row.Caption, null);
        UpdateSummary();
    }

    // ---- the two views ----------------------------------------------------------------------

    private void ShowAdvanced(bool advanced)
    {
        if (advanced && !_showingAdvanced)
        {
            CarryBasicsToAdvanced();
        }
        else if (!advanced && _showingAdvanced)
        {
            CarryAdvancedToBasics();
        }

        _showingAdvanced = advanced;
        _advanced.Visible = advanced;
        foreach (var control in _basics)
        {
            control.Visible = !advanced;
        }

        _toggle.Text = JobEditorModel.AdvancedLinkText(JobEditorModel.AdvancedSetCount(_original), advanced);
    }

    /// <summary>The radio that is on, or null when the file's strategy has no radio and the row is disabled.</summary>
    private HowRotated? HowShown()
    {
        if (!_how[0].Enabled)
        {
            return null;
        }

        for (var i = 0; i < _how.Length; i++)
        {
            if (_how[i].Checked)
            {
                return (HowRotated)i;
            }
        }

        return null;
    }

    private BasicsAnswers Answers() => new(
        HowShown(),
        _schedule.SelectedItem as string,
        _maxSize.Text,
        _rotate.Text,
        _maxAge.Text,
        Dated: _names.SelectedIndex == 1,
        (ArchiveCompression)Math.Max(_compression.SelectedIndex, 0),
        _oldDir.Text,
        _createOldDir.Checked);

    private void CarryBasicsToAdvanced()
    {
        foreach (var (key, value) in JobEditorModel.BasicsEdits(_original, Answers()))
        {
            if (_rows.TryGetValue(key, out var row))
            {
                SetRow(row, string.IsNullOrWhiteSpace(value) ? null : value);
            }
        }
    }

    private void CarryAdvancedToBasics()
    {
        _filling = true;

        string? Read(string key) => _rows.TryGetValue(key, out var row) ? ReadRow(row) : null;

        var managed = string.Equals(Read("kind"), "manage", StringComparison.OrdinalIgnoreCase);
        var how = managed
            ? HowRotated.Manage
            : Read("lockstrategy")?.Trim().ToLowerInvariant() switch
            {
                null or "rename" => HowRotated.Rename,
                "auto" => HowRotated.Auto,
                "copytruncate" => HowRotated.CopyTruncate,
                _ => (HowRotated?)null,
            };

        foreach (var radio in _how)
        {
            radio.Enabled = how is not null;
        }

        if (how is { } answer)
        {
            _how[(int)answer].Checked = true;
        }

        var schedule = Read("schedule");
        if (JobEditorModel.SizeApplies(schedule))
        {
            _schedule.SelectedIndex = 0;
        }
        else
        {
            Select(_schedule, schedule ?? JobEditorModel.DefaultChoice(JobSchema.Find("schedule")?.Default));
        }

        _maxSize.Text = Read("maxsize") ?? string.Empty;
        _rotate.Text = Read("rotate") ?? string.Empty;
        _maxAge.Text = Read("maxage") ?? string.Empty;
        _names.SelectedIndex = string.Equals(Read("dateext"), "true", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        _compression.SelectedIndex = (int)JobEditorModel.CompressionShown(Read("compress"), Read("compresstype"));
        _oldDir.Text = Read("olddir") ?? string.Empty;
        _createOldDir.Checked = string.Equals(Read("createolddir"), "true", StringComparison.OrdinalIgnoreCase);

        _filling = false;
        HowChanged();
        UpdateSummary();
    }

    /// <summary>
    /// The rows a "how" answer makes meaningless are disabled, and the hint says what the answer does.
    /// </summary>
    private void HowChanged()
    {
        var how = HowShown();
        var managed = how == HowRotated.Manage;
        var bySize = !_showingAdvanced && JobEditorModel.SizeApplies(ScheduleShown());

        _howHint.Text = how switch
        {
            HowRotated.Auto => JobEditorText.HowAutoHint,
            HowRotated.CopyTruncate => JobEditorText.HowCopyTruncateHint,
            HowRotated.Rename => JobEditorText.HowRenameHint,
            HowRotated.Manage => JobEditorText.HowManageHint,
            _ => JobEditorText.HowLocked,
        };
        _tips.SetToolTip(_howHint, _howHint.Text);

        _schedule.Enabled = _maxSize.Enabled = _earlyLead.Enabled = !managed && !bySize;
        _whenHint.Text = managed
            ? JobEditorText.WhenManaged
            : bySize ? JobEditorText.WhenBySize : JobEditorText.WhenHint;

        _names.Enabled = _oldDir.Enabled = _browseFolder.Enabled = _createOldDir.Enabled = !managed;

        UpdateSummary();
    }

    /// <summary>The schedule the visible view says, which decides whether the size threshold means anything.</summary>
    private string? ScheduleShown() =>
        _showingAdvanced && _rows.TryGetValue("schedule", out var row)
            ? ReadRow(row)
            : JobEditorModel.SizeApplies(Field("schedule")?.Value) && !_showingAdvanced
                ? "size"
                : JobEditorModel.ChoiceValue(_schedule.SelectedItem as string);

    private void ScheduleChanged()
    {
        if (_filling)
        {
            return;
        }

        if (_rows.TryGetValue("size", out var size))
        {
            size.Editor.Enabled = JobEditorModel.SizeApplies(ScheduleShown());
        }

        UpdateSummary();
    }

    private void UpdateSummary()
    {
        if (_filling)
        {
            return;
        }

        _summary.Text = JobSummary.Sentence(
            HowShown(),
            ScheduleShown(),
            _maxSize.Text,
            _rotate.Text,
            _maxAge.Text,
            dated: _names.SelectedIndex == 1,
            (ArchiveCompression)Math.Max(_compression.SelectedIndex, 0),
            _oldDir.Text,
            _files.Lines.FirstOrDefault(l => l.Trim().Length > 0));
        _tips.SetToolTip(_summary, _summary.Text);
    }

    // ---- judging a value before the CLI sees it --------------------------------------------

    private void JudgeOnLeave(TextBox box, string key) =>
        box.Leave += (_, _) =>
        {
            if (Field(key) is { } field)
            {
                Judged(key, null, JobEditorModel.Judge(field, box.Text));
            }
        };

    private void JudgeRow(Row row) =>
        Judged(row.Field.Key, row.Caption, JobEditorModel.Judge(row.Field, ReadRow(row)));

    private void Judged(string key, Label? caption, string? problem)
    {
        if (problem is null)
        {
            if (_problems.Remove(key) && _status.Text.Length > 0 && _status.ForeColor == Theme.Current.Danger)
            {
                Say(string.Empty, Theme.Current.Muted);
            }

            if (caption is not null)
            {
                caption.ForeColor = Theme.Current.Text;
            }

            return;
        }

        _problems[key] = problem;
        if (caption is not null)
        {
            caption.ForeColor = Theme.Current.Danger;
        }

        Say(problem, Theme.Current.Danger);
    }

    // ---- what would happen -------------------------------------------------------------------

    /// <summary>
    /// Runs a dry run of the saved job and shows what it would do.
    /// </summary>
    /// <remarks>
    /// Unelevated, like the Run page's dry run: a dry run writes no state, journal or
    /// notification and runs no hook. The verb reads the job from disk, so unsaved changes are
    /// not what it would preview; the status line says so instead of previewing the wrong job.
    /// </remarks>
    private async Task PreviewRunAsync()
    {
        if (_isNew)
        {
            Say(JobEditorText.PreviewSaveFirst, Theme.Current.Muted);
            return;
        }

        if (JobEditorModel.Changes(Args(dryRun: false)))
        {
            Say(JobEditorText.PreviewSaveFirst, Theme.Current.Warning);
            return;
        }

        var result = await _cli.RunAsync(DryRunPreviewProjection.Arguments(_configDir, _original.Job)).ConfigureAwait(true);

        if (IsDisposed)
        {
            return;
        }

        if (result.IsDefect)
        {
            LrDialog.Error(this, JobEditorText.PreviewTitle, result.Describe(), result.Details);
            return;
        }

        var preview = DryRunPreviewProjection.From(result, _original.Job);

        LrDialog.Show(this, preview.Tone switch
        {
            CheckTone.Error => DialogKind.Error,
            CheckTone.Warning => DialogKind.Warning,
            _ => DialogKind.Info,
        }, JobEditorText.PreviewTitle, preview.Message, preview.Details.Length > 0 ? preview.Details : null);
    }

    // ---- files -----------------------------------------------------------------------------

    private void BrowseForFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Where old copies go",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };

        if (_oldDir.Text.Trim().Length > 0)
        {
            dialog.InitialDirectory = _oldDir.Text.Trim().Replace('/', '\\');
        }

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _oldDir.Text = dialog.SelectedPath.Replace('\\', '/');
        }
    }

    private void BrowseForFile()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Pick one of the log files",
            Filter = "Log files (*.log;*.txt)|*.log;*.txt|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        var first = _files.Lines.FirstOrDefault(l => l.Trim().Length > 0);
        if (first is not null && PatternSuggestion.Folder(first) is { Length: > 0 } folder)
        {
            dialog.InitialDirectory = folder.Replace('/', '\\');
        }

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var line = PatternSuggestion.FromPick(dialog.FileName);
        _files.Text = _files.Text.Trim().Length == 0 ? line : _files.Text.TrimEnd() + Environment.NewLine + line;

        if (_isNew)
        {
            var suggestion = JobNameSuggestion.From(line);
            _name.Text = JobNameSuggestion.Apply(_name.Text.Trim(), _lastSuggestedName, suggestion);
            _lastSuggestedName = suggestion;
        }

        _ = PreviewAsync();
    }

    /// <summary>
    /// Says what the files box matches, one line at a time, through the real verb.
    /// </summary>
    /// <remarks>
    /// Never gates Check or Save: a line the CLI cannot answer for is a sentence in Muted, not a
    /// blocked button. A serial number drops an answer that arrives after the box has changed.
    /// </remarks>
    private async Task PreviewAsync()
    {
        var serial = ++_previewSerial;
        var lines = _files.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();

        if (lines.Length == 0)
        {
            _preview.Text = string.Empty;
            return;
        }

        var previews = new List<GlobPreview>();

        foreach (var line in lines.Take(PreviewLines))
        {
            var result = await _cli.RunAsync(GlobPreviewProjection.Arguments(line)).ConfigureAwait(true);

            if (IsDisposed || serial != _previewSerial)
            {
                return;
            }

            // A defect is one sentence here, like any other answer the preview cannot use: the
            // projection words it, and the dialog is for the verbs that write.
            previews.Add(GlobPreviewProjection.From(result));
        }

        var combined = GlobPreviewProjection.Combine(previews);

        _preview.Text = lines.Length > PreviewLines
            ? $"{combined.Sentence} (Preview shows the first {PreviewLines} lines.)"
            : combined.Sentence;
        _preview.ForeColor = combined.Tone switch
        {
            CheckTone.Error => Theme.Current.Danger,
            CheckTone.Warning => Theme.Current.Warning,
            _ => Theme.Current.Muted,
        };
        _tips.SetToolTip(_preview, combined.Remedy);
    }

    // ---- check and save ---------------------------------------------------------------------

    private async Task OneAtATimeAsync(Func<Task> action)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        _check.Enabled = _save.Enabled = false;

        try
        {
            await action().ConfigureAwait(true);
        }
        finally
        {
            _busy = false;

            if (!IsDisposed)
            {
                _check.Enabled = _save.Enabled = true;
            }
        }
    }

    /// <summary>
    /// What the form holds, as edits over the job as it was read.
    /// </summary>
    /// <remarks>
    /// The keys Basics asks about come from whichever view is showing; everything else comes from
    /// Advanced, whose rows hold the file's values until somebody touches them. A foreign key is
    /// sent only when it was removed. The size threshold is sent as cleared unless the schedule
    /// showing is <c>size</c>, or a calendar choice would never take effect.
    /// </remarks>
    private Dictionary<string, string?> Edited()
    {
        var edited = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = _name.Text.Trim(),
            ["paths"] = _files.Text,
            ["enabled"] = _enabled.Checked ? null : "false",
        };

        foreach (var (key, row) in _rows)
        {
            edited[key] = ReadRow(row);
        }

        if (!_showingAdvanced)
        {
            foreach (var (key, value) in JobEditorModel.BasicsEdits(_original, Answers()))
            {
                edited[key] = value;
            }
        }

        if (!JobEditorModel.SizeApplies(ScheduleShown()))
        {
            edited["size"] = null;
        }

        foreach (var (key, foreign) in _foreign.Where(f => f.Value.Removed))
        {
            edited[key] = null;
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

        if (IsDisposed)
        {
            return;
        }

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

        // The dry run first, unelevated: a refusal costs no UAC prompt.
        var dry = await _cli.RunAsync(Args(dryRun: true)).ConfigureAwait(true);

        if (IsDisposed)
        {
            return;
        }

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

        if (IsDisposed)
        {
            return;
        }

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

        var written = Report(result, checking: false);

        if (written is null)
        {
            return;
        }

        _said = written;
        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>
    /// What this window refuses on its own: a new job with no name or no files, and a value the
    /// CLI would refuse by the same grammar. Everything else is the CLI's to judge.
    /// </summary>
    private string? Refused()
    {
        if (_isNew && _name.Text.Trim().Length == 0)
        {
            return JobEditorText.NoName;
        }

        if (_isNew && _files.Text.Trim().Length == 0)
        {
            return JobEditorText.NoFiles;
        }

        return _problems.Values.FirstOrDefault();
    }

    /// <summary>
    /// Reports the verb's answer: the sentence in the status line, and the details in a dialog
    /// when it is an error with something to show.
    /// </summary>
    private string? Report(CliResult result, bool checking)
    {
        var view = JobEditProjection.From(result);

        Say(view.Message, view.Tone switch
        {
            CheckTone.Error => Theme.Current.Danger,
            CheckTone.Warning => Theme.Current.Warning,
            _ => Theme.Current.Muted,
        });

        if (view.Tone == CheckTone.Error && view.Details.Length > 0)
        {
            LrDialog.Show(this, DialogKind.Error, checking ? "Check" : "Save", view.Message, view.Details);
        }

        return view.Written ? view.Message : null;
    }

    private void Say(string text, Color colour)
    {
        _status.Text = text;
        _status.ForeColor = colour;
    }

    /// <summary>Selects a value, adding it first when the list does not have it, so a foreign spelling round-trips.</summary>
    private static void Select(ComboBox combo, string value)
    {
        if (!combo.Items.Contains(value))
        {
            combo.Items.Add(value);
        }

        combo.SelectedItem = value;
    }
}
