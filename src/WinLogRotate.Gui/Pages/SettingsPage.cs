using System.Text.Json;
using WinLogRotate.Gui.Cli;
using WinLogRotate.Gui.Ui;

namespace WinLogRotate.Gui.Pages;

/// <summary>Paths, permissions and the environment, with the fix for anything wrong.</summary>
public sealed class SettingsPage : UserControl
{
    private readonly CliRunner _cli;
    private readonly string? _configDir;

    private readonly TextBox _report = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        Font = new Font(FontFamily.GenericMonospace, 9f),
    };

    private readonly Button _harden = new() { Text = "Repair permissions", Width = 150, FlatStyle = FlatStyle.System };

    public SettingsPage(CliRunner cli, string? configDir)
    {
        _cli = cli;
        _configDir = configDir;

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
        var result = await _cli.RunAsync(MainForm.BuildArgs(_configDir, "doctor")).ConfigureAwait(true);
        _report.Text = result.StdOut + result.StdErr;

        var json = await _cli.RunAsync(MainForm.BuildArgs(_configDir, "doctor", "--json"))
            .ConfigureAwait(true);

        if (!json.Ok)
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json.StdOut);
            var payload = document.RootElement.GetProperty("result");

            // A configuration directory a non-administrator can write is the one finding worth
            // interrupting for: it means any local user could have the run host execute their
            // command as SYSTEM.
            if (!payload.GetProperty("hooksAllowed").GetBoolean()
                && payload.GetProperty("aclVerdict").GetString() is { } verdict
                && verdict is not "Hardened" and not "NotApplicable")
            {
                LrDialog.Show(this, DialogKind.Warning, "Permissions",
                    "The configuration directory can be written by an account that is not an "
                    + "administrator, so hooks have been disabled for safety.\r\n\r\n"
                    + "Repair permissions to fix it.",
                    payload.TryGetProperty("aclFix", out var fix) ? fix.GetString() : null);
            }
        }
        catch (JsonException)
        {
        }
    }

    private async Task HardenAsync()
    {
        var result = await _cli.RunElevatedAsync(
            MainForm.BuildArgs(_configDir, "host", "repair", "--acl")).ConfigureAwait(true);

        if (result.Ok)
        {
            LrDialog.Info(this, "Permissions", "The configuration directory has been secured.");
        }
        else if (result.Failure != CliFailure.UacDeclined)
        {
            LrDialog.Error(this, "Permissions", result.Describe(), result.StdErr);
        }

        await LoadAsync().ConfigureAwait(true);
    }
}
