using WinLogRotate.Contracts;
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
    private readonly Panel _content = new() { Dock = DockStyle.Fill, Padding = new Padding(MainWindowLayout.ContentPadding) };
    private readonly ListBox _nav = new() { Dock = DockStyle.Left, Width = MainWindowLayout.NavigationWidth, BorderStyle = BorderStyle.None, IntegralHeight = false };
    private readonly Label _banner = new() { Dock = DockStyle.Top, Height = 0, Padding = new Padding(12, 8, 12, 8), Visible = false };
    private readonly string? _configDir;

    public MainForm(string? configDir = null)
    {
        _configDir = configDir;

        // Scaled with the monitor. ApplicationHighDpiMode is PerMonitorV2, which scales the
        // fonts, and nothing scaled the pixels: at 125 per cent a 24-pixel box on a 30-pixel
        // pitch held 30-pixel text, and an 18-pixel caption clipped its own descenders. The
        // dimensions say that every number in this project is a logical pixel at 100 per cent.
        //
        // The order is the mechanism. The framework scales a window once, at the first layout
        // after its dimensions are set, and scales whatever exists at that moment - and with
        // layout running, setting the dimensions is that layout, before a single control has
        // been added. So layout is suspended for the whole build, and PerformAutoScale at the
        // end scales everything that exists by then - what was placed by hand and what kept its
        // default size - and records that it has, so nothing is scaled twice.
        //
        // Every window and every page does the same, because a page is created after this
        // window has scaled and so is not scaled by it.
        SuspendLayout();
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);

        Text = $"{ProductInfo.Name} {ProductInfo.Version}";
        // From the layout, where WindowLayoutTests can add them up against the Jobs toolbar.
        MinimumSize = new Size(MainWindowLayout.MinimumWidth, MainWindowLayout.MinimumHeight);
        ClientSize = new Size(MainWindowLayout.Width, MainWindowLayout.Height);
        StartPosition = FormStartPosition.CenterScreen;

        _nav.Items.AddRange(["Jobs", "Run", "History", "Scheduling", "Notifications", "Settings"]);
        _nav.SelectedIndexChanged += (_, _) => ShowPage(_nav.SelectedIndex);

        Controls.Add(_content);
        Controls.Add(_nav);
        Controls.Add(_banner);

        Load += OnLoad;

        ResumeLayout(false);
        PerformAutoScale();
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
    /// <remarks>
    /// <para>
    /// One banner, so one message, and the order is: the executable is missing; it is not ours;
    /// it did not answer intelligibly; it speaks a different version of the response format;
    /// this window is not elevated. Identity beats elevation because the elevation note costs
    /// the reader nothing - everything is still readable and a privileged action will prompt
    /// when it comes to it - while the identity notes say that what you are looking at may be
    /// wrong. Saying anything about UAC while we do not know what program we are talking to is
    /// noise on top of a real problem.
    /// </para>
    /// <para>
    /// This replaced a <c>doctor --json</c> call that read only whether the process started and
    /// discarded the envelope. The probe answers that better and answers three questions doctor
    /// cannot: is it ours, can we read what it says, and do the two halves agree about the wire
    /// format. Every page that needs doctor already runs it for itself.
    /// </para>
    /// </remarks>
    private async Task CheckEnvironmentAsync()
    {
        // Deliberately not CliArgs.For. The root verb declares no --config-dir, so appending one
        // makes this a parse error: it short-circuits to ParseErrorReporter, which exits 2 with
        // an LR1007 envelope under --json - never the answer wanted. The GUI is launched with a
        // directory often enough that this would report a broken CLI to the people least able to
        // explain it. CliIdentityTests.AConfigDirectoryTurnsTheProbeIntoAParseError is the pin.
        var identity = CliIdentity.Inspect(
            await _cli.RunAsync(CliIdentity.Arguments).ConfigureAwait(true));

        // Closed while the probe ran, which a quick operator or the installer's quit event can
        // both manage. There is nothing left to put a banner on.
        if (IsDisposed)
        {
            return;
        }

        if (!identity.Ok)
        {
            ShowBanner(identity.Describe(), identity.Severity);
            return;
        }

        // A read-only view is a supported way to run: the configuration directory is readable
        // by everyone and writable only by administrators, by design.
        //
        // This window's own token, not the one the probe reported. They agree - the child
        // inherits it - but this is a statement about this process, it is known without waiting
        // for anything, and it cannot fail.
        if (!Privilege.IsElevated())
        {
            ShowBanner(
                "Running without administrator rights. You can review everything here; " +
                "changing jobs or scheduling will ask for elevation when needed.",
                Severity.Info);
        }
    }

    /// <summary>
    /// Puts a message across the top of the window.
    /// </summary>
    /// <remarks>
    /// The colour is a role, and <c>Theme.Walk</c> keeps a label's role when it runs again on
    /// the next navigation - so a red banner stays red. It used to be repainted as ordinary
    /// text, and the words survived, which is presumably why nobody noticed.
    /// </remarks>
    private void ShowBanner(string text, Severity severity)
    {
        _banner.Text = text;
        _banner.ForeColor = Tone(severity);

        // In device units: this runs after the window has scaled, so a logical 40 here would be
        // the one height on the form that did not grow with the monitor.
        _banner.Height = LogicalToDeviceUnits(40);
        _banner.Visible = true;
    }

    private static Color Tone(Severity severity) => severity switch
    {
        Severity.Critical or Severity.Error => Theme.Current.Danger,
        Severity.Warning => Theme.Current.Warning,
        _ => Theme.Current.Muted,
    };

    /// <summary>
    /// Replaces the page being shown, and disposes the one it replaces.
    /// </summary>
    /// <remarks>
    /// <c>Controls.Clear()</c> detaches without disposing, so every navigation leaked a page: its
    /// window handles parked, its fonts rooted, and its handlers still wired to a runner that
    /// would call them. A control disposes itself out of its parent's collection, so disposing
    /// each old page is the whole of the replacement.
    /// </remarks>
    private void ShowPage(int index)
    {
        foreach (var stale in _content.Controls.Cast<Control>().ToArray())
        {
            stale.Dispose();
        }

        Control page = index switch
        {
            0 => new JobsPage(_cli, _configDir),
            1 => new RunPage(_cli, _configDir),
            2 => new HistoryPage(_cli, _configDir),
            3 => new SchedulingPage(_cli, _configDir),
            4 => new NotificationsPage(_cli, _configDir),
            _ => new SettingsPage(_cli, _configDir),
        };

        page.Dock = DockStyle.Fill;
        _content.Controls.Add(page);
        Theme.Apply(this, Theme.IsDark);
    }

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
