using WinLogRotate.Core;
using WinLogRotate.Gui.Cli;
using WinLogRotate.Gui.Pages;
using WinLogRotate.Gui.Ui;

namespace WinLogRotate.Gui;

/// <summary>
/// The administration console.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not a resident agent, and deliberately without a tray icon. Rotation runs
/// unattended; on a server nobody is logged in at three in the morning, so a per-session tray
/// icon would usually not exist, would imply that rotation depends on this window being open,
/// and would duplicate what the run host already reports. Results surface in the Event Log, the
/// journal and <c>winlogrotate host status</c>. This window is opened, used and closed.
/// </para>
/// <para>
/// Every action shells out to the CLI rather than reimplementing it, which is what keeps the
/// two from ever disagreeing about what a rotation means.
/// </para>
/// </remarks>
public sealed class MainForm : Form
{
    private readonly CliRunner _cli = CliRunner.Resolve();
    private readonly Panel _content = new() { Dock = DockStyle.Fill, Padding = new Padding(16) };
    private readonly ListBox _nav = new() { Dock = DockStyle.Left, Width = 170, BorderStyle = BorderStyle.None, IntegralHeight = false };
    private readonly Label _banner = new() { Dock = DockStyle.Top, Height = 0, Padding = new Padding(12, 8, 12, 8), Visible = false };
    private readonly string? _configDir;

    public MainForm(string? configDir = null)
    {
        _configDir = configDir;

        Text = $"{ProductInfo.Name} {ProductInfo.Version}";
        MinimumSize = new Size(880, 560);
        ClientSize = new Size(1000, 640);
        StartPosition = FormStartPosition.CenterScreen;

        _nav.Items.AddRange(["Jobs", "Run", "History", "Scheduling", "Settings"]);
        _nav.SelectedIndexChanged += (_, _) => ShowPage(_nav.SelectedIndex);

        Controls.Add(_content);
        Controls.Add(_nav);
        Controls.Add(_banner);

        Load += OnLoad;
    }

    private async void OnLoad(object? sender, EventArgs e)
    {
        Theme.Apply(this, SystemPrefersDark());
        _nav.SelectedIndex = 0;

        await CheckEnvironmentAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Reports, once and non-modally, anything that would otherwise be discovered at the moment
    /// the user presses Save.
    /// </summary>
    private async Task CheckEnvironmentAsync()
    {
        var result = await _cli.RunAsync(Args("doctor", "--json")).ConfigureAwait(true);

        if (result.Failure == CliFailure.NotFound)
        {
            ShowBanner(
                "winlogrotate.exe could not be found, so nothing on these pages will work. " +
                "Reinstall, or place the two executables side by side.",
                Theme.Current.Danger);
            return;
        }

        // A read-only view is a supported way to run: the configuration directory is readable
        // by everyone and writable only by administrators, by design.
        if (!Privilege.IsElevated())
        {
            ShowBanner(
                "Running without administrator rights. You can review everything here; " +
                "changing jobs or scheduling will ask for elevation when needed.",
                Theme.Current.Muted);
        }
    }

    private void ShowBanner(string text, Color color)
    {
        _banner.Text = text;
        _banner.ForeColor = color;
        _banner.Height = 40;
        _banner.Visible = true;
    }

    private void ShowPage(int index)
    {
        _content.Controls.Clear();

        Control page = index switch
        {
            0 => new JobsPage(_cli, _configDir),
            1 => new RunPage(_cli, _configDir),
            2 => new HistoryPage(_cli, _configDir),
            3 => new SchedulingPage(_cli, _configDir),
            _ => new SettingsPage(_cli, _configDir),
        };

        page.Dock = DockStyle.Fill;
        _content.Controls.Add(page);
        Theme.Apply(this, Theme.IsDark);
    }

    internal string[] Args(params string[] verb) => BuildArgs(_configDir, verb);

    internal static string[] BuildArgs(string? configDir, params string[] verb) =>
        configDir is null ? verb : [.. verb, "--config-dir", configDir];

    /// <summary>
    /// Reads the system's app theme.
    /// </summary>
    /// <remarks>
    /// .NET 10's SetColorMode would do this, but only on Windows 11, and this audience is
    /// largely Server 2019 and 2022 where it does nothing. The registry value works everywhere
    /// back to Windows 10.
    /// </remarks>
    private static bool SystemPrefersDark()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int and 0;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }
}
