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

    // ---- how the log is taken away ---------------------------------------------------------

    public const string HowAuto = "Let WinLogRotate check what the program allows (recommended)";

    public const string HowAutoHint =
        "Renamed when the program allows it, otherwise copied out and emptied in place. "
        + "If the program never reopens its log, choose the next answer.";

    public const string HowCopyTruncate = "The program keeps the log open and never starts a new file";

    public const string HowCopyTruncateHint =
        "The contents are copied to the old copy and the file is emptied in place; the program never "
        + "notices. A few lines written during the copy can be lost.";

    public const string HowRename = "The program closes the log between writes, or reopens it after it moves";

    public const string HowRenameHint = "The file is renamed and a fresh, empty one is created for the program to find.";

    public const string HowManage = "The program already writes new files itself (IIS does); only tidy up";

    public const string HowManageHint = "The newest file is never touched; older ones are compressed and deleted.";

    public const string HowCopy = "Copy the log and leave it as it is (it keeps growing)";

    public const string HowCopyHint =
        "A snapshot goes to the old copy; the file itself is never renamed or emptied. For a "
        + "program that trims its own log and must never lose a line.";

    public const string HowLocked = "This job's way of taking the log away is not one of the answers above.";

    public const string NamesNumbered = "Numbered: app.log.1, app.log.2 \u2026";

    public const string NamesDated = "Dated: app.log-20260918";

    public const string CompressZip = "zip (opens in Explorer)";

    public const string CompressGzip = "gzip (what log tools expect)";

    public const string CompressNone = "not compressed";

    public const string OldDirPlaceholder = "beside the log";

    public const string CreateOldDir = "create it if missing";

    public const string PreviewButton = "What would happen\u2026";

    public const string PreviewSaveFirst = "Save first: the preview runs the job as it is on disk.";

    public const string PreviewTitle = "What would happen";

    public const string EarlyLead = "or earlier once it exceeds";

    public const string WhenManaged = "The application decides when a new file starts; each run tidies what it left.";

    public const string WhenBySize = "Rotates only when the log reaches the size set under More settings, never by the calendar.";

    public const string KeepLead = "old copies; delete any older than";

    public const string KeepUnit = "days";

    public const string CheckTooltip = "Tries the job out and writes nothing.";

    public const string SaveTooltip =
        "Writes the job to the configuration folder, which only administrators may change, "
        + "so Windows asks for permission once.";

    public const string MoreDivider = "More settings";

    public const string OpeningStatus = "Pick a log file with Browse, or type its path. Everything else has a sensible default.";

    public const string NoName = "Give the job a name. It appears in the history and in every message about it.";

    public const string NoFiles = "Pick a log file with Browse, or type its path.";

    public const string InheritLink = "Inherit";

    public const string RemoveLink = "Remove";

    public const string InheritedTooltip =
        "The built-in default; a [defaults] table in config.toml overrides it for every job.";

    public const string FirstJobFollowUp = "Run > Dry run shows what it would do, without changing anything.";
}
