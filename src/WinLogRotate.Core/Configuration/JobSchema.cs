using System.Globalization;
using Tomlyn.Syntax;

namespace WinLogRotate.Core.Configuration;

/// <summary>What shape a <c>[job]</c> key's value has.</summary>
public enum JobKeyKind
{
    /// <summary>A bare <c>true</c> or <c>false</c>.</summary>
    Flag,

    /// <summary>A bare whole number.</summary>
    Integer,

    /// <summary>A byte count, bare, or a quoted size such as <c>"100M"</c>.</summary>
    Size,

    /// <summary>A quoted duration: <c>30s</c>, <c>2h</c>, <c>01:00:00</c>.</summary>
    Duration,

    /// <summary>One of a closed set, quoted.</summary>
    Enum,

    /// <summary>A quoted string.</summary>
    Text,

    /// <summary>An array of quoted strings.</summary>
    TextList,
}

/// <summary>
/// The six groups <c>docs/configuration.md</c> presents the keys in.
/// </summary>
/// <remarks>
/// Carried here so a generated surface - a <c>--help</c> listing, a form - can present them the
/// way the reference does, rather than alphabetically, which shuffles the schedule, archiving and
/// behaviour keys together into a wall.
/// </remarks>
public enum JobKeyGroup
{
    /// <summary>Names or scopes one job, and so means nothing in <c>[defaults]</c>.</summary>
    Structural,

    Schedule,
    Retention,
    Archiving,
    Behaviour,
    Hooks,
}

/// <summary>What a bare number on a <see cref="JobKeyKind.Integer"/> key counts.</summary>
public enum JobKeyUnit
{
    /// <summary>Not a counted quantity: an index, a day of the week.</summary>
    None,

    /// <summary>A number of things: archives, files, attempts.</summary>
    Count,

    /// <summary>A number of days.</summary>
    Days,

    /// <summary>A number of milliseconds.</summary>
    Milliseconds,
}

/// <summary>One <c>[job]</c> key: what it is called, what it holds, and what it is for.</summary>
public sealed record JobKey
{
    /// <summary>The TOML spelling, which is not always the CLR property's.</summary>
    /// <remarks>
    /// <c>notifempty</c> binds to <c>NotIfEmpty</c> and <c>retryinterval</c> to
    /// <c>RetryIntervalMs</c>. Anything deriving a surface from the properties would offer
    /// <c>notIfEmpty</c>, which no file may contain - so this table is key-driven, never
    /// property-driven.
    /// </remarks>
    public required string Key { get; init; }

    public required JobKeyKind Kind { get; init; }

    public required JobKeyGroup Group { get; init; }

    /// <summary>The closed set, for <see cref="JobKeyKind.Enum"/>. Empty otherwise.</summary>
    public IReadOnlyList<string> Choices { get; init; } = [];

    /// <summary>
    /// A value that is not this key's default, for the tests that write every key.
    /// </summary>
    /// <remarks>
    /// On the row rather than in a dictionary beside it: a sample list kept separately is a
    /// second list, and a key added without one is a key nothing exercises.
    /// </remarks>
    public required string Sample { get; init; }

    /// <summary>
    /// What the key does for the person setting it, as a form's caption: "Old copies to keep".
    /// </summary>
    /// <remarks>
    /// The key name is for the file; this is for the operator, who should not have to know that
    /// logrotate called it <c>rotate</c>. Required, like <see cref="Sample"/>, so a key added
    /// without one is a key a form cannot caption.
    /// </remarks>
    public required string Title { get; init; }

    /// <summary>What the key means, in one sentence, with an example where a format matters.</summary>
    public required string Description { get; init; }

    /// <summary>
    /// The built-in value in the grammar <see cref="Sample"/> uses - <c>7</c>, <c>true</c>,
    /// <c>zip</c>, <c>1M</c> - or null when there is none: the key is required, or leaving it
    /// out means "not applied".
    /// </summary>
    /// <remarks>
    /// The binder's own defaults live in <c>BuiltInDefaults</c> and <c>SettingsMerge</c>; this
    /// column restates them for a surface that cannot bind a job to find out, and a test binds
    /// an empty job and holds the two to be the same value.
    /// </remarks>
    public string? Default { get; init; }

    /// <summary>What a bare number counts, for a unit label beside the field.</summary>
    public JobKeyUnit Unit { get; init; } = JobKeyUnit.None;

    /// <summary>
    /// The smallest value the validator accepts on an <see cref="JobKeyKind.Integer"/> key, or
    /// null when it refuses nothing. Only what <c>ConfigValidator</c> really enforces.
    /// </summary>
    public int? Min { get; init; }

    /// <summary>The largest value the validator accepts, or null when there is no ceiling.</summary>
    public int? Max { get; init; }

    /// <summary>True when the key describes a particular job rather than how any job behaves.</summary>
    public bool PerJobOnly => Group == JobKeyGroup.Structural;
}

/// <summary>
/// Every key a <c>[job]</c> table may carry, in one table.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ConfigBinder.JobKeys"/> is derived from this, and <c>DefaultsKeys</c> from that, for
/// the reason the second derivation already gave: a key added to one is never missing from the
/// other. The addition here is that the <i>type</i> travels with the key, so a writer knows what
/// <see cref="TomlValue"/> to build without guessing from the text - which is how
/// <c>rotate=30</c> becomes <c>"30"</c> and stops the machine.
/// </para>
/// <para>
/// It references nothing in <see cref="ConfigBinder"/> on purpose. The static initialiser order
/// that caught <c>DefaultsKeys</c> reading a null list runs the other way here, and a cycle
/// between the two would be a nastier version of the same bug.
/// </para>
/// </remarks>
public static class JobSchema
{
    /// <summary>Every key, in the order <c>docs/configuration.md</c> presents them.</summary>
    public static IReadOnlyList<JobKey> Keys { get; } =
    [
        Structural("name", JobKeyKind.Text, "sample-job", "Name",
            "Identifies the job in the history, in messages and in notifications; unique across all jobs."),
        Structural("paths", JobKeyKind.TextList, "C:/logs/sample*.log", "Which files",
            "One per line. * stands for anything in a name; ** also searches subfolders; [abc] one of those characters."),
        Choice("kind", JobKeyGroup.Structural, "manage", "Who rotates",
            "rotate: WinLogRotate moves the log aside. manage: the application starts new files itself; only tidy up.",
            "rotate", "rotate", "manage"),
        Structural("enabled", JobKeyKind.Flag, "false", "Enabled",
            "An unticked job is left alone entirely.", "true"),
        Structural("allowdangerous", JobKeyKind.TextList, "C:/Windows/SampleApp/logs", "Allowed protected folders",
            "Folders this job may work in although they are protected, such as under C:\\Windows."),

        Row("hourly", JobKeyKind.Flag, JobKeyGroup.Schedule, "true", "Every hour",
            "Shorthand for schedule = hourly; the last frequency written in the file wins."),
        Row("daily", JobKeyKind.Flag, JobKeyGroup.Schedule, "true", "Every day",
            "Shorthand for schedule = daily; the last frequency written in the file wins."),
        Row("weekly", JobKeyKind.Flag, JobKeyGroup.Schedule, "true", "Every week",
            "Shorthand for schedule = weekly; the last frequency written in the file wins."),
        Row("monthly", JobKeyKind.Flag, JobKeyGroup.Schedule, "true", "Every month",
            "Shorthand for schedule = monthly; the last frequency written in the file wins."),
        Row("yearly", JobKeyKind.Flag, JobKeyGroup.Schedule, "true", "Every year",
            "Shorthand for schedule = yearly; the last frequency written in the file wins."),
        Choice("schedule", JobKeyGroup.Schedule, "weekly", "How often",
            "By the calendar, or size to rotate only when the log reaches the size below.",
            "daily", "hourly", "daily", "weekly", "monthly", "yearly", "size"),
        Row("size", JobKeyKind.Size, JobKeyGroup.Schedule, "10M", "Rotate at size",
            "Under size scheduling, the size a log must reach to rotate; writing it selects that schedule.", "1M"),
        Row("weekday", JobKeyKind.Integer, JobKeyGroup.Schedule, "3", "Day of the week",
            "For weekly: 0 is Sunday through 6 Saturday; 7 means every seven days whatever the weekday.",
            "0", min: 0, max: 7),
        Row("monthday", JobKeyKind.Integer, JobKeyGroup.Schedule, "12", "Day of the month",
            "For monthly: the day of the month to rotate on.", "0", min: 0, max: 31),

        Row("rotate", JobKeyKind.Integer, JobKeyGroup.Retention, "30", "Old copies to keep",
            "How many old copies to keep; 0 keeps none, -1 keeps all and relies on 'Delete older than'.",
            "7", JobKeyUnit.Count, min: -1),
        Row("start", JobKeyKind.Integer, JobKeyGroup.Retention, "2", "First archive number",
            "The number the first old copy gets, so app.log.1.", "1", JobKeyUnit.Count, min: 0),
        Row("maxage", JobKeyKind.Integer, JobKeyGroup.Retention, "90", "Delete older than",
            "Delete old copies older than this many days, whatever 'Old copies to keep' says.",
            unit: JobKeyUnit.Days),
        Row("minage", JobKeyKind.Integer, JobKeyGroup.Retention, "2", "Skip logs younger than",
            "Do not rotate a log younger than this many days.", unit: JobKeyUnit.Days),
        Row("minsize", JobKeyKind.Size, JobKeyGroup.Retention, "1M", "Skip logs smaller than",
            "Do not rotate a log smaller than this, even when due. Sizes: 100k, 10M, 1G."),
        Row("maxsize", JobKeyKind.Size, JobKeyGroup.Retention, "100M", "Rotate early above",
            "Rotate early once the log exceeds this, whatever the calendar says. Sizes: 100k, 10M, 1G."),
        Row("maxfiles", JobKeyKind.Integer, JobKeyGroup.Retention, "5000", "Most files one line may match",
            "Refuse a line that matches more files than this; a huge match is usually a typo.",
            "1000", JobKeyUnit.Count, min: 1),

        Row("compress", JobKeyKind.Flag, JobKeyGroup.Archiving, "false", "Compress old copies",
            "Compress old copies to save space.", "true"),
        Choice("compresstype", JobKeyGroup.Archiving, "gzip", "Compression format",
            "zip opens in Explorer; gzip is what log tools expect; none is the same as not compressing.",
            "zip", "zip", "gzip", "none"),
        Row("delaycompress", JobKeyKind.Flag, JobKeyGroup.Archiving, "true", "Compress one cycle later",
            "Leave the newest old copy uncompressed for one cycle, for a program still writing to it.", "false"),
        Row("dateext", JobKeyKind.Flag, JobKeyGroup.Archiving, "true", "Date in archive names",
            "Name old copies by date (app.log-20260918) instead of by number (app.log.1).", "false"),
        Row("dateformat", JobKeyKind.Text, JobKeyGroup.Archiving, "-yyyy-MM-dd", "Date format",
            "The date stamp used with the option above: runs of y M d H m s, separated by - _ or .", "-yyyyMMdd"),
        Row("olddir", JobKeyKind.Text, JobKeyGroup.Archiving, "D:/archive", "Archive folder",
            "Where old copies go; a relative folder is taken from each log's own folder."),
        Row("createolddir", JobKeyKind.Flag, JobKeyGroup.Archiving, "true", "Create the archive folder",
            "Create the archive folder if it is missing; otherwise a missing folder stops the job.", "false"),

        Row("missingok", JobKeyKind.Flag, JobKeyGroup.Behaviour, "true", "Allow no matches",
            "Matching no files is fine, not an error.", "false"),
        Row("notify", JobKeyKind.Flag, JobKeyGroup.Behaviour, "false", "Report failures",
            "Report this job's failures through the configured notifications.", "true"),
        Row("notifempty", JobKeyKind.Flag, JobKeyGroup.Behaviour, "false", "Skip empty logs",
            "Leave an empty log alone instead of rotating it.", "true"),
        Choice("lockstrategy", JobKeyGroup.Behaviour, "copytruncate", "When the log is held open",
            "rename moves the file; copytruncate copies then empties it; copy only copies; auto probes and picks.",
            "rename", "rename", "copytruncate", "copy", "auto"),
        Row("livefiles", JobKeyKind.Integer, JobKeyGroup.Behaviour, "3", "Newest files to leave alone",
            "On a manage job, how many of the newest files to leave alone as still being written.",
            "1", JobKeyUnit.Count, min: 0),
        Row("retrycount", JobKeyKind.Integer, JobKeyGroup.Behaviour, "9", "Attempts on a locked file",
            "How many times to try an operation a locked file blocks; 1 means try once.",
            "5", JobKeyUnit.Count, min: 1),
        Row("retryinterval", JobKeyKind.Integer, JobKeyGroup.Behaviour, "250", "Wait between attempts",
            "Milliseconds to wait before trying again; the wait doubles each time.",
            "100", JobKeyUnit.Milliseconds, min: 0),

        Row("prerotate", JobKeyKind.TextList, JobKeyGroup.Hooks, "command:C:/tools/quiesce.exe", "Run before rotating",
            "Commands to run before a log is moved, one per line; a failure abandons the job."),
        Row("postrotate", JobKeyKind.TextList, JobKeyGroup.Hooks, "command:C:/tools/reload.exe", "Run after rotating",
            "Commands to run after the log has moved, one per line."),
        Row("hook_timeout", JobKeyKind.Duration, JobKeyGroup.Hooks, "90s", "Command time limit",
            "How long a command may take: 30s, 15m, 2h.", "60s"),
    ];

    /// <summary>The row for this key, or null if there is none.</summary>
    public static JobKey? Find(string key) =>
        Keys.FirstOrDefault(k => string.Equals(k.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The known key one edit away from what was written, if there is exactly one.</summary>
    /// <remarks>
    /// Only substitutions, insertions and deletions of a single character - enough for
    /// <c>postrotae</c> and <c>compres</c>, and not enough to confidently propose
    /// <c>postrotate</c> for <c>firstaction</c>, which is a different directive rather than a
    /// misspelling of this one.
    /// </remarks>
    public static string? Nearest(string key)
    {
        string? best = null;

        foreach (var known in Keys)
        {
            if (Math.Abs(known.Key.Length - key.Length) > 1 || !WithinOneEdit(key, known.Key))
            {
                continue;
            }

            if (best is not null)
            {
                return null;
            }

            best = known.Key;
        }

        return best;
    }

    /// <summary>
    /// Every key, in schema order, for a help string.
    /// </summary>
    /// <remarks>
    /// Generated rather than written out, so <c>--help</c> cannot fall behind the binder. That is
    /// not hypothetical tidiness: the list an operator reads is the list they will type against,
    /// and a key that exists but is undocumented is a key nobody uses.
    /// </remarks>
    public static string Summary { get; } = string.Join(", ", Keys.Select(k => k.Key));

    /// <summary>
    /// Turns text a caller typed into a value of the type this key is read back as.
    /// </summary>
    /// <remarks>
    /// The type comes from the row and never from the text. Guessing writes <c>rotate = "30"</c>,
    /// which the binder rejects, or <c>dateformat = 20240101</c>, a bare number for a key that
    /// must be a string.
    /// </remarks>
    /// <param name="problem">Why not, in a sentence that completes "'x' is not …".</param>
    public static bool TryParse(string key, string text, out TomlValue value, out string? problem)
    {
        value = default;
        problem = null;

        if (Find(key) is not { } row)
        {
            problem = Nearest(key) is { } near
                ? $"not a [job] key. Did you mean '{near}'?"
                : "not a [job] key. See docs/configuration.md for the list.";
            return false;
        }

        switch (row.Kind)
        {
            case JobKeyKind.Flag:
                if (!bool.TryParse(text, out var flag))
                {
                    problem = "not true or false.";
                    return false;
                }

                value = TomlValue.Of(flag);
                return true;

            case JobKeyKind.Integer:
                if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
                {
                    problem = "not a whole number.";
                    return false;
                }

                value = TomlValue.Of(number);
                return true;

            case JobKeyKind.Size:
                // Bare when it is bare, quoted when it carries a suffix - both are what GetSize
                // reads, and keeping the caller's spelling means "100M" stays "100M" in the file
                // rather than becoming 104857600.
                if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var bytes))
                {
                    value = TomlValue.Of(bytes);
                    return true;
                }

                if (!ConfigBinder.TryParseSize(text, out _))
                {
                    problem = "a byte count or a size such as \"100M\".";
                    return false;
                }

                value = TomlValue.Of(text);
                return true;

            case JobKeyKind.Duration:
                if (!ConfigBinder.TryParseDuration(text, out _))
                {
                    problem = "a duration. Write it as 30s, 15m, 2h, 7d, or as 01:00:00.";
                    return false;
                }

                value = TomlValue.Of(text);
                return true;

            case JobKeyKind.Enum:
                var chosen = row.Choices.FirstOrDefault(
                    c => string.Equals(c, text, StringComparison.OrdinalIgnoreCase));

                if (chosen is null)
                {
                    problem = $"one of {string.Join(", ", row.Choices)}.";
                    return false;
                }

                value = TomlValue.Of(chosen);
                return true;

            case JobKeyKind.TextList:
                // One item, or the array `job show` printed. A caller repeating the key builds
                // the list the ergonomic way; this never splits on a separator, because a Windows
                // path may contain any of them.
                value = TomlValue.List(ListItems(text));
                return true;

            default:
                value = TomlValue.Of(text);
                return true;
        }
    }

    /// <summary>
    /// The items one piece of command-line text names: one, unless it is an array of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// So that what <c>job show</c> prints goes back through <c>job set</c> unchanged. Without
    /// this, feeding back <c>paths = ["C:/logs/*.log"]</c> would write a one-item list whose item
    /// is the literal text <c>["C:/logs/*.log"]</c> - a job matching nothing, from a round trip
    /// that looked like it worked.
    /// </para>
    /// <para>
    /// Decided by parsing rather than by the brackets. <c>[</c> and <c>]</c> are legal in a
    /// Windows filename, so <c>[archive]</c> is a directory somebody has and not an array; it
    /// does not parse as TOML, and stays one item.
    /// </para>
    /// <para>
    /// Public because <see cref="JobDocument"/> has to accumulate a repeated list key across
    /// several pieces of text and cannot go through <see cref="TryParse"/>, which answers about
    /// one. Two spellings of "what items does this text name" is how the two would come apart.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> ListItems(string text)
    {
        var trimmed = text.Trim();

        if (trimmed.Length < 2 || trimmed[0] != '[' || trimmed[^1] != ']')
        {
            return [text];
        }

        // Through TomlFile, so this reads an array exactly as the rest of the product does.
        var document = TomlFile.Parse($"x = {trimmed}", "<value>");

        if (document.HasErrors
            || document.Document.KeyValues.GetChild(0) is not KeyValueSyntax { Value: ArraySyntax array })
        {
            return [text];
        }

        var items = new List<string>();

        for (var i = 0; i < array.Items.ChildrenCount; i++)
        {
            // Anything but a string means this is not a list this product writes - a number, or a
            // nested array - and reading part of it would be worse than reading none.
            if (array.Items.GetChild(i) is not { Value: StringValueSyntax { Value: { } item } })
            {
                return [text];
            }

            items.Add(item);
        }

        return items.Count == 0 ? [text] : items;
    }

    private static JobKey Row(
        string key, JobKeyKind kind, JobKeyGroup group, string sample, string title, string description,
        string? fallback = null, JobKeyUnit unit = JobKeyUnit.None, int? min = null, int? max = null) =>
        new()
        {
            Key = key,
            Kind = kind,
            Group = group,
            Sample = sample,
            Title = title,
            Description = description,
            Default = fallback,
            Unit = unit,
            Min = min,
            Max = max,
        };

    private static JobKey Structural(
        string key, JobKeyKind kind, string sample, string title, string description, string? fallback = null) =>
        Row(key, kind, JobKeyGroup.Structural, sample, title, description, fallback);

    private static JobKey Choice(
        string key, JobKeyGroup group, string sample, string title, string description, string fallback,
        params string[] choices) =>
        new()
        {
            Key = key,
            Kind = JobKeyKind.Enum,
            Group = group,
            Choices = choices,
            Sample = sample,
            Title = title,
            Description = description,
            Default = fallback,
        };

    private static bool WithinOneEdit(string a, string b)
    {
        if (a.Equals(b, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Walk both from the front until they diverge, then from the back. What is left in the
        // middle is the edit, and one edit means at most one character left on each side: a
        // substitution leaves one and one, an insertion one and none.
        int i = 0, j = a.Length - 1, k = b.Length - 1;

        while (i < a.Length && i < b.Length && char.ToLowerInvariant(a[i]) == char.ToLowerInvariant(b[i]))
        {
            i++;
        }

        while (j >= i && k >= i && char.ToLowerInvariant(a[j]) == char.ToLowerInvariant(b[k]))
        {
            j--;
            k--;
        }

        return j - i <= 0 && k - i <= 0;
    }
}
