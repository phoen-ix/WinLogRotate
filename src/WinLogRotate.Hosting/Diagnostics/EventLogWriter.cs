using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using WinLogRotate.Contracts;

namespace WinLogRotate.Hosting.Diagnostics;

/// <summary>
/// Writes to the Windows Event Log, or does nothing at all.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Never throws.</strong> Not once, for any reason. A failure to record that something
/// happened must never become a failure to rotate: the log is the diagnostic channel, and a
/// diagnostic channel that can take down the thing it reports on is worse than no channel.
/// </para>
/// <para>
/// The source is registered by the installer, because <c>RegisterEventSource</c> only succeeds
/// for a source already present in the registry and creating one needs administrator. A
/// per-user install deliberately registers nothing, so being unavailable is an ordinary,
/// expected outcome and not a fault to report.
/// </para>
/// <para>
/// The handle is opened once and released on process exit rather than being owned by a caller.
/// It is a process-lifetime resource for a process that lives for one verb, and threading an
/// <c>IDisposable</c> through every command's construction to release it a few milliseconds
/// earlier would buy nothing.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class EventLogWriter
{
    /// <summary>
    /// The Event Log's hard ceiling on one insertion string is 31,839 characters. Truncating
    /// deterministically below it beats letting ReportEventW fail with ERROR_MORE_DATA, which
    /// would lose the whole event rather than the tail of one message.
    /// </summary>
    private const int MaxMessageChars = 31_000;

    /// <summary>
    /// Most events one invocation may write before it stops.
    /// </summary>
    /// <remarks>
    /// A job whose directory is unreachable can produce a diagnostic per file, and a few
    /// hundred near-identical entries in the Application log is how an administrator decides
    /// this source is noise and filters it out - taking the one event that mattered with it.
    /// The cap is announced by a final event rather than applied silently.
    /// </remarks>
    private const int MaxEventsPerRun = 50;

    private static readonly Lock Gate = new();
    private static nint _handle;
    private static bool _tried;
    private static int _written;
    private static bool _capAnnounced;

    /// <summary>True if the source is registered and writable on this machine.</summary>
    public static bool IsRegistered(string source)
    {
        lock (Gate)
        {
            return Open(source) != 0;
        }
    }

    /// <summary>Writes one event. Returns false if it was not written, for any reason.</summary>
    public static bool TryWrite(string source, Severity severity, int eventId, string message)
    {
        lock (Gate)
        {
            var handle = Open(source);
            if (handle == 0)
            {
                return false;
            }

            if (_written >= MaxEventsPerRun)
            {
                if (_capAnnounced)
                {
                    return false;
                }

                _capAnnounced = true;
                return Report(handle, Severity.Warning, EventIds.Unclassified,
                    $"More than {MaxEventsPerRun} diagnostics were reported by a single run; the "
                    + "rest were not written here. Run \"winlogrotate journal\" for the full record.");
            }

            _written++;
            return Report(handle, severity, eventId, message);
        }
    }

    private static bool Report(nint handle, Severity severity, int eventId, string message)
    {
        var text = message.Length > MaxMessageChars
            ? string.Concat(message.AsSpan(0, MaxMessageChars), "\r\n... truncated")
            : message;

        var pText = nint.Zero;
        var pArray = nint.Zero;
        try
        {
            pText = Marshal.StringToHGlobalUni(text);
            pArray = Marshal.AllocHGlobal(nint.Size);
            Marshal.WriteIntPtr(pArray, 0, pText);

            return EventLogNative.ReportEvent(
                handle,
                TypeFor(severity),

                // Zero, necessarily. A non-zero category with no CategoryMessageFile registered
                // renders in Event Viewer as a bare "(N)", which reads like a defect.
                wCategory: 0,
                (uint)eventId,
                lpUserSid: nint.Zero,
                wNumStrings: 1,
                dwDataSize: 0,
                pArray,
                lpRawData: nint.Zero);
        }
        catch (Exception e) when (e is OutOfMemoryException
            or DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            if (pArray != nint.Zero)
            {
                Marshal.FreeHGlobal(pArray);
            }

            if (pText != nint.Zero)
            {
                Marshal.FreeHGlobal(pText);
            }
        }
    }

    /// <summary>Opens the source once per process. Caller holds <see cref="Gate"/>.</summary>
    private static nint Open(string source)
    {
        if (_tried)
        {
            return _handle;
        }

        _tried = true;

        try
        {
            _handle = EventLogNative.RegisterEventSource(null, source);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            _handle = 0;
        }

        if (_handle != 0)
        {
            AppDomain.CurrentDomain.ProcessExit += static (_, _) => Close();
        }

        return _handle;
    }

    private static void Close()
    {
        lock (Gate)
        {
            if (_handle == 0)
            {
                return;
            }

            var handle = _handle;
            _handle = 0;
            try
            {
                EventLogNative.DeregisterEventSource(handle);
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                // Nothing to release, and nothing worth reporting.
            }
        }
    }

    /// <summary>
    /// TypesSupported is registered as 7 - Error, Warning and Information. There is no fourth
    /// type, so Critical shares Error's; the distinction survives in the event ID and the text.
    /// </summary>
    private static ushort TypeFor(Severity s) => s switch
    {
        Severity.Critical or Severity.Error => EventLogNative.EventLogErrorType,
        Severity.Warning => EventLogNative.EventLogWarningType,
        _ => EventLogNative.EventLogInformationType,
    };
}
