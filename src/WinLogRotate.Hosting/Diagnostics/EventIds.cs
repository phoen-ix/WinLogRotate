using WinLogRotate.Contracts;

namespace WinLogRotate.Hosting.Diagnostics;

/// <summary>
/// The stable mapping from a <see cref="DiagnosticCode"/> to a Windows event ID.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Append-only, and never renumbered.</strong> An alert rule cannot exist without a
/// documented event ID, and a table that changes between versions is worse than no table at
/// all: it silently stops matching, and the alert that was configured to page someone simply
/// never fires again. Same discipline as <see cref="DiagnosticCode"/> itself, for the same
/// reason. The published copy is in <c>docs/notifications.md</c>.
/// </para>
/// <para>
/// <strong>Every ID must be between 1 and 1000.</strong> That is not a style choice. The
/// installer registers <c>EventMessageFile = %SystemRoot%\System32\EventCreate.exe</c>
/// (packaging/winlogrotate.nsi), and that binary's message table defines exactly that range,
/// each entry being the single insertion string <c>%1</c>. An ID outside it renders in Event
/// Viewer as "The description for Event ID N ... cannot be found", which is how an operator
/// learns to distrust the whole log. The alternative - shipping a message DLL compiled with
/// mc.exe - buys categories nobody has asked for at the price of a Windows-only build step,
/// a third installed file and an uninstall path.
/// </para>
/// </remarks>
public static class EventIds
{
    /// <summary>A diagnostic whose code has no mapping. A test makes this unreachable for
    /// every code that exists; it is here so that adding one cannot crash a run.</summary>
    public const int Unclassified = 999;

    /// <summary>The whole range EventCreate.exe's message table covers.</summary>
    public const int MinId = 1;
    public const int MaxId = 1000;

    // Outcomes, which have no diagnostic code because nothing went wrong.
    public const int RunCompletedQuiet = 100;
    public const int RunCompletedWithChanges = 101;
    public const int RunCompletedWithFailures = 110;

    public static int For(string code) => code switch
    {
        // 1xxx - the run could not proceed as asked.
        DiagnosticCode.NeedsAdministrator => 111,
        DiagnosticCode.ConfigUnreadable => 112,
        DiagnosticCode.ConfigInvalid => 113,
        DiagnosticCode.NoJobsConfigured => 114,
        DiagnosticCode.NotSupportedHere => 115,

        // 2xxx - a job or file was skipped.
        DiagnosticCode.JobSkipped => 120,
        DiagnosticCode.FileMissing => 121,
        DiagnosticCode.FileEmpty => 122,
        DiagnosticCode.NotDueYet => 123,
        DiagnosticCode.FirstRunBaseline => 124,

        // 3xxx - rotation errors.
        DiagnosticCode.RotationFailed => 130,
        DiagnosticCode.FileLocked => 131,
        DiagnosticCode.StrategyUnavailable => 132,
        DiagnosticCode.PreviousRunAbandoned => 133,
        DiagnosticCode.NulFillDetected => 134,

        // 4xxx - host and scheduling.
        DiagnosticCode.NoRunHost => 140,
        DiagnosticCode.HostDrift => 141,
        DiagnosticCode.HostRegistrationFailed => 142,

        // 5xxx - notification delivery.
        DiagnosticCode.NotifyMisconfigured => 150,

        // 9xxx - security.
        DiagnosticCode.ConfigDirectoryInsecure => 190,
        DiagnosticCode.DangerousPathRefused => 191,
        DiagnosticCode.HookRefused => 192,
        DiagnosticCode.ReparsePointRefused => 193,
        DiagnosticCode.SecretMissing => 194,
        DiagnosticCode.SecretInPlainConfig => 195,
        DiagnosticCode.SecretStoreUnreadable => 196,

        _ => Unclassified,
    };

    /// <summary>
    /// The single insertion string for one diagnostic.
    /// </summary>
    /// <remarks>
    /// Everything goes into one string because EventCreate.exe's message table has exactly one
    /// insertion per entry. The code is repeated in the text even though it decides the event
    /// ID: an operator reading the message should not have to consult a table to know which
    /// condition they are looking at.
    /// </remarks>
    public static string Render(CliDiagnostic d)
    {
        var text = new System.Text.StringBuilder();

        if (d.Job is not null)
        {
            text.Append('[').Append(d.Job).Append("] ");
        }

        text.Append(d.Message);

        if (d.Path is not null)
        {
            text.Append("\r\n\r\nPath: ").Append(d.Path);
            if (d.Line is { } line)
            {
                text.Append('(').Append(line).Append(',').Append(d.Column ?? 1).Append(')');
            }
        }

        if (d.Remedy is not null)
        {
            text.Append("\r\n\r\n").Append(d.Remedy);
        }

        text.Append("\r\n\r\n").Append(d.Code);
        return text.ToString();
    }
}
