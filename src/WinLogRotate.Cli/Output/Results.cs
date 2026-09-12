using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Notify;
using WinLogRotate.Core.State;
using WinLogRotate.Hosting.Hosts;
using WinLogRotate.Hosting.Security;

namespace WinLogRotate.Cli.Output;

/// <summary>Payload of a verb that returns no data. Registering a concrete type rather
/// than using <c>object</c> keeps the JSON source generator able to see everything.</summary>
public sealed record EmptyResult;

/// <summary>
/// Payload of <c>winlogrotate --version --json</c>.
/// <para>
/// The GUI runs this verb on start - it is the one verb that needs no configuration directory -
/// but it reads product and schema off the <b>envelope</b>, which carries both and carries them
/// even when a verb failed before it could build a payload. The copies here are for a person, or
/// for a script that extracts <c>.result</c> and passes it on. See
/// <c>WinLogRotate.Gui.Cli.CliIdentity</c>.
/// </para></summary>
public sealed record VersionResult
{
    public required string Product { get; init; }
    public required string Version { get; init; }
    public required int Schema { get; init; }
    public required string Runtime { get; init; }
    public required string Architecture { get; init; }

    /// <summary>
    /// True when the process that answered held an elevated token.
    /// </summary>
    /// <remarks>
    /// A fact about that process, for a person or a script asking "was that run elevated?" - and
    /// the only elevation answer available from a verb that reads no configuration at all. It is
    /// not what draws the UAC shield: the shield predicts whether <i>this window's</i> next
    /// action will raise a prompt, which is a question about the window's own token and has to be
    /// answerable before any child exists. See <c>LrDialog.AddShield</c>.
    /// </remarks>
    public required bool Elevated { get; init; }
}

/// <summary>Payload of <c>winlogrotate glob</c>.</summary>
public sealed record GlobResult
{
    public required string Pattern { get; init; }

    /// <summary>The directory the walk actually started from - the longest wildcard-free
    /// prefix. Shown because a surprising anchor is the usual cause of a surprising result.</summary>
    public required string Anchor { get; init; }

    /// <summary>
    /// Where the anchor really led, when a link took it somewhere else. Null otherwise.
    /// </summary>
    /// <remarks>
    /// For <see cref="Anchor"/>'s reason, one step further on: a surprising anchor is the usual
    /// cause of a surprising result, and an anchor that is a junction is the most surprising kind
    /// there is - it is the one case where the directory being walked is not the one that was
    /// typed.
    /// </remarks>
    public string? ResolvedAnchor { get; init; }

    public required int Count { get; init; }
    public required long TotalBytes { get; init; }
    public required IReadOnlyList<string> Files { get; init; }
}

/// <summary>Payload of <c>winlogrotate journal</c>.</summary>
public sealed record JournalResult
{
    public required string Directory { get; init; }
    public required int Count { get; init; }

    /// <summary>Lines that could not be parsed - normally a torn final line from a run that
    /// was killed. Surfaced rather than swallowed, because it is a clue.</summary>
    public required int SkippedLines { get; init; }

    /// <summary>Journal files that could not be opened, or could not be read to the end.</summary>
    /// <remarks>
    /// Distinct from <see cref="SkippedLines"/>. A torn line is a day read and found damaged; an
    /// unreadable file is a day that was never read at all, and the entries below are the other
    /// twenty-nine.
    /// </remarks>
    public required IReadOnlyList<string> UnreadableFiles { get; init; }

    public required IReadOnlyList<CliEvent> Entries { get; init; }
}

/// <summary>
/// A config diagnostic in wire form, located precisely enough to click.
/// </summary>
/// <remarks>
/// For an editor integration or a script. Not for this GUI: the Jobs page runs <c>config check</c>
/// without <c>--json</c> and shows the CLI's own rendered text, which is better output than a
/// hand-walked table of the same fields would be.
/// </remarks>
public sealed record ConfigDiagnosticDto
{
    public required Severity Severity { get; init; }
    public required string Code { get; init; }
    public required string Message { get; init; }
    public required string File { get; init; }
    public int Line { get; init; }
    public int Column { get; init; }
    public string? Remedy { get; init; }
}

/// <summary>Payload of <c>winlogrotate config check</c>.</summary>
public sealed record ConfigCheckResult
{
    public required string Root { get; init; }
    public required int Jobs { get; init; }
    public required int Errors { get; init; }
    public required int Warnings { get; init; }
    public required IReadOnlyList<ConfigDiagnosticDto> Diagnostics { get; init; }
}

/// <summary>Payload of <c>winlogrotate config show</c> - every default resolved.</summary>
public sealed record ConfigShowResult
{
    public required string Root { get; init; }
    public required IReadOnlyList<EffectiveJob> Jobs { get; init; }
}

/// <summary>Payload of <c>winlogrotate run</c>.</summary>
public sealed record RunResult
{
    /// <summary>Groups this run's entries in the journal.</summary>
    public required string RunId { get; init; }

    public required bool DryRun { get; init; }
    public required int JobsConsidered { get; init; }
    public required int JobsRun { get; init; }
    public required int Completed { get; init; }
    public required int Failed { get; init; }
    public required long BytesFreed { get; init; }
    public required IReadOnlyList<string> Errors { get; init; }

    /// <summary>
    /// Jobs that could not be loaded, so this run never attempted them.
    /// </summary>
    /// <remarks>
    /// In the envelope because the console line alone reached only a human reading stdout. A
    /// monitoring wrapper or a GUI parsing --json had no way to learn that a job had stopped
    /// rotating - which, since milestone 16 made this cost one job rather than the whole run, is
    /// the difference between a machine that is fine and one that is quietly not.
    /// </remarks>
    public IReadOnlyList<string> SkippedJobs { get; init; } = [];
}

/// <summary>Payload of <c>winlogrotate doctor</c>: every fact an operator would otherwise
/// gather from four different consoles.</summary>
public sealed record DoctorResult
{
    public required string Version { get; init; }
    public required InstallScope Scope { get; init; }
    public required string Root { get; init; }
    public required bool ConfigExists { get; init; }
    public required bool JobsDirectoryExists { get; init; }
    public required bool Elevated { get; init; }
    public required AclVerdict AclVerdict { get; init; }
    public required bool HooksAllowed { get; init; }
    public string? AclFix { get; init; }
    public required RunHostKind RunHost { get; init; }
    public required string RunHostDetail { get; init; }

    /// <summary>What notifications are configured to do. Reported without doing any of it.</summary>
    public required NotifyDoctorDto Notify { get; init; }
}

/// <summary>
/// The notification facts <c>doctor</c> can state without touching the network.
/// </summary>
/// <remarks>
/// Deliberately counts rather than names. Target strings are credentials - a webhook URL's entropy
/// is in its path - and this payload rides in an envelope the GUI polls, and is pasted into
/// support tickets.
/// </remarks>
public sealed record NotifyDoctorDto
{
    public required bool Enabled { get; init; }
    public required int Targets { get; init; }
    public string? Proxy { get; init; }
    public required bool CertificatePinned { get; init; }
    public required int StoredCredentials { get; init; }
    public required int SuppressedChannels { get; init; }

    /// <summary>
    /// Whether this install can write to the Windows Event Log: <c>writable</c>,
    /// <c>unregistered</c>, or <c>unsupported</c> off Windows.
    /// </summary>
    /// <remarks>
    /// Here rather than beside <c>RunHost</c> because <c>eventlog:</c> is a notification target,
    /// and because this is the one target whose delivery can be answered without touching the
    /// network - which is the whole constraint this section is built around.
    /// </remarks>
    public required string EventLog { get; init; }

    /// <summary>
    /// True when an unregistered source is a fault rather than the ordinary state. Creating one
    /// needs administrator, so the installer does it and only a per-machine install has one.
    /// </summary>
    public required bool EventLogExpected { get; init; }
}

/// <summary>
/// One stored secret, as <c>secret list</c> reports it.
/// </summary>
/// <remarks>
/// Everything here is metadata. There is deliberately no field that could hold a value, not
/// even a masked one: a payload type that cannot carry a secret cannot leak one, whatever a
/// future caller does with it. An architecture test asserts no SecretString appears in this file.
/// </remarks>
public sealed record SecretEntryDto
{
    public required string Name { get; init; }
    public DateTimeOffset? Created { get; init; }
    public DateTimeOffset? Updated { get; init; }
    public string? SetBy { get; init; }

    /// <summary>ok, UNREADABLE, or "?" when this account may not decrypt.</summary>
    public required string Status { get; init; }
}

/// <summary>Payload of <c>winlogrotate secret list</c>.</summary>
public sealed record SecretListResult
{
    public required string Path { get; init; }
    public required string Protection { get; init; }

    /// <summary>Hardened, Missing, TooOpen, Unknown or NotApplicable.</summary>
    public required string FileProtection { get; init; }
    public required IReadOnlyList<SecretEntryDto> Secrets { get; init; }
}

/// <summary>Payload of <c>secret set</c>, <c>remove</c>, <c>test</c> and <c>import</c>.</summary>
public sealed record SecretResult
{
    public required string Verb { get; init; }
    public required string Path { get; init; }

    /// <summary>The names acted on. Never a value, and never anything derived from one.</summary>
    public required IReadOnlyList<string> Names { get; init; }

    /// <summary>
    /// Character count, for <c>secret test</c> only.
    /// </summary>
    /// <remarks>
    /// A length and nothing else - no hash, not even a prefix, because a few bytes of SHA-256
    /// over a low-entropy password confirms an offline guess. The length is here because the
    /// reason that verb exists is spotting the trailing newline a shell appended.
    /// </remarks>
    public int? Length { get; init; }

    /// <summary>True when the entropy key was found readable by others and tightened.</summary>
    public bool EntropyRehardened { get; init; }
}

/// <summary>One configured target, as <c>notify show</c> reports it.</summary>
public sealed record NotifyTargetDto
{
    public required string Target { get; init; }

    /// <summary>Masked. A webhook URL's path is its credential.</summary>
    public required string Display { get; init; }

    /// <summary>
    /// How this target is reached: "smtp", "https", "eventlog", a provider kind, or "?".
    /// </summary>
    /// <remarks>
    /// Deliberately a lowercase string and not an enum, unlike the neighbouring fields. It is
    /// drawn from two vocabularies - a NotifyProviderKind for a named provider, a HookScheme for
    /// a literal target - and carries "?" for one that would not parse. A single enum cannot say
    /// all three, so this stays a display value with a stated set.
    /// </remarks>
    public required string Scheme { get; init; }
    public required bool Usable { get; init; }
    public string? Problem { get; init; }
}

/// <summary>One provider, with how it authenticates but never with what it authenticates using.</summary>
public sealed record NotifyProviderDto
{
    public required string Name { get; init; }
    public required NotifyProviderKind Kind { get; init; }
    public required bool Enabled { get; init; }
    public required string Target { get; init; }

    /// <summary>"none", "secret:name", "env:NAME", "command" or "literal" - never a value.</summary>
    public required string Credential { get; init; }

    /// <summary>True when this provider needs nothing stored anywhere.</summary>
    public required bool CredentialFree { get; init; }
}

/// <summary>Payload of <c>winlogrotate notify show</c>.</summary>
public sealed record NotifyShowResult
{
    public required bool Enabled { get; init; }
    public required NotifyOn On { get; init; }
    public required Severity Threshold { get; init; }
    public required string RemindAfter { get; init; }
    public required string Budget { get; init; }
    public required int Retries { get; init; }

    /// <summary>False when nothing could be sent even if something went wrong.</summary>
    public required bool WouldSend { get; init; }

    /// <summary>What the outbound connections will use, in words.</summary>
    public required string Proxy { get; init; }

    /// <summary>The certificate every channel must present, or null for the machine's trust store.</summary>
    public string? CertificatePin { get; init; }

    public required IReadOnlyList<NotifyTargetDto> Targets { get; init; }
    public required IReadOnlyList<NotifyProviderDto> Providers { get; init; }
}

/// <summary>One channel's answer to <c>notify test</c>.</summary>
public sealed record NotifyTestChannelDto
{
    public required string Channel { get; init; }
    public required string Display { get; init; }
    public required bool Ok { get; init; }
    public required long Milliseconds { get; init; }

    /// <summary>HTTP status, where the transport had one.</summary>
    public int? Status { get; init; }

    /// <summary>Already redacted.</summary>
    public string? Error { get; init; }

    /// <summary>True when a real run would skip this channel because its breaker is open.</summary>
    public required bool WouldBeSkipped { get; init; }
}

/// <summary>
/// Payload of <c>winlogrotate notify test</c>.
/// </summary>
/// <remarks>
/// There is deliberately no overall pass/fail flag and the verb always exits 0. A caller that
/// wants one reads <see cref="Failed"/>; encoding it in the exit code would make a webhook outage
/// look like a rotation failure to whatever ran the command.
/// </remarks>
public sealed record NotifyTestResult
{
    public required int Sent { get; init; }
    public required int Failed { get; init; }
    public required IReadOnlyList<NotifyTestChannelDto> Channels { get; init; }
}

/// <summary>What was last reported about one job.</summary>
public sealed record NotifyJobStatusDto
{
    public required string Job { get; init; }
    public required NotifyOutcome Outcome { get; init; }
    public DateTimeOffset? NotifiedAt { get; init; }
    public DateTimeOffset? FailingSince { get; init; }
}

/// <summary>One channel's circuit breaker.</summary>
public sealed record NotifyChannelStatusDto
{
    public required string Channel { get; init; }
    public required BreakerVerdict State { get; init; }
    public required int ConsecutiveFailures { get; init; }
    public required int SkipRunsRemaining { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset? LastAttempt { get; init; }
}

/// <summary>Payload of <c>notify status</c> and <c>notify reset</c>.</summary>
public sealed record NotifyStatusResult
{
    public required string Path { get; init; }
    public required IReadOnlyList<NotifyJobStatusDto> Jobs { get; init; }
    public required IReadOnlyList<NotifyChannelStatusDto> Channels { get; init; }
    public IReadOnlyList<string> Reset { get; init; } = [];
}

/// <summary>Payload of the <c>host</c> verbs.</summary>
public sealed record HostResult
{
    public required string Host { get; init; }
    public required string ConfigRoot { get; init; }
    public required string Scope { get; init; }
}

/// <summary>Payload of <c>winlogrotate probe</c>.</summary>
public sealed record ProbeResultDto
{
    public required string Path { get; init; }
    public required string Verdict { get; init; }
    public required string Explanation { get; init; }

    /// <summary>
    /// The best strategy this file actually supports.
    /// </summary>
    /// <remarks>
    /// For whoever is choosing a <c>lockstrategy</c> for a job - at a prompt, or in a script that
    /// probes a path before writing a configuration. Nothing in the window runs this verb.
    /// </remarks>
    public string? Suggested { get; init; }

    public int BlockingError { get; init; }
    public string? BlockingErrorText { get; init; }

    /// <summary>
    /// True when nothing else holds the file open at all.
    /// </summary>
    /// <remarks>
    /// <c>ProbeResult</c> has computed this since the probe existed and nothing carried it out, so
    /// the one fact that distinguishes "the writer permits renaming" from "there is no writer" was
    /// thrown away. It is also what lets a test prove it measured a genuinely contended file
    /// rather than one whose holder had quietly died.
    /// </remarks>
    public bool Unlocked { get; init; }
}

/// <summary>Payload of <c>winlogrotate import</c>.</summary>
public sealed record ImportResult
{
    public required string Source { get; init; }
    public required string OutputDirectory { get; init; }
    public required int Jobs { get; init; }

    /// <summary>Jobs containing something that could not be translated. Each is written
    /// disabled, with the untranslatable part preserved as a TODO comment.</summary>
    public required int NeedingReview { get; init; }

    public required IReadOnlyList<string> Files { get; init; }
}

/// <summary>Payload of <c>winlogrotate host export-task</c>.</summary>
public sealed record ExportTaskResult
{
    public required string Xml { get; init; }
}

/// <summary>One producer found by <c>winlogrotate scan</c>.</summary>
public sealed record ScanFinding
{
    public required string Producer { get; init; }
    public required string Directory { get; init; }
    public required string Pattern { get; init; }
    public required bool SelfRotates { get; init; }

    /// <summary>Almost always false, and that is the point of the whole verb.</summary>
    public required bool SelfDeletes { get; init; }

    public required string SuggestedKind { get; init; }
    public string? Note { get; init; }
    public int FileCount { get; init; }
    public long TotalBytes { get; init; }
}

/// <summary>Payload of <c>winlogrotate scan</c>.</summary>
public sealed record ScanResult
{
    public required IReadOnlyList<ScanFinding> Findings { get; init; }
}

/// <summary>Payload of <c>winlogrotate host pause</c>.</summary>
public sealed record PauseResult
{
    public required string PausedUntil { get; init; }
}

/// <summary>Payload of the <c>update</c> verbs.</summary>
public sealed record UpdateResult
{
    public required string Current { get; init; }
    public string? Latest { get; init; }
    public required bool UpdateAvailable { get; init; }
    public string? Detail { get; init; }
}
