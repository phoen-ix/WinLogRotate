using WinLogRotate.Core;
using WinLogRotate.Gui.Cli;
using WinLogRotate.Gui.Ui;

namespace WinLogRotate.Gui;

internal static class Program
{
    /// <summary>
    /// Closes the window when the installer asks it to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Names.GuiQuitEvent</c> has been declared here and in the installer since the beginning,
    /// and a test pins the two spellings against each other - but nothing ever created or waited
    /// on the event, so the installer's <c>CloseGui</c> opened a handle that did not exist, set
    /// nothing, slept two seconds and then replaced the executable of a still-running GUI. The
    /// name test could never have caught that; it only proves the two files agree about a string.
    /// </para>
    /// <para>
    /// Created rather than opened, because the installer only ever opens it: if the GUI is not
    /// running there is nothing to close, which is exactly what <c>OpenEventW</c> returning null
    /// already tells it.
    /// </para>
    /// <para>
    /// The wait is armed when the window's handle exists, and re-arms after every signal. It used
    /// to be registered before <c>Application.Run</c> with <c>executeOnlyOnce</c>, so a signal in
    /// the window between registration and the handle was consumed by a post that had nothing to
    /// post to - and the registration was then spent, so no later signal was heard either. The
    /// installer's fixed two-second wait then failed on a locked executable, which is the exact
    /// outcome the event exists to prevent.
    /// </para>
    /// </remarks>
    private static IDisposable? ListenForQuit(Form form)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var quitter = new Quitter(
                new EventWaitHandle(false, EventResetMode.AutoReset, Hosting.Names.GuiQuitEvent));

            form.HandleCreated += (_, _) => quitter.Arm(() => Close(form));

            return quitter;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            // A GUI that cannot be asked to close politely is still a working GUI. The installer
            // falls back to waiting, which is what it did for every release before this one.
            return null;
        }
    }

    /// <summary>
    /// Closes the window from a pool thread, whatever state it is in.
    /// </summary>
    /// <remarks>
    /// This can fire after the window has closed on its own. An installer asking a GUI to close
    /// politely must never be the thing that crashes it, and the guard that makes that true is
    /// <see cref="UiThread.Post"/>, which this method's own shape was taken from.
    /// </remarks>
    private static void Close(Form form) => UiThread.Post(form, form.Close);

    /// <summary>The quit event and, once the window can hear it, the wait on it.</summary>
    private sealed class Quitter(EventWaitHandle handle) : IDisposable
    {
        private RegisteredWaitHandle? _registration;

        /// <summary>Starts listening, once. A handle recreated later does not register twice.</summary>
        public void Arm(Action onSignal)
        {
            _registration ??= ThreadPool.RegisterWaitForSingleObject(
                handle,
                (_, _) => onSignal(),
                state: null,
                timeout: Timeout.InfiniteTimeSpan,
                executeOnlyOnce: false);
        }

        public void Dispose()
        {
            _registration?.Unregister(null);
            handle.Dispose();
        }
    }

    /// <summary>
    /// Reports an exception nothing else caught, in this product's dialog.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An exception escaping an <c>async void</c> handler does not end a WinForms process; the
    /// framework shows its stock <c>ThreadExceptionDialog</c> - light, with a stack trace and a
    /// Continue button - and leaves the page half-updated. No handler was installed, so that is
    /// what the operator saw for anything a page failed to anticipate.
    /// </para>
    /// <para>
    /// The owner is dropped once the window is gone, because a dialog owned by a disposed form
    /// is itself an exception.
    /// </para>
    /// </remarks>
    private static void ReportUnexpected(Form window, Exception e) =>
        LrDialog.Error(
            window.IsDisposed ? null : window,
            "Unexpected error",
            "Something went wrong that this window did not anticipate. Whatever it was doing may "
            + "not have finished; the journal and the Event Log say what did.",
            e.ToString());

    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // Before the first control exists, or the framework refuses to change it. CatchException
        // is what routes an escaping handler exception to Application.ThreadException instead of
        // the stock dialog.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        // Windows 11 only (build 22000+), and this audience is largely Server 2019 and 2022,
        // so it is a bonus rather than the mechanism. Theme does the real work, back to
        // Windows 10, and MessageBox would stay light regardless - which is why LrDialog
        // exists and an architecture test forbids MessageBox in this project.
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            Application.SetColorMode(SystemColorMode.System);
        }

        // One window per session. The rotation gate is a separate, machine-wide mutex: this one
        // only stops a second copy of the console appearing.
        using var single = new Mutex(true, Hosting.Names.GuiInstanceMutex, out var createdNew);
        if (!createdNew)
        {
            return;
        }

        string? configDir = null;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] is "--config-dir")
            {
                configDir = args[i + 1];
            }
        }

        // What earlier operations could not remove themselves, before anything new is started.
        CliRunner.SweepStaleWork();

        var form = new MainForm(configDir);

        Application.ThreadException += (_, e) => ReportUnexpected(form, e.Exception);

        // A faulted task nobody awaited. Observed so the runtime does not count it as unhandled,
        // and reported on the UI thread, because this event is raised from the finalizer's.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            UiThread.Post(form, () => ReportUnexpected(form, e.Exception));
        };

        using var closing = ListenForQuit(form);

        Application.Run(form);
        GC.KeepAlive(single);
    }
}
