using System.Diagnostics;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.ExceptionServices;

using WinLogRotate.Gui.Cli;
using WinLogRotate.Gui.Pages;

using Xunit;

namespace WinLogRotate.Gui.Tests;

/// <summary>
/// Opens the real console on <see cref="DocsSeed"/>'s machine and saves every page and dialog the
/// docs show, as docs/screenshots/*.png.
/// </summary>
/// <remarks>
/// <para>
/// Opt-in: it runs only when DOCS_SCREENSHOT_DIR names where the PNGs go, which
/// scripts/docs-screenshots.sh sets. It is still a test - each shot first asserts that the page
/// shows what the image is supposed to show, and any dialog nobody asked for fails it.
/// </para>
/// <para>
/// The window is driven on its own STA thread, pumping messages by hand, because the pages load
/// by shelling out to winlogrotate.exe and their continuations come back through the message
/// loop. A modal dialog runs its own loop, so dialogs are captured from a timer, which that loop
/// keeps ticking.
/// </para>
/// </remarks>
public sealed class DocsTour
{
    private static readonly string? OutDir = Environment.GetEnvironmentVariable("DOCS_SCREENSHOT_DIR");

    private readonly List<string> _unexpected = [];
    private Action<Form>? _onDialog;
    private ITestOutputHelper? _log;

    [Fact]
    [Trait("Category", "Docs")]
    public void Screenshots()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(OutDir), "Set DOCS_SCREENSHOT_DIR to render the docs screenshots.");
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The console is WinForms and needs Windows.");

        _log = TestContext.Current.TestOutputHelper;
        Directory.CreateDirectory(OutDir!);
        if (DocsSeed.Build() is { } problem)
        {
            _unexpected.Add(problem);
        }

        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Tour();
            }
            catch (Exception e)
            {
                failure = ExceptionDispatchInfo.Capture(e);
            }
        });

        // Background, so a window stuck in a modal loop cannot keep the test host alive after
        // the join below has given up on it.
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromMinutes(6)), "The tour did not finish in six minutes.");
        failure?.Throw();
        Assert.Empty(_unexpected);

        DocsSeed.Clean();
    }

    private void Tour()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => _unexpected.Add(e.Exception.ToString());

        // Hover effects follow the pointer, so it is parked off the window.
        Cursor.Position = Point.Empty;

        // Anything modal that the current step did not ask for is recorded and dismissed, so a
        // surprise error dialog fails the tour instead of hanging it.
        using var dialogs = new System.Windows.Forms.Timer { Interval = 100 };
        dialogs.Tick += (_, _) =>
        {
            if (Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.Modal && f.Visible) is not { } dialog)
            {
                return;
            }

            dialogs.Stop();

            try
            {
                if (_onDialog is { } script)
                {
                    _onDialog = null;
                    _log?.WriteLine($"dialog '{dialog.Text}'");
                    script(dialog);
                }
                else if (_unexpected.Count < 20)
                {
                    _unexpected.Add($"Unexpected dialog '{dialog.Text}': {string.Join(" | ", All(dialog).OfType<Label>().Select(l => l.Text))}");
                    _log?.WriteLine(_unexpected[^1]);
                }
                else
                {
                    _log?.WriteLine($"dialog '{dialog.Text}' will not close; disposing it");
                    dialog.Dispose();
                    return;
                }
            }
            finally
            {
                if (!dialog.IsDisposed)
                {
                    dialog.DialogResult = DialogResult.Cancel;
                    _log?.WriteLine($"cancelled '{dialog.Text}'");
                }

                dialogs.Start();
            }
        };
        dialogs.Start();

        using (var form = Open(DocsSeed.EmptyConfig))
        {
            Wait(() => Button(form, JobsEmptyState.Add.Text, required: false) is not null, "the empty state");
            Shoot(form, "jobs-empty");
        }

        using (var form = Open(DocsSeed.ProblemConfig))
        {
            var jobs = Grid(form);
            Wait(() => jobs.Rows.Count == 2, "the jobs that still load");

            Dialog(() => Button(form, JobsToolbar.Check.Text).PerformClick(), check =>
            {
                Assert.Equal("Configuration", check.Text);

                // The details are what the picture is for. A LinkLabel has no PerformClick.
                var details = All(check).OfType<LinkLabel>().Single();
                typeof(LinkLabel).GetMethod("OnLinkClicked", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(details, [new LinkLabelLinkClickedEventArgs(details.Links[0])]);
                Wait(() => details.Text == "Hide details", "the details");
                Shoot(check, "jobs-check-problems");
            });
        }

        using (var form = Open(DocsSeed.Config))
        {
            var jobs = Grid(form);
            Wait(() => jobs.Rows.Count == 3 && Row(jobs, "Shop API") is not null, "the three jobs");
            Shoot(form, "jobs-list");

            Select(jobs, "Shop API");
            Dialog(() => Button(form, JobsToolbar.Remove.Text).PerformClick(), confirm =>
            {
                Assert.Equal("Remove job", confirm.Text);
                Shoot(confirm, "jobs-remove");
            });

            Navigate(form, "Run");
            Button(form, "Dry run").PerformClick();
            var output = All(form).OfType<TextBox>().First(t => t.Multiline);
            Wait(() => output.Text.Contains("dry run:", StringComparison.Ordinal), "the dry run's summary");
            Shoot(form, "run-dry-run");

            Navigate(form, "History");
            var history = Grid(form);
            Wait(() => history.Rows.Count > 0, "the journal");
            Shoot(form, "history-journal");

            Navigate(form, "Scheduling");
            Shoot(form, "scheduling-task");

            Navigate(form, "Notifications");
            var providers = Grid(form);
            Wait(() => providers.Rows.Count > 0, "the providers");
            Shoot(form, "notifications-providers");

            // A provider row, not a target row: only those carry a credential.
            var provider = providers.Rows.Cast<DataGridViewRow>().First(r => r.Tag is string);
            providers.CurrentCell = provider.Cells[0];
            provider.Selected = true;

            Dialog(() => Button(form, "Set credential...").PerformClick(), prompt =>
            {
                Assert.StartsWith("Credential for", prompt.Text, StringComparison.Ordinal);
                Shoot(prompt, "notifications-credential");
            });

            Navigate(form, "Settings");
            var report = All(form).OfType<TextBox>().First(t => t.Multiline);
            Wait(() => report.Text.Length > 0, "the doctor report");
            Shoot(form, "settings-overview");

            // Last: opened straight after the first Jobs page loaded, before the Remove dialog, the
            // tour hung after this editor's shots for reasons never pinned down. Here it closes,
            // and the dialog watchdog above would dispose it if it ever stopped closing again.
            Navigate(form, "Jobs");
            jobs = Grid(form);
            Wait(() => jobs.Rows.Count == 3, "the jobs, for the editor");
            Select(jobs, "Shop API");
            Dialog(() => Button(form, JobsToolbar.Edit.Text).PerformClick(), editor =>
            {
                Wait(() => editor.Text == "Job: Shop API", "the editor");
                Shoot(editor, "jobs-editor");

                var menu = All(editor).OfType<ListBox>().First();
                menu.SelectedIndex = menu.Items.Cast<object>().ToList().FindIndex(i => $"{i}".StartsWith("Keep", StringComparison.Ordinal));
                Settle();
                Shoot(editor, "jobs-editor-keep");
            });
        }
    }

    private MainForm Open(string configDir)
    {
        var form = new MainForm(configDir)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(40, 40),
        };

        form.Show();
        Settle();
        return form;
    }

    private void Navigate(Form form, string page)
    {
        var nav = All(form).OfType<ListBox>().First();
        nav.SelectedIndex = nav.Items.IndexOf(page);
        Settle();
    }

    /// <summary>Runs <paramref name="open"/>, and <paramref name="script"/> on the dialog it opens, which is then cancelled.</summary>
    private void Dialog(Action open, Action<Form> script)
    {
        var done = false;
        _onDialog = dialog =>
        {
            try
            {
                Settle();
                script(dialog);
            }
            finally
            {
                done = true;
            }
        };

        open();
        Wait(() => done, "the dialog");
        Settle();
    }

    /// <summary>
    /// Pumps the message loop until <paramref name="ready"/> holds, then until no winlogrotate.exe
    /// is running - the closest thing a native window has to network idle - and a little longer,
    /// for the paint that follows.
    /// </summary>
    /// <remarks>
    /// The second wait is capped and does not fail: a child that outlives its page is not what the
    /// step asserts, and waiting on it without a cap is how the first version of this tour hung.
    /// </remarks>
    private void Wait(Func<bool> ready, string what)
    {
        var clock = Stopwatch.StartNew();

        while (!ready())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(30))
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            Pump();
        }

        var idle = Stopwatch.StartNew();
        while (Process.GetProcessesByName("winlogrotate") is { Length: > 0 } running)
        {
            if (idle.Elapsed > TimeSpan.FromSeconds(10))
            {
                _log?.WriteLine($"{what}: winlogrotate.exe still running ({string.Join(", ", running.Select(p => p.Id))})");
                break;
            }

            Pump();
        }

        for (var i = 0; i < 20; i++)
        {
            Pump();
        }

        _log?.WriteLine($"{what}: ready after {clock.Elapsed.TotalSeconds:F1}s");
    }

    private static void Pump()
    {
        Application.DoEvents();
        Thread.Sleep(25);
    }

    private void Settle() => Wait(() => true, "the window to go quiet");

    /// <summary>
    /// Saves the window's client area. The frame is left out: drawn by WM_PRINT it is the classic
    /// frame, not the one Windows shows.
    /// </summary>
    private void Shoot(Form form, string name)
    {
        using var full = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(full, new Rectangle(Point.Empty, form.Size));

        var origin = form.PointToScreen(Point.Empty);
        var client = new Rectangle(origin.X - form.Left, origin.Y - form.Top, form.ClientSize.Width, form.ClientSize.Height);

        using var shot = full.Clone(client, full.PixelFormat);
        shot.Save(Path.Combine(OutDir!, name + ".png"), ImageFormat.Png);
        _log?.WriteLine($"shot {name}");
    }

    private static DataGridView Grid(Form form) => All(form).OfType<DataGridView>().First(g => g.Visible);

    private static DataGridViewRow? Row(DataGridView grid, string job) =>
        grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => r.Cells["name"].Value as string == job);

    private void Select(DataGridView grid, string job)
    {
        var row = Row(grid, job)!;
        grid.CurrentCell = row.Cells[0];
        row.Selected = true;
        Settle();
    }

    private static Button Button(Form form, string text) => Button(form, text, required: true)!;

    private static Button? Button(Form form, string text, bool required)
    {
        var button = All(form).OfType<Button>().FirstOrDefault(b => b.Text == text && b.Visible);
        return button is null && required ? throw new InvalidOperationException($"No visible '{text}' button.") : button;
    }

    private static IEnumerable<Control> All(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;

            foreach (var nested in All(child))
            {
                yield return nested;
            }
        }
    }
}
