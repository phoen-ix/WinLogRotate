using System.Globalization;
using WinLogRotate.Gui.Cli;
using WinLogRotate.Gui.Ui;

namespace WinLogRotate.Gui.Pages;

/// <summary>
/// Chooses what runs rotations, and switches between the options freely.
/// </summary>
/// <remarks>
/// <para>
/// Switching calls exactly the same CLI verb the installer does, so the path is exercised every
/// time anyone changes their mind rather than only once during setup. Rotation clocks live in a
/// host-independent state file, so switching never causes anything to re-rotate.
/// </para>
/// <para>
/// The time the task fires is a picker under the task option. It shows what config.toml says,
/// which doctor reports, and Apply passes it as <c>--at</c> - so the verb writes it to the
/// configuration before it registers, and a later repair keeps it. The task fired at three in the
/// morning for as long as this page existed, and there was nowhere to say otherwise.
/// </para>
/// </remarks>
public sealed class SchedulingPage : UserControl
{
    private readonly CliRunner _cli;
    private readonly string? _configDir;

    private readonly RadioButton _task = new() { Text = "Scheduled task (recommended)", AutoSize = true };
    // Disabled when the build cannot register one, which the model decides. It used to be
    // selectable, and `host use service` refuses unconditionally, so choosing it was a UAC
    // prompt followed by an error dialog.
    private readonly RadioButton _service = new()
    {
        Text = "Windows service",
        AutoSize = true,
        Enabled = SchedulingProjection.Offered(RunHostChoice.Service),
    };
    private readonly RadioButton _none = new() { Text = "Neither - I will trigger it myself", AutoSize = true };

    // Enabled only while the task is the selection: a time under an option nobody chose is a
    // control that looks like it does something.
    private readonly Label _atLabel = new() { Text = "Runs daily at", AutoSize = true, Margin = new Padding(48, 6, 6, 0) };
    private readonly DateTimePicker _at = new()
    {
        Format = DateTimePickerFormat.Custom,
        CustomFormat = "HH:mm",
        ShowUpDown = true,
        Width = 70,
        Enabled = false,
    };
    // Disabled until the first refresh says which host is registered. An Apply that is
    // clickable before the page knows anything is an Apply that acts on a selection nobody made.
    private readonly Button _apply =
        new() { Text = "Apply", Width = 110, FlatStyle = FlatStyle.System, Enabled = false };

    /// <summary>Whether the registered host is established, and therefore whether Apply means
    /// anything. Owned by <see cref="RefreshStatusAsync"/>; read by the Apply path.</summary>
    private bool _known;
    private readonly Label _current = new() { Dock = DockStyle.Top, Height = 44 };

    // Held so it can be disposed with the page. A Font assigned to a control is not owned by it,
    // so one created inline here outlived every navigation away from this page.
    private readonly Font _mono = new(FontFamily.GenericMonospace, 9f);

    private readonly TextBox _log;

    public SchedulingPage(CliRunner cli, string? configDir)
    {
        _cli = cli;
        _configDir = configDir;

        // Scaled with the monitor, in the order MainForm explains: layout suspended, the mode
        // and the dimensions, the controls, and then the one scale. A page is created after
        // that window has scaled, so it is not scaled by it and does this for itself.
        SuspendLayout();
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);

        _log = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = _mono,
        };

        LrDialog.AddShield(_apply);
        _apply.Click += async (_, _) => await ApplyAsync().ConfigureAwait(true);

        // Three radios of 19 pixels and three hints of 18, each with 6 of margin, are 147, and
        // the time row under the first hint is another 26 plus its 6: at 150 the last hint lost
        // its bottom line once the row was there.
        var options = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 184,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
        };

        var when = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        when.Controls.AddRange([_atLabel, _at]);
        _task.CheckedChanged += (_, _) => _at.Enabled = _task.Checked;

        options.Controls.Add(_task);
        options.Controls.Add(Hint("Runs daily as SYSTEM at the time below. A run missed while the machine was off is caught up afterwards."));
        options.Controls.Add(when);
        options.Controls.Add(_service);
        options.Controls.Add(Hint(
            SchedulingProjection.Unavailable(RunHostChoice.Service) ?? "A resident agent with its own timer."));
        options.Controls.Add(_none);
        options.Controls.Add(Hint("Nothing runs on its own. Trigger it from your own scheduler."));

        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40 };
        actions.Controls.Add(_apply);

        Controls.Add(_log);
        Controls.Add(actions);
        Controls.Add(options);
        Controls.Add(_current);

        // Deliberately no selection here. Which host is registered is a fact about the machine,
        // and RefreshStatusAsync is what learns it; pre-selecting one turned "I did not touch
        // this page" into "remove the service".
        Load += async (_, _) => await RefreshStatusAsync().ConfigureAwait(true);

        ResumeLayout(false);
        PerformAutoScale();
    }

    private static Label Hint(string text) =>
        new() { Text = "        " + text, AutoSize = false, Width = 640, Height = 18 };

    private async Task RefreshStatusAsync()
    {
        var result = await _cli.RunAsync(CliArgs.For(_configDir, "doctor", "--json"))
            .ConfigureAwait(true);

        // Navigated away from while doctor ran. Nothing to show it on, and _known must not be
        // touched: a disposed page's Apply cannot be pressed anyway.
        if (IsDisposed)
        {
            return;
        }

        var view = SchedulingProjection.From(result);

        _current.Text = view.Current;
        _current.ForeColor = view.Warn ? Theme.Current.Warning : Theme.Current.Muted;

        _task.Checked = view.Selected == RunHostChoice.Task;
        _service.Checked = view.Selected == RunHostChoice.Service;
        _none.Checked = view.Selected == RunHostChoice.None;

        // The date half is never shown and never sent; only the time of day travels.
        _at.Value = DateTime.Today.Add(view.Time);

        // A selection nobody made must not become an instruction. When the current host could
        // not be established, every option on this page is one of those.
        _known = view.Known;
        _apply.Enabled = _known;

        // Exit 4 is the only code that says nothing about what was or was not done can be relied
        // on, so it is the only one worth interrupting for - a configuration error is exit 2 and
        // belongs in the line above.
        if (result.IsDefect)
        {
            LrDialog.Error(this, "Scheduling", result.Describe(), result.Details);
        }
    }

    private async Task ApplyAsync()
    {
        var kind = _task.Checked ? "task" : _service.Checked ? "service" : "none";

        // Invariant and 24-hour: it is the [host] table's own spelling, and the verb parses it
        // with the same grammar the configuration is read with.
        var at = _at.Value.ToString("HH:mm", CultureInfo.InvariantCulture);

        _apply.Enabled = false;
        _log.Clear();
        _log.AppendText($"Switching to: {kind}{(_task.Checked ? $", daily at {at}" : "")}{Environment.NewLine}");

        try
        {
            // Streamed rather than awaited silently: a switch stops a service, waits for it,
            // removes a task and registers another, which can take ten seconds or more. Ten
            // seconds of a frozen window reads as a hang.
            var result = await _cli.RunElevatedAsync(
                _task.Checked
                    ? CliArgs.For(_configDir, "host", "use", "task", "--at", at)
                    : CliArgs.For(_configDir, "host", "use", kind),
                UiThread.LineTo(_log, line => _log.AppendText(line + Environment.NewLine)))
                .ConfigureAwait(true);

            if (IsDisposed)
            {
                return;
            }

            _log.AppendText(result.Describe() + Environment.NewLine);

            if (!result.Ok && result.Failure != CliFailure.UacDeclined)
            {
                LrDialog.Error(this, "Could not change the schedule", result.Describe(), result.Details);
            }

            await RefreshStatusAsync().ConfigureAwait(true);
        }
        finally
        {
            // Back to what is known, never unconditionally on. The refresh above has usually
            // just set this; on the path where it threw, the last established answer is still
            // the right one to offer - and if nothing was ever established, still nothing.
            if (!IsDisposed)
            {
                _apply.Enabled = _known;
            }
        }
    }

    /// <summary>Disposes the font the page created, after the control that used it.</summary>
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            _mono.Dispose();
        }
    }
}
