using WinLogRotate.Core;
using WinLogRotate.Gui.Ui;

namespace WinLogRotate.Gui;

internal static class Program
{
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

        Application.Run(new MainForm(configDir));
        GC.KeepAlive(single);
    }
}
