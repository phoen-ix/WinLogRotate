namespace WinLogRotate.Contracts;

/// <summary>
/// Stable diagnostic identifiers. These appear in the Event Log, the journal, the GUI and
/// the documentation, so they are append-only: never renumber, never reuse.
/// </summary>
public static class DiagnosticCode
{
    // 1xxx - the run could not proceed as asked
    public const string NeedsAdministrator = "LR1001";
    public const string ConfigUnreadable = "LR1002";
    public const string ConfigInvalid = "LR1003";
    public const string NoJobsConfigured = "LR1004";

    /// <summary>The verb needs a platform this is not. Distinct from NeedsAdministrator, whose
    /// name would be a lie: no amount of elevation makes DPAPI exist on Linux.</summary>
    public const string NotSupportedHere = "LR1005";

    /// <summary>An exception escaped a verb. This is a defect in the product, not a problem
    /// with the machine or the configuration, and it is the only code that says so.</summary>
    public const string InternalError = "LR1006";

    /// <summary>
    /// The command line named something this verb could not use: a date it cannot read, a
    /// duration, a run model that is not one, a file that is not there.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="ConfigInvalid"/>, which means the machine's configuration is
    /// wrong and is documented as "nothing was attempted" about a night's rotation. These are
    /// about the words somebody just typed, they go to that person rather than to whoever
    /// maintains the configuration, and an alert rule watching event 113 should not fire because
    /// an operator mistyped a date at a prompt.
    /// <para>
    /// One code for the argument and the file it names, because they are one condition: the verb
    /// was given something it cannot work with, and nothing was attempted.
    /// </para>
    /// </remarks>
    public const string ArgumentUnusable = "LR1007";

    /// <summary>
    /// Another rotation holds the gate, so this one did nothing.
    /// </summary>
    /// <remarks>
    /// Info, not an error. <c>ExitCode.LockHeld</c>'s own summary calls it "the expected outcome
    /// when a manual run overlaps the scheduled one", and the registered task passes
    /// <c>--lock-held-exit 0</c> because of it. It exists so that the envelope for such a run says
    /// something: <c>ok</c> is false whenever the exit code is not zero, and an empty
    /// <c>diagnostics</c> beside it leaves a caller with a bare 3.
    /// </remarks>
    public const string AlreadyRunning = "LR1008";

    /// <summary>
    /// A verb failed and gave no reason. The backstop, and a defect wherever it appears.
    /// </summary>
    /// <remarks>
    /// <c>CliEnvelope.Diagnostics</c> promises "never empty on a failure", and under
    /// <c>--json</c> it is the only channel there is: <c>JsonOutputSink.Line</c> is a no-op. Nine
    /// paths broke that promise, and each of them is now fixed at the site. This is what catches
    /// the tenth.
    /// <para>
    /// Its wording is written to be embarrassing rather than informative, because an operator
    /// reading it has already been failed by something else. Seeing it in the wild is a bug
    /// report, not a diagnosis.
    /// </para>
    /// </remarks>
    public const string FailedWithoutReason = "LR1009";

    /// <summary>
    /// A configuration file could not be written, so it was left as it was.
    /// </summary>
    /// <remarks>
    /// The write half of <see cref="ConfigUnreadable"/>, which used to cover both - so "could not
    /// quarantine the unparseable file" and "config.toml could not be written" both raised the
    /// code documented as "the configuration could not be read", on the event an alert rule
    /// watches for exactly that.
    /// </remarks>
    public const string ConfigUnwritable = "LR1010";

    // 2xxx - a job or file was skipped
    public const string JobSkipped = "LR2001";
    public const string FileMissing = "LR2002";
    public const string FileEmpty = "LR2003";
    public const string NotDueYet = "LR2004";
    public const string FirstRunBaseline = "LR2005";

    // 3xxx - rotation errors
    public const string RotationFailed = "LR3001";
    public const string FileLocked = "LR3002";
    public const string StrategyUnavailable = "LR3003";
    /// <summary>A previous run was killed and left the rotation mutex abandoned.</summary>
    public const string PreviousRunAbandoned = "LR3101";
    /// <summary>copytruncate produced a NUL-filled file: the writer caches its own offset.
    /// The strategy is quarantined for this path permanently.</summary>
    public const string NulFillDetected = "LR3102";

    /// <summary>
    /// A hook was permitted, it ran, and it did not succeed - a non-zero exit, a timeout, an
    /// executable that is not there.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="HookRefused"/>, which means the gate said no. An alert rule has
    /// to be able to tell "somebody's configuration directory is writable" from "the reload script
    /// returned 1": the first is a security finding about the machine, the second is a broken
    /// script, and they go to different people. Same argument the 5xxx band's own comment makes
    /// for not folding a webhook failure into a rotation failure.
    /// </remarks>
    public const string HookFailed = "LR3103";

    /// <summary>
    /// The rotation clocks could not be written. The run happened; its record of it did not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately not <see cref="InternalError"/>, which this used to be by default because
    /// nothing caught it. That code promises "a defect in the product, not a problem with the
    /// machine or the configuration", and its remedy says nothing about what was or was not done
    /// can be relied on - both of which are false here. The rotations happened and are in the
    /// journal; what failed is a file write, which is the machine's business.
    /// </para>
    /// <para>
    /// An Error, because the consequence is real and arrives later: with no clock written, every
    /// log the run rotated is due again on the next one. A full disk is exactly when this fires,
    /// which is exactly when a second rotation of everything is least affordable.
    /// </para>
    /// </remarks>
    public const string StateNotSaved = "LR3105";

    /// <summary>
    /// The journal could not be opened or stopped accepting entries. The run carries on without it.
    /// </summary>
    /// <remarks>
    /// <c>docs/diagnostics.md</c> names three channels, each for a different reader, and the
    /// journal's is "whoever is asking what happened to a specific file". Losing it costs that
    /// reader and nobody else - so a rotation must not stop because its diary is full, which is
    /// what happened while <c>JournalWriter.Open</c> and <c>Write</c> were unguarded: a full disk
    /// aborted the run mid-rotation and reported <c>LR1006</c>.
    /// </remarks>
    public const string JournalUnavailable = "LR3106";

    /// <summary>
    /// The rotation clocks could not be read, so the run starts from a fresh baseline.
    /// </summary>
    /// <remarks>
    /// Reported as <see cref="ConfigUnreadable"/> until now, which put "your state file is
    /// corrupt" and "your configuration is broken" on one event ID. They are different problems
    /// with different costs: this one delays every log by an interval and fixes itself, and the
    /// other stops the machine.
    /// </remarks>
    public const string StateUnreadable = "LR3107";

    /// <summary>
    /// One rotation index is held by two files - <c>app.log.1</c> beside <c>app.log.1.gz</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both are kept and both shift. The planner will not choose between them, because neither
    /// can be shown to be the redundant one: <c>Compressor</c> stamps an archive with its source's
    /// own modification time, so a pair left by a compression that was interrupted between writing
    /// the archive and deleting the original is indistinguishable by date - and a pair left by a
    /// <c>delaycompress</c> chain that failed to shift is two genuinely different generations.
    /// </para>
    /// <para>
    /// Warning rather than Error: the rotation completes and nothing is lost. It is worth a line
    /// because the state is evidence that something earlier did not finish, and because a
    /// directory quietly carrying two spellings of every generation is not what the operator
    /// configured.
    /// </para>
    /// </remarks>
    public const string DuplicateGeneration = "LR3104";

    // 4xxx - host / scheduling
    public const string NoRunHost = "LR4001";
    public const string HostDrift = "LR4002";
    public const string HostRegistrationFailed = "LR4003";

    /// <summary>
    /// The release feed could not be reached, so whether an update exists is unknown.
    /// </summary>
    /// <remarks>
    /// <c>UpdateCommand</c>'s own comment says a failed check "must read as 'could not check',
    /// never as ... an error that a monitoring system would page someone about" - and then raised
    /// <see cref="ConfigUnreadable"/>, which maps to event 112 and tells the operator their
    /// configuration is broken. A network reached over the internet is the one thing here that is
    /// expected to be unavailable sometimes.
    /// </remarks>
    public const string UpdateCheckFailed = "LR4004";

    // 5xxx - notification delivery.
    //
    // Deliberately its own band rather than sharing 3xxx: a webhook that could not be reached is
    // not a rotation that failed, and an exit code or an alert rule keyed on the difference has
    // to be able to tell them apart.
    /// <summary>A notification target is unparseable, contradictory, or missing its credential.</summary>
    public const string NotifyMisconfigured = "LR5001";

    /// <summary>A channel could not be reached. Never an error: the rotation already happened,
    /// and its exit code must not depend on a webhook.</summary>
    public const string NotifyFailed = "LR5002";

    /// <summary>A channel is suppressed after repeated failures, so it was not attempted.</summary>
    public const string NotifyCircuitOpen = "LR5003";

    /// <summary>The notification state could not be read, so change detection starts over.</summary>
    public const string NotifyStateUnreadable = "LR5004";

    /// <summary>The notification phase was cut short, or skipped, to protect the run's
    /// deadline. See the remark on 0x41306 in docs/diagnostics.md.</summary>
    public const string NotifyBudgetClamped = "LR5005";

    /// <summary>
    /// A channel refused one message permanently, and will refuse it again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A non-retryable 4xx is about the message, not the channel - a digest too large for the
    /// endpoint, a job name a webhook template will not take. The channel is healthy and the rest
    /// of the run's messages go through it.
    /// </para>
    /// <para>
    /// It exists because the alternative was silence that never ended. An undelivered message
    /// does not advance its job's state, so the planner called the same incident new on every
    /// run and re-sent it to every healthy channel, for ever - which
    /// <c>HookDispatcher</c>'s remarks and <c>docs/notifications.md</c> both said the breaker
    /// prevented. It did not: the channel recorded a success every run, because one message
    /// getting through was counted as the channel working.
    /// </para>
    /// </remarks>
    public const string NotifyMessageRefused = "LR5006";

    // 9xxx - security. Never suppressed, never merely warned about.
    /// <summary>conf.d is writable by a non-administrator. All hooks and all
    /// dangerous-path overrides are refused for the entire run.</summary>
    public const string ConfigDirectoryInsecure = "LR9001";
    public const string DangerousPathRefused = "LR9002";
    public const string HookRefused = "LR9003";
    public const string ReparsePointRefused = "LR9004";

    /// <summary>A config file names a secret that is not in the store.</summary>
    public const string SecretMissing = "LR9005";

    /// <summary>A credential is written in a file every local user can read.</summary>
    public const string SecretInPlainConfig = "LR9006";

    /// <summary>The secret store, or the key protecting it, is not safe - or was not, and was
    /// repaired. Never silent: a repair nobody is told about is indistinguishable from a
    /// problem that never existed.</summary>
    public const string SecretStoreUnreadable = "LR9007";

    /// <summary>
    /// The secret store could not be written, so nothing was stored.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="InternalError"/>, which this was by default because nothing
    /// caught it: a full disk or a locked file is not "a defect in the product", and telling an
    /// operator that nothing about what was done can be relied on is wrong twice over here -
    /// <c>AtomicJson</c> writes a temporary sibling and moves it, so a failure leaves the previous
    /// contents exactly as they were and nothing is half written.
    /// <para>
    /// Distinct from <see cref="SecretStoreUnreadable"/>, which is about integrity - a store that
    /// cannot be decrypted, or a key that was repaired. Those go to different people: one is a
    /// disk, the other is a machine that may have been tampered with.
    /// </para>
    /// </remarks>
    public const string SecretStoreUnwritable = "LR9008";

    /// <summary>
    /// The rotation gate has been held by another process for longer than any rotation lasts, so
    /// nothing has rotated on this machine since.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In the 9xxx band - never suppressed, never merely warned about - because it is a
    /// machine-wide denial of rotation available to any unprivileged local account.
    /// <c>Global\WinLogRotate.Rotation</c> grants <c>Everyone</c> the right to synchronise on
    /// it, which is what lets the SYSTEM task and an unelevated GUI share one gate and also what
    /// lets anybody hold it. That entry cannot be narrowed: <c>SYNCHRONIZE</c> is the right to
    /// wait, and a satisfied wait is ownership.
    /// </para>
    /// <para>
    /// Distinct from <see cref="AlreadyRunning"/>, which is the same observation made once. An
    /// overlapping manual run is normal and Info; a gate held past
    /// <c>GateHoldRule.Implausible</c> is not a rotation, and it stops borrowing the scheduled
    /// task's <c>--lock-held-exit</c> - the option that exists so an overlap does not paint Last
    /// Run Result red, and which was quietly covering a machine where nothing ran at all.
    /// </para>
    /// </remarks>
    public const string RotationGateHeld = "LR9009";
}
