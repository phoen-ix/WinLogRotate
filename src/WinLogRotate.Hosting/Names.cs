namespace WinLogRotate.Hosting;

/// <summary>
/// Names shared between the application and the installer script.
/// </summary>
/// <remarks>
/// <para>
/// Every constant here is hard-coded a second time in <c>packaging/winlogrotate.nsi</c>, and a
/// test asserts the two agree. That test matters more than it looks: renaming one of these in
/// only one place does not fail loudly. The installer simply stops recognising a running
/// rotation and overwrites the executable underneath it, or stops finding the service it is
/// meant to remove - both silent, both discovered by a user rather than by CI.
/// </para>
/// <para>
/// Deliberately platform-neutral. These are strings, not API calls, and the pinning test has to
/// read them on the Linux leg where the installer script is also compiled.
/// </para>
/// </remarks>
public static class Names
{
    /// <summary>Machine-wide rotation gate. Global\ because the SYSTEM task lives in session 0
    /// and the GUI does not.</summary>
    public const string RotationMutex = @"Global\WinLogRotate.Rotation";

    /// <summary>Per-session single-instance guard for the GUI.</summary>
    public const string GuiInstanceMutex = @"Local\WinLogRotate.Gui";

    /// <summary>Asks a running GUI to close politely, so an upgrade need not kill it.</summary>
    public const string GuiQuitEvent = @"Local\WinLogRotate-quit-7f3a1c";

    /// <summary>The Windows service, when the service host is the chosen run model.</summary>
    public const string ServiceName = "WinLogRotate";

    /// <summary>Folder and task, as they appear in taskschd.msc.</summary>
    public const string TaskFolder = @"\WinLogRotate";
    public const string TaskName = "Rotate";
    public const string TaskPath = @"\WinLogRotate\Rotate";

    /// <summary>Event Log source, registered by the installer because creating one needs admin
    /// and the unelevated CLI would otherwise throw on its first write.</summary>
    public const string EventLogSource = "WinLogRotate";
    public const string EventLogName = "Application";

    /// <summary>Add/Remove Programs key, under the name the installer registers.</summary>
    public const string UninstallKey =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WinLogRotate";

    /// <summary>Machine policy that disables every update check, including the GUI's.
    /// Enterprises ask for this on day one; shipping it later means shipping it twice.</summary>
    public const string PolicyKey = @"SOFTWARE\Policies\WinLogRotate";
}
