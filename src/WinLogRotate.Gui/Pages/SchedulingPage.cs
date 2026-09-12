using WinLogRotate.Gui.Cli;
using WinLogRotate.Gui.Ui;

namespace WinLogRotate.Gui.Pages;

/// <summary>
/// Chooses what runs rotations, and switches between the options freely.
/// </summary>
/// <remarks>
/// Switching calls exactly the same CLI verb the installer does, so the path is exercised every
/// time anyone changes their mind rather than only once during setup. Rotation clocks live in a
/// host-independent state file, so switching never causes anything to re-rotate.
/// </remarks>
public sealed class SchedulingPage : UserControl
{
    private readonly CliRunner _cli;
    private readonly string? _configDir;

    private readonly RadioButton _task = new() { Text = "Scheduled task (recommended)", AutoSize = true };
    private readonly RadioButton _service = new() { Text = "Windows service", AutoSize = true };
    private readonly RadioButton _none = new() { Text = "Neither - I will trigger it myself", AutoSize = true };
    // Disabled until the first refresh says which host is registered. An Apply that is
    // clickable before the page knows anything is an Apply that acts on a selection nobody made.
    private readonly Button _apply =
        new() { Text = "Apply", Width = 110, FlatStyle = FlatStyle.System, Enabled = false };

    /// <summary>Whether the registered host is established, and therefore whether Apply means
    /// anything. Owned by <see cref="RefreshStatusAsync"/>; read by the Apply path.</summary>
    private bool _known;
    private readonly Label _current = new() { Dock = DockStyle.Top, Height = 44 };
    private readonly TextBox _log = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Font = new Font(FontFamily.GenericMonospace, 9f),
    };

    public SchedulingPage(CliRunner cli, string? configDir)
    {
        _cli = cli;
        _configDir = configDir;

        LrDialog.AddShield(_apply);
        _apply.Click += async (_, _) => await ApplyAsync().ConfigureAwait(true);

        var options = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 130,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
        };

        options.Controls.Add(_task);
        options.Controls.Add(Hint("Runs daily as SYSTEM. Nothing stays resident, and a run missed while the machine was off is caught up afterwards."));
        options.Controls.Add(_service);
        options.Controls.Add(Hint("A resident agent with its own timer."));
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
    }

    private static Label Hint(string text) =>
        new() { Text = "        " + text, AutoSize = false, Width = 640, Height = 18 };

    private async Task RefreshStatusAsync()
    {
        var result = await _cli.RunAsync(CliArgs.For(_configDir, "doctor", "--json"))
            .ConfigureAwait(true);

        var view = SchedulingProjection.From(result);

        _current.Text = view.Current;
        _current.ForeColor = view.Warn ? Theme.Current.Warning : Theme.Current.Muted;

        _task.Checked = view.Selected == RunHostChoice.Task;
        _service.Checked = view.Selected == RunHostChoice.Service;
        _none.Checked = view.Selected == RunHostChoice.None;

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

        _apply.Enabled = false;
        _log.Clear();
        _log.AppendText($"Switching to: {kind}{Environment.NewLine}");

        try
        {
            // Streamed rather than awaited silently: a switch stops a service, waits for it,
            // removes a task and registers another, which can take ten seconds or more. Ten
            // seconds of a frozen window reads as a hang.
            var result = await _cli.RunElevatedAsync(
                CliArgs.For(_configDir, "host", "use", kind),
                UiThread.LineTo(_log, line => _log.AppendText(line + Environment.NewLine)))
                .ConfigureAwait(true);

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
            _apply.Enabled = _known;
        }
    }
}
