using WinLogRotate.Core;
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
    /// </remarks>
    private static IDisposable? ListenForQuit(Form form)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var quit = new EventWaitHandle(false, EventResetMode.AutoReset, Hosting.Names.GuiQuitEvent);

            var registered = ThreadPool.RegisterWaitForSingleObject(
                quit,
                (_, _) => Close(form),
                state: null,
                timeout: Timeout.InfiniteTimeSpan,
                executeOnlyOnce: true);

            return new Quitter(quit, registered);
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
    /// This can fire before the window is shown - the event is registered before
    /// Application.Run - or after it has closed on its own. An installer asking a GUI to close
    /// politely must never be the thing that crashes it, and the guard that makes that true is
    /// now <see cref="UiThread.Post"/>, which this method's own shape was taken from.
    /// </remarks>
    private static void Close(Form form) => UiThread.Post(form, form.Close);

    private sealed class Quitter(EventWaitHandle handle, RegisteredWaitHandle registration) : IDisposable
    {
        public void Dispose()
        {
            registration.Unregister(null);
            handle.Dispose();
        }
    }

    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

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

        var form = new MainForm(configDir);
        using var closing = ListenForQuit(form);

        Application.Run(form);
        GC.KeepAlive(single);
    }
}
