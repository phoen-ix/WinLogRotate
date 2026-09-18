using System.Text.Json;
using WinLogRotate.Core;
using WinLogRotate.Gui.Cli;
using WinLogRotate.Gui.Ui;

namespace WinLogRotate.Gui.Pages;

/// <summary>Paths, permissions and the environment, with the fix for anything wrong - and
/// the release this is, with the newer one when there is one.</summary>
public sealed class SettingsPage : UserControl
{
    private readonly CliRunner _cli;
    private readonly string? _configDir;

    // Held so it can be disposed with the page. A Font assigned to a control is not owned by it,
    // so one created inline here outlived every navigation away from this page.
    private readonly Font _mono = new(FontFamily.GenericMonospace, 9f);

    private readonly TextBox _report;

    private readonly Button _harden = new() { Text = "Repair permissions", Width = 150, FlatStyle = FlatStyle.System };

    // The Updates panel. Two choices, one of which is the default and neither of which installs
    // anything; a check, which reads; and an update, which is hidden until a check has found
    // something to install and which is the one press that changes the machine.
    private readonly RadioButton _manual = new() { Text = UpdateText.OnlyWhenAsked, AutoSize = true };
    private readonly RadioButton _daily = new() { Text = UpdateText.OnceADay, AutoSize = true };
    private readonly Button _checkNow = new() { Text = UpdatesPanel.CheckNow.Text, Width = UpdatesPanel.CheckNow.Width, FlatStyle = FlatStyle.System };
    private readonly Button _update = new() { Text = UpdatesPanel.Update.Text, Width = UpdatesPanel.Update.Width, FlatStyle = FlatStyle.System, Visible = false };
    private readonly Label _status = new() { AutoSize = true, Margin = new Padding(UpdatesPanel.Padding, UpdatesPanel.Padding + 3, 0, 0) };

    private UpdateStatus? _known;
    private bool _busy;

    // Armed when the installer has the file. If this window is still here when it fires, the
    // install did not happen - the installer waits for a rotation for two minutes and then
    // gives up, or refused for a reason only its log records - and the page says so rather
    // than leaving "Installing..." on screen for good.
    private readonly System.Windows.Forms.Timer _watchdog = new() { Interval = 180_000 };

    public SettingsPage(CliRunner cli, string? configDir)
    {
        _cli = cli;
        _configDir = configDir;

        // Scaled with the monitor, in the order MainForm explains: layout suspended, the mode
        // and the dimensions, the controls, and then the one scale. A page is created after
        // that window has scaled, so it is not scaled by it and does this for itself.
        SuspendLayout();
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);

        _report = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = _mono,
        };

        LrDialog.AddShield(_harden);
        _harden.Click += async (_, _) => await HardenAsync().ConfigureAwait(true);

        var refresh = new Button { Text = "Refresh", Width = 90, FlatStyle = FlatStyle.System };
        refresh.Click += async (_, _) => await LoadAsync().ConfigureAwait(true);

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40 };
        toolbar.Controls.AddRange([refresh, _harden]);

        Controls.Add(_report);
        Controls.Add(toolbar);
        Controls.Add(BuildUpdatesPanel());

        Load += async (_, _) => await LoadAsync().ConfigureAwait(true);

        ResumeLayout(false);
        PerformAutoScale();
    }

    private Panel BuildUpdatesPanel()
    {
        var preferences = PreferencesStore.Load();

        _manual.Checked = preferences.UpdateCheck != UpdateCheckMode.Daily;
        _daily.Checked = preferences.UpdateCheck == UpdateCheckMode.Daily;
        _status.Text = preferences.LastUpdateCheckUtc is { } last ? UpdateApplyProjection.Stamp(last) : UpdateText.NeverChecked;
        _status.ForeColor = Theme.Current.Muted;

        // Saved on the change of the one that becomes checked; the other's event fires too and
        // would save the same thing twice.
        _daily.CheckedChanged += (_, _) =>
        {
            if (_daily.Checked)
            {
                PreferencesStore.Save(PreferencesStore.Load() with { UpdateCheck = UpdateCheckMode.Daily });
            }
        };
        _manual.CheckedChanged += (_, _) =>
        {
            if (_manual.Checked)
            {
                PreferencesStore.Save(PreferencesStore.Load() with { UpdateCheck = UpdateCheckMode.Manual });
            }
        };

        _checkNow.Click += async (_, _) => await OneAtATimeAsync(CheckAsync).ConfigureAwait(true);
        _update.Click += async (_, _) => await OneAtATimeAsync(ApplyAsync).ConfigureAwait(true);

        _watchdog.Tick += async (_, _) => await WatchdogAsync().ConfigureAwait(true);

        var title = new Label { Text = UpdateText.Title, AutoSize = true, Font = new Font(Font, FontStyle.Bold) };
        _titleFont = title.Font;

        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        buttons.Controls.AddRange([_checkNow, _update, _status]);

        var rows = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(UpdatesPanel.Padding),
        };
        rows.Controls.AddRange([title, _manual, _daily, buttons]);

        var panel = new Panel { Dock = DockStyle.Top, Height = UpdatesPanel.Height };
        panel.Controls.Add(rows);
        return panel;
    }

    private Font? _titleFont;

    /// <summary>Asks the CLI whether a newer release exists, and shows what it said.</summary>
    private async Task CheckAsync()
    {
        _status.Text = UpdateText.Checking;
        _status.ForeColor = Theme.Current.Muted;
        _update.Visible = false;

        // Not CliArgs.For: the update verbs declare no --config-dir, and would answer one with
        // a parse error.
        var result = await _cli.RunAsync(["update", "check", "--json"]).ConfigureAwait(true);

        if (IsDisposed)
        {
            return;
        }

        // Stamped whether or not the feed answered, so a scheduled check on an offline machine
        // waits a day rather than trying on every start.
        PreferencesStore.Save(PreferencesStore.Load() with { LastUpdateCheckUtc = DateTimeOffset.UtcNow });

        if (result.IsDefect)
        {
            LrDialog.Error(this, UpdateText.Title, result.Describe(), result.Details);
        }

        _known = UpdateStatusProjection.From(result, ProductInfo.Version);

        _status.Text = _known.Sentence;
        _status.ForeColor = _known.Tone switch
        {
            CheckTone.Error => Theme.Current.Danger,
            CheckTone.Warning => Theme.Current.Warning,
            _ => Theme.Current.Muted,
        };

        if (_known.Available)
        {
            // The shield says "this will prompt", and only a per-machine install does. The
            // decision of whether to draw one at all is LrDialog's.
            if (_known.NeedsElevation)
            {
                LrDialog.AddShield(_update);
            }

            _update.Visible = true;
        }
    }

    /// <summary>Downloads and hands over. If it goes well, this window is closed by the installer.</summary>
    private async Task ApplyAsync()
    {
        if (_known is not { Available: true } known)
        {
            return;
        }

        _status.Text = UpdateText.Downloading;
        _status.ForeColor = Theme.Current.Muted;

        string[] arguments = ["update", "apply", "--restart-gui"];

        var onLine = UiThread.LineTo(this, line =>
        {
            if (UpdateApplyProjection.ProgressLine(line) is { } progress)
            {
                _status.Text = progress;
            }
        });

        var result = known.NeedsElevation
            ? await _cli.RunElevatedAsync(arguments, onLine).ConfigureAwait(true)
            : await _cli.RunStreamingAsync(arguments, onLine).ConfigureAwait(true);

        if (IsDisposed)
        {
            return;
        }

        if (result.IsDefect)
        {
            LrDialog.Error(this, UpdateText.Title, result.Describe(), result.Details);
        }

        var outcome = UpdateApplyProjection.From(result);

        _status.Text = outcome.Message;

        switch (outcome.Kind)
        {
            case UpdateOutcomeKind.Installing:
                // No dialog: a modal here would keep this window from closing when the
                // installer asks it to, and the installer gives it ten seconds.
                _watchdog.Start();
                break;

            case UpdateOutcomeKind.Cancelled:
                _status.ForeColor = Theme.Current.Muted;
                break;

            default:
                _status.ForeColor = Theme.Current.Danger;
                LrDialog.Show(this, DialogKind.Error, UpdateText.Title, outcome.Message, outcome.Details);
                break;
        }
    }

    /// <summary>Three minutes after a hand-over, if this window is still here.</summary>
    private async Task WatchdogAsync()
    {
        _watchdog.Stop();

        var identity = CliIdentity.Inspect(await _cli.RunAsync(CliIdentity.Arguments).ConfigureAwait(true));

        if (IsDisposed)
        {
            return;
        }

        // The files were replaced but this window was not closed - a console the installer
        // could not reach, which its quit event normally rules out. Otherwise nothing changed.
        var replaced = identity.Ok && identity.Version is { } version
            && !string.Equals(version, ProductInfo.Version, StringComparison.Ordinal);

        _status.Text = replaced ? UpdateText.InstalledButStillOpen(identity.Version!) : UpdateText.DidNotInstall;
        _status.ForeColor = replaced ? Theme.Current.Muted : Theme.Current.Warning;
        _update.Visible = !replaced;
        _checkNow.Enabled = true;
        _update.Enabled = true;
    }

    /// <summary>One check or one update at a time, and neither while the other runs.</summary>
    /// <remarks>
    /// After a hand-over the buttons stay disabled: the installer is about to close this window,
    /// and a second press during those seconds would start a second download.
    /// </remarks>
    private async Task OneAtATimeAsync(Func<Task> action)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        _checkNow.Enabled = _update.Enabled = false;

        try
        {
            await action().ConfigureAwait(true);
        }
        finally
        {
            _busy = false;

            if (!IsDisposed && !_watchdog.Enabled)
            {
                _checkNow.Enabled = _update.Enabled = true;
            }
        }
    }

    private async Task LoadAsync()
    {
        // One launch. The envelope carries the report the text verb prints, line for line, so
        // the page no longer ran doctor twice - once for the prose and once for the fields - and
        // no longer risked showing a report and a warning that disagreed.
        var json = await _cli.RunAsync(CliArgs.For(_configDir, "doctor", "--json"))
            .ConfigureAwait(true);

        // Navigated away from while doctor ran.
        if (IsDisposed)
        {
            return;
        }

        if (!json.Ok)
        {
            _report.Text = json.Describe() + Environment.NewLine + json.Details;

            // Exit 4 is the only code that says nothing about what was or was not done can be
            // relied on, so it is the only one worth interrupting for - a configuration error is
            // exit 2 and belongs in the report above.
            if (json.IsDefect)
            {
                LrDialog.Error(this, "Settings", json.Describe(), json.Details);
            }

            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json.StdOut);
            var payload = document.RootElement.GetProperty("result");

            _report.Text = string.Join(
                Environment.NewLine,
                payload.GetProperty("report").EnumerateArray().Select(line => line.GetString()));

            // A configuration directory a non-administrator can write is the one finding worth
            // interrupting for: it means any local user could have the run host execute their
            // command as SYSTEM. Whether this one is that finding is the model's decision - this
            // page used to decide it itself, and warned every per-user installation on every
            // visit, pointing at a Repair that would decline for the same reason the directory
            // is loose: it is in the user's own profile, by design.
            var warning = SettingsProjection.PermissionsWarning(
                payload.GetProperty("hooksAllowed").GetBoolean(),
                payload.GetProperty("aclVerdict").GetString() ?? string.Empty,
                payload.GetProperty("scope").GetString() ?? string.Empty,
                payload.TryGetProperty("aclFix", out var fix) && fix.ValueKind == JsonValueKind.String
                    ? fix.GetString()
                    : null);

            if (warning is not null)
            {
                LrDialog.Show(this, DialogKind.Warning, "Permissions", warning.Message, warning.Details);
            }
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // The report above is already on screen; this walk only decides whether to warn as
            // well. Caught wider than JsonException so that a missing field cannot end the
            // process from an async void handler.
        }
    }

    /// <summary>Repairs the permissions, once at a time.</summary>
    /// <remarks>
    /// The button stays enabled for the length of a UAC prompt otherwise, and a second press
    /// during it is a second prompt for the same repair.
    /// </remarks>
    private async Task HardenAsync()
    {
        _harden.Enabled = false;

        try
        {
            var result = await _cli.RunElevatedAsync(
                CliArgs.For(_configDir, "host", "repair", "--acl", "--json")).ConfigureAwait(true);

            if (IsDisposed)
            {
                return;
            }

            if (result.Failure != CliFailure.UacDeclined)
            {
                // Exit 0 is not the same as "it was secured". On a per-user installation the verb
                // declines, says why in a Warning, and exits 0 - so the one person whose directory
                // cannot be secured was the one person told it had been.
                var view = RepairProjection.From(result);

                LrDialog.Show(
                    this,
                    view.Tone switch
                    {
                        CheckTone.Clean => DialogKind.Info,
                        CheckTone.Warning => DialogKind.Warning,
                        _ => DialogKind.Error,
                    },
                    "Permissions",
                    view.Message,
                    view.Details);
            }

            await LoadAsync().ConfigureAwait(true);
        }
        finally
        {
            if (!IsDisposed)
            {
                _harden.Enabled = true;
            }
        }
    }

    /// <summary>Disposes the fonts the page created, after the controls that used them, and
    /// the watchdog, which would otherwise fire into a page that is gone.</summary>
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            _watchdog.Dispose();
            _mono.Dispose();
            _titleFont?.Dispose();
        }
    }
}
