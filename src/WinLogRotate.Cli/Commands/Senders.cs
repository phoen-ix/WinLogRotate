using System.Runtime.Versioning;
using WinLogRotate.Core;
using WinLogRotate.Core.Notify;
using WinLogRotate.Core.Notify.Delivery;
using WinLogRotate.Core.Secrets;
using WinLogRotate.Hosting;
using WinLogRotate.Hosting.Diagnostics;
using WinLogRotate.Hosting.Security;

namespace WinLogRotate.Cli.Commands;

/// <summary>
/// Builds the transports for one invocation, and owns the ones that hold a socket.
/// </summary>
/// <remarks>
/// <para>
/// This is where the four reporting schemes are assembled and where the three that execute code -
/// <c>command:</c>, <c>service:</c>, <c>event:</c> - are conspicuously absent. They are not
/// forgotten: they need the configuration directory to be one no ordinary account can write, and
/// they land with that gate. Leaving them out of the table means a target naming one gets a
/// refusal every run instead of silence, which is the difference between "not yet" and "broken".
/// </para>
/// <para>
/// The Event Log transport comes from Hosting because it is a P/Invoke into advapi32 and Core is
/// platform-neutral; off Windows it is simply not registered, so an <c>eventlog:</c> target on a
/// non-Windows machine is refused rather than silently accepted.
/// </para>
/// </remarks>
internal sealed class Senders : IDisposable
{
    private readonly List<IDisposable> _owned = [];

    private Senders(SenderTable table) => Table = table;

    public SenderTable Table { get; }

    public static Senders Build(NotifySettings settings)
    {
        var userAgent = $"{ProductInfo.Name}/{ProductInfo.Version}";

        var http = new HttpNotifySender(settings, userAgent);
        var pushover = new PushoverNotifySender(settings, userAgent);

        var map = new Dictionary<HookScheme, INotifySender>
        {
            [HookScheme.Http] = http,
            [HookScheme.Pushover] = pushover,
            [HookScheme.Smtp] = new SmtpNotifySender(),
        };

        var reasons = new Dictionary<HookScheme, string>();

        if (OperatingSystem.IsWindows())
        {
            AddEventLog(map);
        }
        else
        {
            // Said as a platform fact rather than a missing feature, so nobody goes looking for a
            // build that has it. The only place this is seen is the Linux test leg.
            reasons[HookScheme.EventLog] = "needs Windows";
        }

        var senders = new Senders(new SenderTable(map, reasons));
        senders._owned.Add(http);
        senders._owned.Add(pushover);

        return senders;
    }

    /// <summary>The secret platform for this machine, for resolving stored credentials.</summary>
    public static ISecretPlatform Platform() => SecretPlatform.ForThisMachine();

    [SupportedOSPlatform("windows")]
    private static void AddEventLog(Dictionary<HookScheme, INotifySender> map) =>
        map[HookScheme.EventLog] = new EventLogNotifySender(Names.EventLogSource);

    public void Dispose()
    {
        foreach (var item in _owned)
        {
            item.Dispose();
        }
    }
}
