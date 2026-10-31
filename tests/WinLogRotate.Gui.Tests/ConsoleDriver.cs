using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;

using Xunit;

namespace WinLogRotate.Gui.Tests;

/// <summary>
/// Drives the real console: opens <see cref="MainForm"/>, pumps its messages, finds its controls
/// and answers its dialogs. The docs tour and the end-to-end journeys both stand on it.
/// </summary>
/// <remarks>
/// <para>
/// The window is driven on its own STA thread, pumping messages by hand, because the pages load
/// by shelling out to winlogrotate.exe and their continuations come back through the message
/// loop. A modal dialog runs its own loop, so dialogs are answered from a timer, which that loop
/// keeps ticking - and keeps ticking inside a script that is itself answering a dialog, which is
/// how a dialog opened from a dialog (the editor's Check, the credential prompt's mismatch) is
/// answered too.
/// </para>
/// <para>
/// A dialog nobody asked for is recorded and cancelled, and fails the run; so does an exception
/// that reaches the message loop.
/// </para>
/// </remarks>
internal sealed class ConsoleDriver
{
    private static bool _configured;

    private readonly List<string> _unexpected = [];
    private readonly HashSet<Form> _answered = [];
    private readonly Queue<Action<Form>> _scripts = [];

    private ConsoleDriver(ITestOutputHelper? log) => Log = log;

    public ITestOutputHelper? Log { get; }

    /// <summary>Runs <paramref name="script"/> against the console on an STA thread, and fails on anything unexpected.</summary>
    public static void Run(Action<ConsoleDriver> script, TimeSpan? limit = null)
    {
        var driver = new ConsoleDriver(TestContext.Current.TestOutputHelper);

        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                driver.Drive(script);
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

        var allowed = limit ?? TimeSpan.FromMinutes(3);
        Assert.True(thread.Join(allowed), $"The console script did not finish in {allowed.TotalMinutes} minutes.");
        failure?.Throw();
        Assert.Empty(driver._unexpected);
    }

    private void Drive(Action<ConsoleDriver> script)
    {
        // en-US whatever the machine: the docs PNGs must not show 172,4 KB or 15.06.2026, and a
        // journey that reads a label reads it in one language.
        var english = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = english;
        CultureInfo.DefaultThreadCurrentUICulture = english;
        CultureInfo.CurrentCulture = english;
        CultureInfo.CurrentUICulture = english;

        // Once per process: WinForms refuses most of these after its first window exists.
        if (!_configured)
        {
            _configured = true;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        }

        ThreadExceptionEventHandler onException = (_, e) => _unexpected.Add(e.Exception.ToString());
        Application.ThreadException += onException;

        // Hover effects follow the pointer, so it is parked off the window.
        Cursor.Position = Point.Empty;

        using var dialogs = new System.Windows.Forms.Timer { Interval = 100 };
        dialogs.Tick += (_, _) => Answer();
        dialogs.Start();

        try
        {
            script(this);
        }
        finally
        {
            dialogs.Stop();
            Application.ThreadException -= onException;
        }
    }

    /// <summary>
    /// Hands the newest modal dialog nobody has answered yet to the script waiting for it, or
    /// records it as unexpected; either way it is cancelled afterwards unless the script closed it.
    /// </summary>
    private void Answer()
    {
        if (Application.OpenForms.Cast<Form>().LastOrDefault(f => f.Modal && f.Visible && !_answered.Contains(f)) is not { } dialog)
        {
            return;
        }

        _answered.Add(dialog);

        try
        {
            if (_scripts.TryDequeue(out var script))
            {
                Log?.WriteLine($"dialog '{dialog.Text}'");
                script(dialog);
            }
            else if (_unexpected.Count < 20)
            {
                _unexpected.Add($"Unexpected dialog '{dialog.Text}': {string.Join(" | ", All(dialog).OfType<Label>().Select(l => l.Text))}");
                Log?.WriteLine(_unexpected[^1]);
            }
            else
            {
                Log?.WriteLine($"dialog '{dialog.Text}' will not close; disposing it");
                dialog.Dispose();
            }
        }
        finally
        {
            if (!dialog.IsDisposed && dialog.DialogResult == DialogResult.None)
            {
                dialog.DialogResult = DialogResult.Cancel;
                Log?.WriteLine($"cancelled '{dialog.Text}'");
            }
        }
    }

    public MainForm Open(string configDir)
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

    public void Navigate(Form form, string page)
    {
        var nav = All(form).OfType<ListBox>().First();
        nav.SelectedIndex = nav.Items.IndexOf(page);
        Settle();
    }

    /// <summary>
    /// Runs <paramref name="open"/>, and each of <paramref name="scripts"/> on the dialogs it opens,
    /// in order. Each dialog is cancelled afterwards unless its script answered it (a button, or
    /// DialogResult).
    /// </summary>
    public void Dialog(Action open, params Action<Form>[] scripts)
    {
        var done = 0;
        foreach (var script in scripts)
        {
            _scripts.Enqueue(dialog =>
            {
                try
                {
                    Settle();
                    script(dialog);
                }
                finally
                {
                    done++;
                }
            });
        }

        open();
        Wait(() => done == scripts.Length, scripts.Length == 1 ? "the dialog" : $"{scripts.Length} dialogs");
        Settle();
    }

    /// <summary>
    /// Pumps the message loop until <paramref name="ready"/> holds, then until no winlogrotate.exe
    /// is running - the closest thing a native window has to network idle - and a little longer,
    /// for the paint that follows.
    /// </summary>
    /// <remarks>
    /// The second wait is capped and does not fail: a child that outlives its page is not what the
    /// step asserts, and waiting on it without a cap is how the first version of the tour hung.
    /// </remarks>
    public void Wait(Func<bool> ready, string what, int seconds = 30)
    {
        var clock = Stopwatch.StartNew();

        while (!ready())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(seconds))
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
                Log?.WriteLine($"{what}: winlogrotate.exe still running ({string.Join(", ", running.Select(p => p.Id))})");
                break;
            }

            Pump();
        }

        for (var i = 0; i < 20; i++)
        {
            Pump();
        }

        Log?.WriteLine($"{what}: ready after {clock.Elapsed.TotalSeconds:F1}s");
    }

    private static void Pump()
    {
        Application.DoEvents();
        Thread.Sleep(25);
    }

    public void Settle() => Wait(() => true, "the window to go quiet");

    public void Click(Button button)
    {
        button.PerformClick();
        Settle();
    }

    public static DataGridView Grid(Control root) => All(root).OfType<DataGridView>().First(g => g.Visible);

    public static DataGridViewRow? Row(DataGridView grid, string job) =>
        grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => r.Cells["name"].Value as string == job);

    public void Select(DataGridView grid, string job)
    {
        var row = Row(grid, job) ?? throw new InvalidOperationException($"No row '{job}'.");
        grid.CurrentCell = row.Cells[0];
        row.Selected = true;
        Settle();
    }

    public static Button Button(Control root, string text) => Button(root, text, required: true)!;

    public static Button? Button(Control root, string text, bool required)
    {
        var button = All(root).OfType<Button>().FirstOrDefault(b => b.Text == text && b.Visible);
        return button is null && required ? throw new InvalidOperationException($"No visible '{text}' button.") : button;
    }

    /// <summary>Whether a visible label on <paramref name="root"/> contains <paramref name="text"/>.</summary>
    public static bool Says(Control root, string text) =>
        All(root).OfType<Label>().Any(l => l.Visible && l.Text.Contains(text, StringComparison.Ordinal));

    /// <summary>An <c>LrDialog</c>'s message: its first label that is not the details link.</summary>
    public static string Message(Form dialog) => All(dialog).OfType<Label>().First(l => l is not LinkLabel).Text;

    /// <summary>A private field of a window, for the editor's boxes, which carry no name a user would see.</summary>
    public static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

    /// <summary>Raises a protected event handler, for what a test cannot click: a link, a double-click.</summary>
    public static void Raise(Control control, string handler, EventArgs args) =>
        control.GetType().GetMethod(handler, BindingFlags.Instance | BindingFlags.NonPublic, [args.GetType()])!
            .Invoke(control, [args]);

    public static IEnumerable<Control> All(Control root)
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

/// <summary>
/// One window at a time: every class that opens the console or runs winlogrotate.exe joins it. The
/// dialog watcher reads Application.OpenForms and Wait() watches every winlogrotate.exe on the
/// machine, so two of them at once would answer each other's dialogs and wait on each other's runs.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleCollection
{
    public const string Name = "Console";
}
