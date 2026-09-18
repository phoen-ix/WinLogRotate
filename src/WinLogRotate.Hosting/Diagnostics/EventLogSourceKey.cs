namespace WinLogRotate.Hosting.Diagnostics;

/// <summary>
/// Where Windows records that an Event Log source exists.
/// </summary>
/// <remarks>
/// <para>
/// A source is a registry key: <c>HKLM\SYSTEM\CurrentControlSet\Services\EventLog\{log}\{source}</c>,
/// carrying the message file Event Viewer renders the event's text with. The installer creates the
/// product's own - <c>EVENTLOG_KEY</c> in <c>packaging/winlogrotate.nsi</c>, pinned against
/// <see cref="Names"/> by the installer name tests - and <c>EventLogWriter</c> asks for it before
/// it writes anything.
/// </para>
/// <para>
/// Platform-neutral on purpose: this composes a string and touches no registry, so the Linux leg
/// can pin that the writer looks exactly where the installer writes.
/// </para>
/// </remarks>
internal static class EventLogSourceKey
{
    /// <summary>The key every event log, and every source under it, lives beneath.</summary>
    public const string Root = @"SYSTEM\CurrentControlSet\Services\EventLog";

    /// <summary>The key that says <paramref name="source"/> is registered under <paramref name="log"/>.</summary>
    public static string PathFor(string log, string source) => $@"{Root}\{log}\{source}";
}
