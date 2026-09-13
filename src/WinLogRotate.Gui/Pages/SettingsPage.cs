using System.Text.Json;
using WinLogRotate.Gui.Cli;
using WinLogRotate.Gui.Ui;

namespace WinLogRotate.Gui.Pages;

/// <summary>Paths, permissions and the environment, with the fix for anything wrong.</summary>
public sealed class SettingsPage : UserControl
{
    private readonly CliRunner _cli;
    private readonly string? _configDir;

    // Held so it can be disposed with the page. A Font assigned to a control is not owned by it,
    // so one created inline here outlived every navigation away from this page.
    private readonly Font _mono = new(FontFamily.GenericMonospace, 9f);

    private readonly TextBox _report;

    private readonly Button _harden = new() { Text = "Repair permissions", Width = 150, FlatStyle = FlatStyle.System };

    public SettingsPage(CliRunner cli, string? configDir)
    {
        _cli = cli;
        _configDir = configDir;

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

        Load += async (_, _) => await LoadAsync().ConfigureAwait(true);
    }

    private async Task LoadAsync()
    {
        var result = await _cli.RunAsync(CliArgs.For(_configDir, "doctor")).ConfigureAwait(true);

        // Navigated away from while doctor ran. Checked after each wait, because the second verb
        // takes as long as the first.
        if (IsDisposed)
        {
            return;
        }

        _report.Text = result.StdOut + result.StdErr;

        var json = await _cli.RunAsync(CliArgs.For(_configDir, "doctor", "--json"))
            .ConfigureAwait(true);

        if (IsDisposed)
        {
            return;
        }

        if (!json.Ok)
        {
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
