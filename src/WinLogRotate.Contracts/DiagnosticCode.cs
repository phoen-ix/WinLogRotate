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

    // 4xxx - host / scheduling
    public const string NoRunHost = "LR4001";
    public const string HostDrift = "LR4002";
    public const string HostRegistrationFailed = "LR4003";

    // 5xxx - notification delivery.
    //
    // Deliberately its own band rather than sharing 3xxx: a webhook that could not be reached is
    // not a rotation that failed, and an exit code or an alert rule keyed on the difference has
    // to be able to tell them apart.
    /// <summary>A notification target is unparseable, contradictory, or missing its credential.</summary>
    public const string NotifyMisconfigured = "LR5001";

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
}
