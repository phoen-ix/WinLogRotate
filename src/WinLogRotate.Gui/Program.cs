using WinLogRotate.Core;

namespace WinLogRotate.Gui;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        // Windows 11 only (build 22000+), and this audience is largely Server 2019/2022,
        // so treat it as a bonus rather than the mechanism. The real theming is a manual
        // tree walk plus DwmSetWindowAttribute for title bars, landing in M18.
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            Application.SetColorMode(SystemColorMode.System);
        }

        using var form = new Form
        {
            Text = $"{ProductInfo.Name} {ProductInfo.Version}",
            Width = 900,
            Height = 600,
            StartPosition = FormStartPosition.CenterScreen,
        };
        Application.Run(form);
    }
}
