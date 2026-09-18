using Microsoft.Win32;
using Shouldly;
using WinLogRotate.Contracts;
using WinLogRotate.Hosting;
using WinLogRotate.Hosting.Diagnostics;
using Xunit;

namespace WinLogRotate.Windows.Tests;

/// <summary>
/// Whether the Event Log writer can tell a registered source from one that is not.
/// </summary>
/// <remarks>
/// <para>
/// It could not. <c>RegisterEventSourceW</c> succeeds for any name at all - an unregistered
/// source is quietly attached to the Application log with no message file behind it - so
/// <c>IsRegistered</c> was true on every Windows machine. <c>doctor</c> reported a per-user
/// install as writable, events went into the Application log that Event Viewer could only
/// render as "the description for Event ID cannot be found", and the <c>eventlog:</c> target's
/// per-user refusal never fired.
/// </para>
/// <para>
/// Every test here uses scratch names, so the product's own source is neither consulted nor
/// touched. The registered case creates its key under a scratch <i>log</i>, not under
/// Application, so nothing is ever attached to the real one; Dispose removes the whole scratch
/// log, the way <c>MachineEntropyTests</c> removes its scratch key.
/// </para>
/// </remarks>
public sealed class EventLogWriterTests : IDisposable
{
    private readonly string _log = $"WinLogRotate-test-{Guid.NewGuid():N}";
    private readonly string _source = $"WinLogRotate-test-source-{Guid.NewGuid():N}";

    public void Dispose()
    {
        // Dispose runs even for a skipped test, and Registry.LocalMachine is null off Windows.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            Registry.LocalMachine.DeleteSubKeyTree($@"{EventLogSourceKey.Root}\{_log}", throwOnMissingSubKey: false);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Not elevated; nothing was created either.
        }
    }

    private static void RequireAdmin()
    {
        WindowsOnly.Require();

        RegistryKey? probe = null;
        try
        {
            probe = Registry.LocalMachine.OpenSubKey(EventLogSourceKey.Root, writable: true);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException)
        {
            // A denial is the same answer as null: not elevated.
        }

        using (probe)
        {
            Assert.SkipWhen(probe is null, "needs administrator: this writes under HKLM\\SYSTEM");
        }
    }

    /// <summary>
    /// A source Windows has no record of is not writable, and nothing is written through it.
    /// </summary>
    /// <remarks>
    /// Both went the other way before: <c>IsRegistered</c> said true, and <c>TryWrite</c> put an
    /// event into the Application log with no message file to render it. Asserted through the
    /// public entry points rather than the registry probe alone, because the once-per-process
    /// memo behind them is what every caller in the product actually sees.
    /// </remarks>
    [Fact]
    public void ASourceWindowsHasNoRecordOfIsNotWritable()
    {
        WindowsOnly.Require();

        EventLogWriter.SourceIsRegistered(Names.EventLogName, _source).ShouldBeFalse();
        EventLogWriter.IsRegistered(_source).ShouldBeFalse();
        EventLogWriter.TryWrite(_source, Severity.Warning, 999, "must not be written").ShouldBeFalse();
    }

    /// <summary>
    /// A source with its key in place is found where the installer puts it.
    /// </summary>
    [Fact]
    public void ASourceWithItsKeyInPlaceIsRegistered()
    {
        RequireAdmin();

        using (var key = Registry.LocalMachine.CreateSubKey(EventLogSourceKey.PathFor(_log, _source), writable: true))
        {
            // What the installer writes, so the scratch source is the real one in miniature.
            key.SetValue("EventMessageFile", @"%SystemRoot%\System32\EventCreate.exe", RegistryValueKind.ExpandString);
            key.SetValue("TypesSupported", 7, RegistryValueKind.DWord);
        }

        EventLogWriter.SourceIsRegistered(_log, _source).ShouldBeTrue();
    }
}
