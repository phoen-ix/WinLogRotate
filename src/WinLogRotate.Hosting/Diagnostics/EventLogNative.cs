using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WinLogRotate.Hosting.Diagnostics;

/// <summary>
/// The three advapi32 calls needed to write to the Windows Event Log.
/// </summary>
/// <remarks>
/// <para>
/// Hand-declared rather than reached through <c>System.Diagnostics.EventLog</c>, which is not
/// part of the shared framework for a plain <c>net10.0</c> target and would mean a package
/// reference plus its reflection surface, for three functions.
/// </para>
/// <para>
/// <c>lpStrings</c> is declared as <see cref="nint"/> and built with <see cref="Marshal"/>
/// rather than marshalled as a <c>string[]</c>. That keeps the promise the csproj makes: no
/// hand-written unsafe code, only the stubs <c>[LibraryImport]</c> generates.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class EventLogNative
{
    internal const ushort EventLogSuccessType = 0x0000;
    internal const ushort EventLogErrorType = 0x0001;
    internal const ushort EventLogWarningType = 0x0002;
    internal const ushort EventLogInformationType = 0x0004;

    /// <param name="lpUNCServerName">Null for the local machine, which is the only case here.</param>
    [LibraryImport("advapi32.dll", EntryPoint = "RegisterEventSourceW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint RegisterEventSource(string? lpUNCServerName, string lpSourceName);

    [LibraryImport("advapi32.dll", EntryPoint = "ReportEventW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ReportEvent(
        nint hEventLog,
        ushort wType,
        ushort wCategory,
        uint dwEventID,
        nint lpUserSid,
        ushort wNumStrings,
        uint dwDataSize,
        nint lpStrings,
        nint lpRawData);

    [LibraryImport("advapi32.dll", EntryPoint = "DeregisterEventSource", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeregisterEventSource(nint hEventLog);
}
