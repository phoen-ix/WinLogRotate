using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WinLogRotate.Hosting.Hooks;

[StructLayout(LayoutKind.Sequential)]
internal struct ServiceStatus
{
    public uint ServiceType;
    public uint CurrentState;
    public uint ControlsAccepted;
    public uint Win32ExitCode;
    public uint ServiceSpecificExitCode;
    public uint CheckPoint;
    public uint WaitHint;
}

/// <summary>
/// The four advapi32 calls needed to send a service control code.
/// </summary>
/// <remarks>
/// Hand-declared for the same reason <c>EventLogNative</c> is: <c>System.ServiceProcess</c> is not
/// in the shared framework for a plain <c>net10.0</c> target, and this needs one control code.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class ServiceControlNative
{
    /// <summary>Enough to look a service up by name. Nothing more is asked for.</summary>
    internal const uint ScManagerConnect = 0x0001;

    /// <summary>
    /// The access right <c>SERVICE_CONTROL_PARAMCHANGE</c> requires, plus the one that lets a
    /// failure say what state the service was in.
    /// </summary>
    internal const uint ServicePauseContinue = 0x0040;
    internal const uint ServiceQueryStatus = 0x0004;

    /// <summary>
    /// "Re-read your configuration" - the Windows equivalent of logrotate's <c>kill -HUP</c>.
    /// </summary>
    internal const uint ControlParamChange = 0x0004;

    internal const int ErrorAccessDenied = 5;
    internal const int ErrorInvalidServiceControl = 1052;
    internal const int ErrorServiceNotActive = 1062;
    internal const int ErrorServiceDoesNotExist = 1060;

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint OpenSCManager(
        string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint OpenService(
        nint hSCManager, string lpServiceName, uint dwDesiredAccess);

    [LibraryImport("advapi32.dll", EntryPoint = "ControlService", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ControlService(
        nint hService, uint dwControl, ref ServiceStatus lpServiceStatus);

    [LibraryImport("advapi32.dll", EntryPoint = "CloseServiceHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseServiceHandle(nint hSCObject);
}
