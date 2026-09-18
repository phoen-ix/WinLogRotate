namespace WinLogRotate.Gui.Cli;

/// <summary>
/// Every sentence the job editor shows, in one place a test can read.
/// </summary>
/// <remarks>
/// The rule these follow: say what a thing does for the person, never what the file calls it;
/// give an example where a format matters; and never the words glob or pattern, which mean
/// nothing to most people who open this window.
/// </remarks>
public static class JobEditorText
{
    public const string NamePlaceholder = "e.g. iis";

    public const string FilesHint = "One line per file. * stands for anything in a name; ** also searches subfolders.";

    public const string Browse = "Browse…";

    public const string RotateRadio = "WinLogRotate moves the log aside";

    public const string RotateHint = "The live log is renamed so the program starts a fresh one.";

    public const string ManageRadio = "The application already starts new files (IIS does); only tidy up";

    public const string ManageHint = "The newest file is never touched; older ones are compressed and deleted.";

    public const string EarlyLead = "or earlier once it exceeds";

    public const string WhenHint = "Default: every day, no size limit. Sizes: 100k, 10M, 1G.";

    public const string WhenManaged = "The application decides when a new file starts; each run tidies what it left.";

    public const string WhenBySize = "This job rotates by size only; change that under Advanced settings.";

    public const string KeepLead = "old copies; delete any older than";

    public const string KeepUnit = "days";

    public const string KeepHint = "Default: keep 7 and never delete by age. Old copies are zipped.";

    public const string ButtonsHint =
        "Check tries the job out and writes nothing. Save writes it to the configuration folder, "
        + "which only administrators may change, so Windows asks for permission once.";

    public const string OpeningStatus = "Pick a log file with Browse, or type its path. Everything else has a sensible default.";

    public const string NoName = "Give the job a name. It appears in the history and in every message about it.";

    public const string NoFiles = "Pick a log file with Browse, or type its path.";

    public const string InheritLink = "Inherit";

    public const string RemoveLink = "Remove";

    public const string ForeignSection = "Other keys in this file";

    public const string InheritedTooltip =
        "The built-in default; a [defaults] table in config.toml overrides it for every job.";

    public const string FirstJobFollowUp = "Run > Dry run shows what it would do, without changing anything.";
}
