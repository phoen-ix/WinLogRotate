namespace WinLogRotate.Core.Notify;

/// <summary>The closed set of things a hook or a notification target can be.</summary>
public enum HookScheme
{
    /// <summary>Run a program. No shell, ever.</summary>
    Command,

    /// <summary>service:VERB:NAME - the Windows answer to kill -HUP.</summary>
    Service,

    /// <summary>event:Global\Name - signal a named kernel event.</summary>
    Event,

    /// <summary>http:// or https:// - the whole string is the address.</summary>
    Http,

    /// <summary>smtp:someone@example.com - the relay comes from the provider table.</summary>
    Smtp,

    /// <summary>pushover:USER-KEY - the application token comes from the provider table.</summary>
    Pushover,

    /// <summary>
    /// The Windows Event Log.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT <c>event:</c>, which the README already documents as signalling a named
    /// kernel event. Re-using it would silently change the meaning of a published directive, so
    /// the parser tests pin both spellings side by side.
    /// </remarks>
    EventLog,
}

/// <summary>Properties of a scheme, as opposed to of a particular target.</summary>
public static class HookSchemes
{
    /// <summary>
    /// Schemes that cause code to execute on this machine, and therefore need a configuration
    /// directory no non-administrator can write.
    /// </summary>
    /// <remarks>
    /// The reporting schemes are deliberately absent. Refusing those on a loose directory would
    /// remove the only channel able to tell anyone that the directory is loose - the alarm would
    /// be silenced by the condition it exists to report.
    /// </remarks>
    public static bool NeedsHardenedConfDir(HookScheme scheme) =>
        scheme is HookScheme.Command or HookScheme.Service or HookScheme.Event;

    /// <summary><c>eventlog:</c> alone is complete; the message is the notification.</summary>
    public static bool RequiresTarget(HookScheme scheme) => scheme is not HookScheme.EventLog;

    /// <summary>Whether the target is a URL, and so has to be redacted as a credential.</summary>
    public static bool TargetIsUrl(HookScheme scheme) => scheme is HookScheme.Http;

    public static string Name(HookScheme scheme) => scheme switch
    {
        HookScheme.Command => "command",
        HookScheme.Service => "service",
        HookScheme.Event => "event",
        HookScheme.Http => "http",
        HookScheme.Smtp => "smtp",
        HookScheme.Pushover => "pushover",
        HookScheme.EventLog => "eventlog",
        _ => scheme.ToString().ToLowerInvariant(),
    };
}
