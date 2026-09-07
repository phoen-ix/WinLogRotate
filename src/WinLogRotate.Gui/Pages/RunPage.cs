using WinLogRotate.Gui.Cli;
using WinLogRotate.Gui.Ui;

namespace WinLogRotate.Gui.Pages;

/// <summary>
/// Runs a rotation and streams what it does.
/// </summary>
/// <remarks>
/// The output pane is literally the CLI's own output, which is why the GUI and the command line
/// can never disagree about what happened. Dry run is the default and is deliberately the
/// prominent button: this tool deletes files, and the safe action should be the easy one.
/// </remarks>
public sealed class RunPage : UserControl
{
    private readonly CliRunner _cli;
    private readonly string? _configDir;

    private readonly TextBox _output = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        Font = new Font(FontFamily.GenericMonospace, 9f),
    };

    private readonly Button _dryRun = new() { Text = "Dry run", Width = 120, FlatStyle = FlatStyle.System };
    private readonly Button _run = new() { Text = "Rotate now", Width = 120, FlatStyle = FlatStyle.System };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 24 };

    public RunPage(CliRunner cli, string? configDir)
    {
        _cli = cli;
        _configDir = configDir;

        // Rotating for real needs administrator on a per-machine install, so it carries the
        // shield. Dry run does not: it changes nothing.
        LrDialog.AddShield(_run);

        _dryRun.Click += async (_, _) => await ExecuteAsync(dryRun: true).ConfigureAwait(true);
        _run.Click += async (_, _) => await ExecuteAsync(dryRun: false).ConfigureAwait(true);

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40 };
        toolbar.Controls.AddRange([_dryRun, _run]);

        var hint = new Label
        {
            Dock = DockStyle.Top,
            Height = 34,
            Text = "A dry run shows exactly what would happen and changes nothing. "
                 + "It is the same code path as a real run, stopped one step earlier.",
        };

        Controls.Add(_output);
        Controls.Add(hint);
        Controls.Add(toolbar);
        Controls.Add(_status);
    }

    private async Task ExecuteAsync(bool dryRun)
    {
        if (!dryRun && !LrDialog.Confirm(this, "Rotate now",
                "This will compress, move and delete log files according to the configuration.\r\n\r\n"
                + "Run a dry run first if you are not sure what it will do.", needsAdmin: true))
        {
            return;
        }

        _dryRun.Enabled = _run.Enabled = false;
        _output.Clear();
        _status.ForeColor = Theme.Current.Muted;
        _status.Text = dryRun ? "Planning..." : "Rotating...";

        try
        {
            var arguments = dryRun
                ? MainForm.BuildArgs(_configDir, "run", "--dry-run", "--verbose")
                : MainForm.BuildArgs(_configDir, "run", "--verbose");

            var result = dryRun
                ? await _cli.RunAsync(arguments).ConfigureAwait(true)
                : await _cli.RunElevatedAsync(arguments, Append).ConfigureAwait(true);

            if (result.StdOut.Length > 0)
            {
                Append(result.StdOut);
            }

            if (result.StdErr.Length > 0)
            {
                Append(result.StdErr);
            }

            _status.Text = result.Describe();
            _status.ForeColor = result.Ok ? Theme.Current.Muted : Theme.Current.Danger;
        }
        finally
        {
            _dryRun.Enabled = _run.Enabled = true;
        }
    }

    private void Append(string text)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => Append(text));
            return;
        }

        _output.AppendText(text.EndsWith('\n') ? text : text + Environment.NewLine);
    }
}
