using System.Text.Json;
using Tomlyn.Syntax;
using WinLogRotate.Core.Configuration;

namespace WinLogRotate.Gui.Cli;

/// <summary>One key, as an editor offers it.</summary>
public sealed record JobField
{
    public required string Key { get; init; }

    /// <summary>What shape the value has, which decides what control edits it.</summary>
    public required JobKeyKind Kind { get; init; }

    /// <summary>Which section of the form it belongs in.</summary>
    public required JobKeyGroup Group { get; init; }

    /// <summary>The values an enum key accepts. Empty for every other kind.</summary>
    public required IReadOnlyList<string> Choices { get; init; }

    /// <summary>An example, for the placeholder text.</summary>
    public required string Sample { get; init; }

    /// <summary>
    /// What the file says, in a form a person can edit - or null when the file says nothing.
    /// </summary>
    /// <remarks>
    /// Unquoted, because a text box holding <c>"iis"</c> with the quotes showing is a text box
    /// somebody will delete the quotes from and then wonder why nothing changed. A list's items
    /// are one per line, which is what a multiline box shows and what the save path splits back.
    /// </remarks>
    public string? Value { get; init; }

    /// <summary>The line it is written on, or 0 when it is not written at all.</summary>
    public int Line { get; init; }

    /// <summary>
    /// The key that decided this value when it was not this key: <c>daily</c> for a schedule the
    /// file spells as <c>daily = true</c>, <c>size</c> for one it spells as a threshold. Null when
    /// the value is the key's own.
    /// </summary>
    public string? Via { get; init; }

    /// <summary>
    /// Whether the file says this, rather than inheriting it.
    /// </summary>
    /// <remarks>
    /// The distinction the whole editor turns on. A field showing a value it inherited, saved
    /// back, stops inheriting - so an editor that cannot tell the two apart converts every
    /// <c>[defaults]</c> value into a per-job one the first time somebody presses Save.
    /// </remarks>
    public bool IsSet => Value is not null;

    /// <summary>
    /// Whether this product reads this key.
    /// </summary>
    /// <remarks>
    /// False for a key somebody left by hand, or one from a newer schema than this build knows.
    /// Such a key can be removed but not written, so the form shows it and offers only Clear.
    /// </remarks>
    public required bool Known { get; init; }
}

/// <summary>A job, ready to be put on a form.</summary>
public sealed record JobEditorView
{
    /// <summary>Empty for a job that does not exist yet.</summary>
    public required string Job { get; init; }

    public required string Path { get; init; }

    /// <summary>Every key this product knows, plus any the file has that it does not.</summary>
    public required IReadOnlyList<JobField> Fields { get; init; }

    /// <summary>What a run would say about this file, worded for a person.</summary>
    public required IReadOnlyList<string> Problems { get; init; }

    /// <summary>The response made no sense, so the fields say nothing rather than nothing much.</summary>
    public required bool Unreadable { get; init; }

    /// <summary>
    /// Why <c>job show</c> would not read the job, or null when it did.
    /// </summary>
    /// <remarks>
    /// A refusal completes with no payload and its reason on the envelope - the name is not a
    /// job, and here are the ones that are. That is a readable answer, not a version mismatch,
    /// and the editor has to be able to tell the two apart to say the right thing about either.
    /// </remarks>
    public string? Refusal { get; init; }
}

/// <summary>One group of the Advanced view: a heading and the fields under it.</summary>
public sealed record JobSection(JobKeyGroup Group, string Title, IReadOnlyList<JobField> Fields);

/// <summary>Which control edits a field.</summary>
public enum FieldEditor
{
    /// <summary>A two-state tick box; the row remembers whether it was touched.</summary>
    Check,

    /// <summary>A drop-down list with an inherit entry first.</summary>
    Choice,

    /// <summary>A text box that must hold a whole number.</summary>
    Number,

    /// <summary>A text box.</summary>
    Text,

    /// <summary>A multi-line text box, one item per line.</summary>
    Lines,
}

/// <summary>Everything a form needs to draw one field: what to call it, how to explain it, what to show when it says nothing.</summary>
public sealed record FieldPresentation
{
    /// <summary>The caption in the operator's words.</summary>
    public required string Caption { get; init; }

    /// <summary>The key as the file spells it, shown small beside the caption.</summary>
    public required string Key { get; init; }

    public required string Description { get; init; }

    /// <summary>The built-in value, or null when leaving the key out means "not applied".</summary>
    public string? Default { get; init; }

    /// <summary><c>days</c>, <c>ms</c>, or null.</summary>
    public string? Unit { get; init; }

    /// <summary>What an empty box shows: the default, or an example when there is none.</summary>
    public required string Placeholder { get; init; }

    public required FieldEditor Editor { get; init; }

    public required IReadOnlyList<string> Choices { get; init; }

    /// <summary>For a tick box: whether the inherited value is ticked.</summary>
    public bool InheritedChecked { get; init; }
}

/// <summary>
/// How a log is taken away from the program that writes it, as the Basics view asks it.
/// </summary>
/// <remarks>
/// Four of the five are <c>lockstrategy</c> values and one is <c>kind = manage</c>, because
/// that is how the person thinks about it: "the program already writes new files itself" is an
/// answer to the same question as "the program keeps the file open". Every strategy has an
/// answer, because these radios are the strategy's only home on the form.
/// </remarks>
public enum HowRotated
{
    /// <summary>Probe the file and take the best the writer permits (<c>lockstrategy = auto</c>).</summary>
    Auto,

    /// <summary>Copy the contents out and empty the file in place (<c>copytruncate</c>).</summary>
    CopyTruncate,

    /// <summary>Rename the file and create a fresh one (<c>rename</c>).</summary>
    Rename,

    /// <summary>The application starts new files itself; only tidy up (<c>kind = manage</c>).</summary>
    Manage,

    /// <summary>A snapshot goes to the old copy and the log is left as it is, growing
    /// (<c>copy</c>). The rarest answer, last so the four common ones keep their places.</summary>
    Copy,
}

/// <summary>The pages of the job editor, in the order the menu lists them.</summary>
public enum JobPageId
{
    Files,
    How,
    When,
    Keep,
    Copies,
    Hooks,
    Wrong,

    /// <summary>Keys the file writes that this build does not read. Present only when there are some.</summary>
    Other,
}

/// <summary>
/// One page of the editor: what the menu calls it, what its title says, and the settings it
/// shows as typed rows under its own controls.
/// </summary>
/// <remarks>
/// Every known key has exactly one page and one home on it - a plain-language control, or a
/// row under "More settings" - which is what lets the form drop the Basics/Advanced views and
/// the carrying of values between them. <see cref="JobEditorModel.PageOf"/> is the table.
/// </remarks>
public sealed record JobPage(JobPageId Id, string Title, string Menu, IReadOnlyList<JobField> More);

/// <summary>Whether and how old copies are compressed, as the Basics view asks it.</summary>
public enum ArchiveCompression
{
    Zip,
    Gzip,
    None,
}

/// <summary>What the Basics view asked, ready to be turned into edits.</summary>
/// <param name="How">The radio, or null when the file's strategy has no radio and is left alone.</param>
/// <param name="Schedule">The combo's item, or null for the default entry.</param>
/// <param name="Dated">Old copies named by date rather than by number.</param>
/// <param name="OldDir">The folder old copies go to, or blank for beside the log.</param>
public sealed record BasicsAnswers(
    HowRotated? How,
    string? Schedule,
    string? MaxSize,
    string? Rotate,
    string? MaxAge,
    bool Dated,
    ArchiveCompression Compression,
    string? OldDir,
    bool CreateOldDir);

/// <summary>
/// The decisions a job editor makes, with nothing that needs a window.
/// </summary>
/// <remarks>
/// <para>
/// Two questions, and they are the whole form: given <c>job show --json</c>, which fields are set
/// here and which are inherited; and given what somebody typed, what command line says that. The
/// second is the testable one - <c>JobEditorModelTests</c> parses what this produces against the
/// real <c>CommandTree</c>, so a form that would have built an invocation the CLI refuses fails
/// here rather than in front of an operator.
/// </para>
/// <para>
/// Here rather than on the form because a project referencing <c>WinLogRotate.Gui</c> carries a
/// Microsoft.WindowsDesktop.App framework reference and cannot be built on the Linux leg at all.
/// Every decision left on the form is a decision no test can see.
/// </para>
/// </remarks>
public static class JobEditorModel
{
    /// <summary>The keys whose value is a command this product would run.</summary>
    /// <remarks>
    /// Named here rather than derived from <see cref="JobKeyGroup.Hooks"/>, because
    /// <c>hook_timeout</c> is in that group and is a duration, not a command - warning that a
    /// timeout will never run would be nonsense.
    /// </remarks>
    public static IReadOnlyList<string> Hooks { get; } = ["prerotate", "postrotate"];

    /// <summary>The keys the header above both views carries: the job's identity and its files.</summary>
    public static IReadOnlyList<string> Header { get; } = ["name", "paths", "enabled"];

    /// <summary>
    /// The keys the Basics view asks about, besides <c>kind</c>, which it asks as a pair of radios.
    /// </summary>
    /// <remarks>
    /// Chosen, not derived: how often, an early trigger, how many to keep and for how long are the
    /// decisions somebody adding a log file has to make. Everything else has a default the summary
    /// sentence states. The Advanced view offers these too - Basics is a subset, never a third place.
    /// </remarks>
    public static IReadOnlyList<string> Basics { get; } =
    [
        "schedule", "maxsize", "rotate", "maxage",
        "lockstrategy", "dateext", "compress", "compresstype", "olddir", "createolddir",
    ];

    /// <summary>
    /// The five keys that spell a schedule as a flag. Never shown: the schedule field shows what
    /// they decided, and a change to it retires them.
    /// </summary>
    public static IReadOnlyList<string> Shorthands { get; } = ["hourly", "daily", "weekly", "monthly", "yearly"];

    /// <summary>The Advanced view's sections: every known key the header does not carry, grouped and in schema order.</summary>
    public static IReadOnlyList<JobSection> Sections(JobEditorView view) =>
        [.. Enum.GetValues<JobKeyGroup>()
            .Select(group => new JobSection(
                group,
                group == JobKeyGroup.Structural ? "Job" : group.ToString(),
                [.. view.Fields.Where(f =>
                    f.Known
                    && f.Group == group
                    && !Header.Contains(f.Key, StringComparer.OrdinalIgnoreCase)
                    && !Shorthands.Contains(f.Key, StringComparer.OrdinalIgnoreCase))]))
            .Where(section => section.Fields.Count > 0)];

    /// <summary>Keys the file writes that this build does not read. They can be seen and removed, nothing else.</summary>
    public static IReadOnlyList<JobField> Foreign(JobEditorView view) =>
        [.. view.Fields.Where(f => !f.Known)];

    /// <summary>The keys a page asks with a control of its own rather than a typed row.</summary>
    /// <remarks><c>kind</c> and the Basics keys: the How radios stand for both <c>kind</c> and
    /// <c>lockstrategy</c>.</remarks>
    public static IReadOnlyList<string> Controls { get; } = ["kind", .. Basics];

    private static readonly IReadOnlyDictionary<string, JobPageId> PageTable =
        new Dictionary<string, JobPageId>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = JobPageId.Files,
            ["paths"] = JobPageId.Files,
            ["enabled"] = JobPageId.Files,
            ["allowdangerous"] = JobPageId.Files,

            ["kind"] = JobPageId.How,
            ["lockstrategy"] = JobPageId.How,
            ["livefiles"] = JobPageId.How,
            ["retrycount"] = JobPageId.How,
            ["retryinterval"] = JobPageId.How,

            ["schedule"] = JobPageId.When,
            ["hourly"] = JobPageId.When,
            ["daily"] = JobPageId.When,
            ["weekly"] = JobPageId.When,
            ["monthly"] = JobPageId.When,
            ["yearly"] = JobPageId.When,
            ["maxsize"] = JobPageId.When,
            ["size"] = JobPageId.When,
            ["weekday"] = JobPageId.When,
            ["monthday"] = JobPageId.When,

            ["rotate"] = JobPageId.Keep,
            ["maxage"] = JobPageId.Keep,
            ["start"] = JobPageId.Keep,
            ["minage"] = JobPageId.Keep,
            ["minsize"] = JobPageId.Keep,
            ["maxfiles"] = JobPageId.Keep,

            ["compress"] = JobPageId.Copies,
            ["compresstype"] = JobPageId.Copies,
            ["delaycompress"] = JobPageId.Copies,
            ["dateext"] = JobPageId.Copies,
            ["dateformat"] = JobPageId.Copies,
            ["olddir"] = JobPageId.Copies,
            ["createolddir"] = JobPageId.Copies,

            ["prerotate"] = JobPageId.Hooks,
            ["postrotate"] = JobPageId.Hooks,
            ["hook_timeout"] = JobPageId.Hooks,

            ["missingok"] = JobPageId.Wrong,
            ["notifempty"] = JobPageId.Wrong,
            ["notify"] = JobPageId.Wrong,
        };

    /// <summary>
    /// The page a key lives on.
    /// </summary>
    /// <remarks>
    /// An explicit table, not the schema's group: the group says what a key is about, the page
    /// says where a person looks for it, and those differ - <c>livefiles</c> is behaviour, but
    /// somebody asking "how is the log taken away" is where it belongs. A key the table does not
    /// name is on the Other page, and <c>EveryKnownKeyIsOnExactlyOnePage</c> makes sure that is
    /// only ever a key this build does not know.
    /// </remarks>
    public static JobPageId PageOf(string key) =>
        PageTable.TryGetValue(key, out var page) ? page : JobPageId.Other;

    public static string Title(JobPageId page) => page switch
    {
        JobPageId.Files => "Which files",
        JobPageId.How => "How the log is taken away",
        JobPageId.When => "How often",
        JobPageId.Keep => "How many old copies to keep",
        JobPageId.Copies => "Old copies",
        JobPageId.Hooks => "Before and after",
        JobPageId.Wrong => "If things go wrong",
        _ => "Other keys in this file",
    };

    public static string Menu(JobPageId page) => page switch
    {
        JobPageId.Files => "Files",
        JobPageId.How => "How",
        JobPageId.When => "When",
        JobPageId.Keep => "Keep",
        JobPageId.Copies => "Old copies",
        JobPageId.Hooks => "Before & after",
        JobPageId.Wrong => "If things go wrong",
        _ => "Other keys",
    };

    /// <summary>
    /// The editor's pages for this job: every known key once, on the page the table names, as a
    /// control or as a row under it; and the Other page when the file has keys of its own.
    /// </summary>
    public static IReadOnlyList<JobPage> Pages(JobEditorView view)
    {
        var pages = new List<JobPage>();

        foreach (var id in Enum.GetValues<JobPageId>())
        {
            if (id == JobPageId.Other)
            {
                if (Foreign(view).Count > 0)
                {
                    pages.Add(new JobPage(id, Title(id), Menu(id), []));
                }

                continue;
            }

            pages.Add(new JobPage(id, Title(id), Menu(id), [.. view.Fields.Where(f =>
                f.Known
                && PageOf(f.Key) == id
                && !Header.Contains(f.Key, StringComparer.OrdinalIgnoreCase)
                && !Shorthands.Contains(f.Key, StringComparer.OrdinalIgnoreCase)
                && !Controls.Contains(f.Key, StringComparer.OrdinalIgnoreCase))]));
        }

        return pages;
    }

    /// <summary>What the menu shows for a page, marked when one of its settings is refused.</summary>
    public static string MenuText(JobPage page, bool hasProblem) =>
        hasProblem ? $"{page.Menu} \u25CF" : page.Menu;

    /// <summary>A problem as the status line says it: which page, then the words.</summary>
    public static string ProblemLine(JobPage page, string problem) => $"{page.Menu}: {problem}";

    /// <summary>How to draw one field.</summary>
    public static FieldPresentation Presentation(JobField field)
    {
        var row = field.Known ? JobSchema.Find(field.Key) : null;

        if (row is null)
        {
            return new FieldPresentation
            {
                Caption = field.Key,
                Key = field.Key,
                Description = "Not a setting this product reads.",
                Placeholder = string.Empty,
                Editor = FieldEditor.Text,
                Choices = [],
            };
        }

        return new FieldPresentation
        {
            Caption = row.Title,
            Key = row.Key,
            Description = row.Description,
            Default = row.Default,
            Unit = row.Unit switch
            {
                JobKeyUnit.Days => "days",
                JobKeyUnit.Milliseconds => "ms",
                _ => null,
            },
            Placeholder = row.Default ?? $"e.g. {row.Sample}",
            Editor = row.Kind switch
            {
                JobKeyKind.Flag => FieldEditor.Check,
                JobKeyKind.Enum => FieldEditor.Choice,
                JobKeyKind.Integer => FieldEditor.Number,
                JobKeyKind.TextList => FieldEditor.Lines,
                _ => FieldEditor.Text,
            },
            Choices = row.Choices,
            InheritedChecked = string.Equals(row.Default, "true", StringComparison.OrdinalIgnoreCase),
        };
    }

    /// <summary>Where a field's value comes from, as the label beside it says it.</summary>
    public static string SourceLabel(JobField field, string? now)
    {
        if (!field.Known)
        {
            return "not a setting this product reads";
        }

        if (!Unchanged(field, now))
        {
            return Blank(now) ? "will inherit again" : "will be set here";
        }

        if (field.IsSet)
        {
            return field.Via switch
            {
                null => $"set here (line {field.Line})",
                "size" => $"set here (line {field.Line}, by size)",
                var shorthand => $"set here (line {field.Line}, as {shorthand} = true)",
            };
        }

        return JobSchema.Find(field.Key)?.Default is { } fallback
            ? $"inherited (default {fallback})"
            : "inherited (none)";
    }

    /// <summary>
    /// Why the CLI would refuse this text for this field, in the verb's own words, or null.
    /// </summary>
    /// <remarks>
    /// The same grammar <c>JobDocument.Apply</c> judges by, asked before the verb is run, so the
    /// form can say "'soon' is not a whole number." beside the box instead of after a round trip.
    /// Only what one field can be wrong about on its own; a rule between two keys stays with the
    /// dry run.
    /// </remarks>
    public static string? Judge(JobField field, string? text)
    {
        if (Blank(text) || !field.Known)
        {
            return null;
        }

        var trimmed = text!.Trim();

        if (!JobSchema.TryParse(field.Key, trimmed, out _, out var problem))
        {
            return $"'{trimmed}' is {problem}";
        }

        var row = JobSchema.Find(field.Key);

        if (row is { Kind: JobKeyKind.Integer }
            && int.TryParse(trimmed, System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            if (row is { Min: { } min, Max: { } max } && (number < min || number > max))
            {
                return $"{row.Key} must be between {min} and {max}.";
            }

            if (row is { Min: { } floor } && number < floor)
            {
                return $"{row.Key} must be at least {floor}.";
            }

            if (row is { Max: { } ceiling } && number > ceiling)
            {
                return $"{row.Key} must be at most {ceiling}.";
            }
        }

        if (string.Equals(field.Key, "dateformat", StringComparison.OrdinalIgnoreCase))
        {
            return ConfigValidator.DateFormatProblem(trimmed)?.Message;
        }

        return null;
    }

    /// <summary>Whether the size threshold means anything under the schedule the form shows.</summary>
    /// <remarks>
    /// Only under <c>size</c>. Otherwise the form hands the key back empty, so a threshold the
    /// file already holds is unset - a product-written file has <c>size</c> after
    /// <c>schedule</c>, and the last of the two to be written wins, so without this a calendar
    /// choice would never take effect.
    /// </remarks>
    public static bool SizeApplies(string? scheduleShown) =>
        string.Equals(scheduleShown?.Trim(), "size", StringComparison.OrdinalIgnoreCase);

    /// <summary>The entry a drop-down shows for "leave it inherited".</summary>
    public const string InheritChoice = "(inherit)";

    /// <summary>The entry the Basics drop-down shows for "leave it at the default", naming it.</summary>
    public static string DefaultChoice(string? fallback) =>
        fallback is null ? InheritChoice : $"(default: {fallback})";

    /// <summary>The value a drop-down item stands for: null for either inherit entry.</summary>
    public static string? ChoiceValue(string? item) =>
        item is null || item.StartsWith('(') ? null : item;

    /// <summary>Whether the file says the application rotates its own logs.</summary>
    public static bool IsManaged(JobEditorView view) =>
        string.Equals(
            view.Fields.FirstOrDefault(f => string.Equals(f.Key, "kind", StringComparison.OrdinalIgnoreCase))?.Value,
            "manage", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What the "who rotates" radios say <c>kind</c> should be.
    /// </summary>
    /// <remarks>
    /// The first radio is the default, so a new job that keeps it says nothing and inherits; an
    /// existing <c>manage</c> job switched back does say <c>rotate</c>, because silence would
    /// leave the file as it was.
    /// </remarks>
    public static string? KindValue(bool manage, string? original) =>
        manage ? "manage" : original is null ? null : "rotate";

    /// <summary>The "how" radio the file means, or null when its strategy has no radio.</summary>
    public static HowRotated? HowRotatedShown(JobEditorView view)
    {
        if (IsManaged(view))
        {
            return HowRotated.Manage;
        }

        var strategy = view.Fields.FirstOrDefault(f =>
            string.Equals(f.Key, "lockstrategy", StringComparison.OrdinalIgnoreCase))?.Value;

        return strategy?.Trim().ToLowerInvariant() switch
        {
            null or "rename" => HowRotated.Rename,
            "auto" => HowRotated.Auto,
            "copytruncate" => HowRotated.CopyTruncate,
            "copy" => HowRotated.Copy,
            _ => null,
        };
    }

    /// <summary>
    /// What the radio says <c>lockstrategy</c> should be.
    /// </summary>
    /// <remarks>
    /// The engine's own default is <c>rename</c>, so the Rename answer on a job that says
    /// nothing says nothing. The editor's default for a new job is the probe, and that one is
    /// written, because it differs from what silence would mean.
    /// </remarks>
    public static string? StrategyValue(HowRotated? how, string? original) => how switch
    {
        HowRotated.Auto => "auto",
        HowRotated.CopyTruncate => "copytruncate",
        HowRotated.Copy => "copy",
        HowRotated.Rename => original is null ? null : "rename",
        _ => original,
    };

    /// <summary>
    /// A flag as a tick box wants it: the original when the file already means that, else the word.
    /// </summary>
    public static string? FlagValue(bool want, string? original, bool defaultTrue)
    {
        var means = original is null
            ? defaultTrue
            : string.Equals(original, "true", StringComparison.OrdinalIgnoreCase);

        return means == want ? original : want ? "true" : "false";
    }

    /// <summary>The compression the file means: none when compress is off, else the type or zip.</summary>
    public static ArchiveCompression CompressionShown(string? compress, string? compressType)
    {
        if (string.Equals(compress, "false", StringComparison.OrdinalIgnoreCase))
        {
            return ArchiveCompression.None;
        }

        return compressType?.Trim().ToLowerInvariant() switch
        {
            "gzip" => ArchiveCompression.Gzip,
            "none" => ArchiveCompression.None,
            _ => ArchiveCompression.Zip,
        };
    }

    /// <summary>
    /// The compress and compresstype edits one choice stands for, writing only what changes.
    /// </summary>
    public static (string? Compress, string? CompressType) CompressionEdits(
        string? compress, string? compressType, ArchiveCompression choice)
    {
        if (CompressionShown(compress, compressType) == choice)
        {
            return (compress, compressType);
        }

        if (choice == ArchiveCompression.None)
        {
            return ("false", compressType);
        }

        var on = string.Equals(compress, "false", StringComparison.OrdinalIgnoreCase) ? "true" : compress;
        return (on, choice == ArchiveCompression.Gzip ? "gzip" : "zip");
    }

    /// <summary>The edits the Basics view stands for, over the job as it was read.</summary>
    /// <remarks>
    /// Under "only tidy up" the schedule, the early trigger, the naming and the folder mean
    /// nothing, so they are handed back as they were: a size typed before the radio was flipped
    /// is not written. A null <paramref name="answers"/>.How leaves kind and lockstrategy alone.
    /// </remarks>
    public static IReadOnlyDictionary<string, string?> BasicsEdits(JobEditorView original, BasicsAnswers answers)
    {
        string? Was(string key) =>
            original.Fields.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;

        var manage = answers.How == HowRotated.Manage;
        var (compress, compressType) = CompressionEdits(Was("compress"), Was("compresstype"), answers.Compression);
        var oldDir = string.IsNullOrWhiteSpace(answers.OldDir) ? null : answers.OldDir.Trim();

        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["kind"] = answers.How is null ? Was("kind") : KindValue(manage, Was("kind")),
            ["lockstrategy"] = manage ? Was("lockstrategy") : StrategyValue(answers.How, Was("lockstrategy")),
            ["schedule"] = manage ? Was("schedule") : ChoiceValue(answers.Schedule),
            ["maxsize"] = manage ? Was("maxsize") : answers.MaxSize,
            ["rotate"] = answers.Rotate,
            ["maxage"] = answers.MaxAge,
            ["dateext"] = manage ? Was("dateext") : FlagValue(answers.Dated, Was("dateext"), defaultTrue: false),
            ["compress"] = compress,
            ["compresstype"] = compressType,
            ["olddir"] = manage ? Was("olddir") : oldDir,
            ["createolddir"] = manage ? Was("createolddir") : FlagValue(answers.CreateOldDir, Was("createolddir"), defaultTrue: false),
        };
    }

    /// <summary>How many keys the file sets that the Basics view cannot show.</summary>
    public static int AdvancedSetCount(JobEditorView view) =>
        view.Fields.Count(f =>
            f.IsSet
            && !Header.Contains(f.Key, StringComparer.OrdinalIgnoreCase)
            && !Basics.Contains(f.Key, StringComparer.OrdinalIgnoreCase)
            && !Shorthands.Contains(f.Key, StringComparer.OrdinalIgnoreCase)
            && !string.Equals(f.Key, "kind", StringComparison.OrdinalIgnoreCase));

    /// <summary>The toggle's caption.</summary>
    public static string AdvancedLinkText(int advancedSet, bool showingAdvanced) =>
        showingAdvanced
            ? "Basic settings"
            : advancedSet == 0
                ? "Advanced settings"
                : $"Advanced settings ({advancedSet} set)";

    /// <summary>
    /// What to say beside a hook field, given what <c>doctor</c> made of this installation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Null when there is nothing to say. Otherwise the field is one whose contents will never
    /// run: the hook gate judges the owner of every file it would execute from, and on a per-user
    /// installation the owner is the installing user by design - which is most people who open
    /// this window at all.
    /// </para>
    /// <para>
    /// Said rather than enforced. The key is still writable, because the installation can change
    /// and a job file written on one machine is deployed to others; what must not happen is
    /// accepting it in silence, which is how somebody discovers at three in the morning that the
    /// service-restart hook they configured has never once fired.
    /// </para>
    /// </remarks>
    public static string? HookWarning(bool hooksAllowed) =>
        hooksAllowed
            ? null
            : "Hooks are refused on this installation, so anything entered here will never run. "
              + "'winlogrotate doctor' says why.";

    /// <summary>Whether <c>doctor --json</c> says hooks can run here.</summary>
    /// <remarks>
    /// False when the answer cannot be read at all, which is the safe direction: a warning shown
    /// where hooks do work is a sentence somebody ignores, and a warning missing where they do
    /// not is a hook nobody knows is dead.
    /// </remarks>
    public static bool HooksAllowed(string doctorJson)
    {
        try
        {
            using var document = JsonDocument.Parse(doctorJson);

            return document.RootElement.GetProperty("result")
                .GetProperty("hooksAllowed").GetBoolean();
        }
        catch (Exception e) when (e is JsonException
                                      or KeyNotFoundException
                                      or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>An empty editor, for a job that does not exist yet.</summary>
    public static JobEditorView Blank() => new()
    {
        Job = string.Empty,
        Path = string.Empty,
        Fields = [.. JobSchema.Keys.Select(Unset)],
        Problems = [],
        Unreadable = false,
    };

    /// <summary>Turns a <c>job show --json</c> response into fields.</summary>
    /// <remarks>
    /// Every key this product knows appears, set or not, because a form that listed only the keys
    /// a file happens to write would offer no way to add the others. Keys the file has and this
    /// build does not know are appended, so they can be seen and cleared.
    /// </remarks>
    public static JobEditorView From(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);

            // A refusal before a version mismatch. The verb completes with no payload when the
            // name is not a job, and says which names are; reading `result` regardless threw
            // KeyNotFoundException, and the editor told the operator the two executables were
            // different versions about a typo in a name.
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && (!document.RootElement.TryGetProperty("result", out var payload)
                    || payload.ValueKind != JsonValueKind.Object)
                && EnvelopeDiagnostics.From(json, "diagnostics").Where(d => d.IsProblem).ToList()
                    is { Count: > 0 } refused)
            {
                return new JobEditorView
                {
                    Job = string.Empty,
                    Path = string.Empty,
                    Fields = [],
                    Problems = [.. refused.Select(d => d.Message)],
                    Unreadable = false,
                    Refusal = refused[0].Message,
                };
            }

            var result = document.RootElement.GetProperty("result");

            var written = new Dictionary<string, (string Source, int Line, bool Known)>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var key in result.GetProperty("keys").EnumerateArray())
            {
                written[key.GetProperty("key").GetString() ?? ""] = (
                    key.GetProperty("source").GetString() ?? "",
                    key.GetProperty("line").GetInt32(),
                    key.GetProperty("known").GetBoolean());
            }

            var fields = new List<JobField>();

            foreach (var row in JobSchema.Keys)
            {
                fields.Add(written.TryGetValue(row.Key, out var found)
                    ? new JobField
                    {
                        Key = row.Key,
                        Kind = row.Kind,
                        Group = row.Group,
                        Choices = row.Choices,
                        Sample = row.Sample,
                        Value = Display(row, found.Source),
                        Line = found.Line,
                        Known = true,
                    }
                    : Unset(row));
            }

            // Anything the file writes that this build has no row for. Appended rather than
            // dropped: the operator has to be able to see a key they are being warned about, and
            // 'job set --unset' is the only way to act on that warning.
            foreach (var (key, found) in written.Where(w => !w.Value.Known))
            {
                var unknown = new JobKey
                {
                    Key = key,
                    Kind = JobKeyKind.Text,
                    Group = JobKeyGroup.Behaviour,
                    Sample = string.Empty,
                    Title = key,
                    Description = "Not a setting this product reads.",
                };

                fields.Add(new JobField
                {
                    Key = key,
                    Kind = unknown.Kind,
                    Group = unknown.Group,
                    Choices = [],
                    Sample = string.Empty,
                    Value = Display(unknown, found.Source),
                    Line = found.Line,
                    Known = false,
                });
            }

            FoldSchedule(fields);

            return new JobEditorView
            {
                Job = result.GetProperty("job").GetString() ?? "",
                Path = result.GetProperty("path").GetString() ?? "",
                Fields = fields,
                Problems = [.. result.GetProperty("diagnostics").EnumerateArray()
                    .Select(d => d.GetProperty("message").GetString() ?? "")
                    .Where(m => m.Length > 0)],
                Unreadable = false,
            };
        }
        catch (Exception e) when (e is JsonException
                                      or KeyNotFoundException
                                      or InvalidOperationException
                                      or FormatException)
        {
            // Wider than JsonException. GetProperty throws KeyNotFoundException and the typed
            // accessors throw InvalidOperationException, so an envelope that parsed but was
            // missing a field would otherwise escape into an async void handler in a process
            // that installs no unhandled-exception handler. A CLI one version out of step is the
            // ordinary way to produce exactly that.
            return new JobEditorView
            {
                Job = string.Empty,
                Path = string.Empty,
                Fields = [],
                Problems = ["Could not read the response from winlogrotate.exe."],
                Unreadable = true,
            };
        }
    }

    /// <summary>
    /// The command line that saves what somebody typed, naming only what they changed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the changes, which is the difference between an editor and a rewriter. Sending every
    /// field would write a value for each one, and a field showing an inherited value would
    /// become a per-job value - so pressing Save without typing anything would sever the job from
    /// <c>[defaults]</c> for good.
    /// </para>
    /// <para>
    /// A field somebody cleared becomes <c>--unset</c>, not an empty <c>--set</c>. Clearing means
    /// inherit again; an empty string is a value, and the two are different states.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> SaveArgs(
        string? configDir,
        string job,
        bool isNew,
        JobEditorView original,
        IReadOnlyDictionary<string, string?> edited)
    {
        var args = new List<string> { "job", isNew ? "add" : "set", job };

        var before = original.Fields.ToDictionary(f => f.Key, f => f.Value, StringComparer.OrdinalIgnoreCase);
        var kinds = original.Fields.ToDictionary(f => f.Key, f => f.Kind, StringComparer.OrdinalIgnoreCase);

        var schedule = original.Fields.FirstOrDefault(f =>
            string.Equals(f.Key, "schedule", StringComparison.OrdinalIgnoreCase));
        var scheduleTouched = false;

        foreach (var (key, now) in edited.OrderBy(e => Order(e.Key)))
        {
            // The name is the job's identity, carried as the argument above. It is refused from
            // --set, so sending it would turn every Save into a refusal.
            if (string.Equals(key, "name", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var kind = kinds.GetValueOrDefault(key, JobKeyKind.Text);
            var was = before.TryGetValue(key, out var found) ? found : null;

            // Trimmed, because " 30" is what a box holds after a stray space and the verb refuses
            // it as not a whole number - and a space around an unchanged value is not a change.
            var value = Blank(now) ? null : now!.Trim();

            if (Same(key, kind, was, value))
            {
                continue;
            }

            if (string.Equals(key, "schedule", StringComparison.OrdinalIgnoreCase))
            {
                scheduleTouched = true;

                // The file never wrote 'schedule'; it wrote the shorthand the field shows. Unsetting
                // a key the file does not have would be refused, and the shorthand is retired below.
                if (value is null && schedule?.Via is { } via && !string.Equals(via, "size", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            if (value is null)
            {
                args.Add("--unset");
                args.Add(key);
                continue;
            }

            // A list is one --set per item, because the verb accumulates a repeated key and
            // deliberately never splits on a separator - a Windows path may contain any of them.
            foreach (var item in Items(kind, value))
            {
                args.Add("--set");
                args.Add($"{key}={item}");
            }
        }

        // A changed schedule retires every shorthand the file writes, whatever its value: a
        // 'weekly = false' left behind is one hand edit from silently overriding the choice, and
        // a 'daily = true' below the new line would override it tonight.
        if (scheduleTouched && !isNew)
        {
            foreach (var shorthand in original.Fields.Where(f =>
                         f.IsSet && Shorthands.Contains(f.Key, StringComparer.OrdinalIgnoreCase)))
            {
                if (!args.Contains(shorthand.Key, StringComparer.OrdinalIgnoreCase))
                {
                    args.Add("--unset");
                    args.Add(shorthand.Key);
                }
            }
        }

        return CliArgs.For(configDir, [.. args]);
    }

    /// <summary>
    /// Whether what the form holds is what the file says, allowing for how a form holds it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A list is compared item by item. The model joins one with <c>\n</c> and a multiline box
    /// hands it back with <c>\r\n</c>, so compared as text every path was a change, and an
    /// untouched Save raised a UAC prompt for a write the CLI then reported as Unchanged.
    /// </para>
    /// <para>
    /// For <c>enabled</c>, an explicit <c>true</c> and an absent key are the same state - there is
    /// no <c>[defaults]</c> layer beneath it - and the form's checkbox says the first as null.
    /// Without this an explicit <c>enabled = true</c> in the file was unset by every untouched
    /// Save.
    /// </para>
    /// </remarks>
    private static bool Same(string key, JobKeyKind kind, string? was, string? now)
    {
        if (kind == JobKeyKind.TextList)
        {
            return Items(kind, was ?? string.Empty)
                .SequenceEqual(Items(kind, now ?? string.Empty), StringComparer.Ordinal);
        }

        if (string.Equals(key, "enabled", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(Enabled(was), Enabled(now), StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(was, now, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether what a box holds is what the file says, allowing for how a box holds it.
    /// </summary>
    /// <remarks>
    /// The same judgement <see cref="SaveArgs"/> makes, for the grid's Source column: a wrapped
    /// cell hands a hook's commands back with Windows line endings and a trailing one, and the
    /// column said "will be set here" about a value nobody had touched.
    /// </remarks>
    public static bool Unchanged(JobField field, string? now) =>
        Same(field.Key, field.Kind, field.Value, Blank(now) ? null : now!.Trim());

    /// <summary>An <c>enabled</c> value with the default spelled the way the form spells it.</summary>
    private static string? Enabled(string? value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ? null : value;

    /// <summary>
    /// The same command line, with the flag that makes it write nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Validate and save differ by exactly this, and by nothing that changes what is judged. A
    /// form that validated through one route and saved through another would show a green tick
    /// and then fail - and <c>--dry-run</c> needs no administrator rights, so this can run on
    /// every blur without a UAC prompt.
    /// </para>
    /// <para>
    /// <c>--json</c> as well, because this runs unelevated and the answer comes back on stdout.
    /// Without it the verb answers in prose, with its diagnostics on standard error, and
    /// <see cref="JobEditProjection"/> has no envelope to read: every Check then said "could not
    /// read the response" with the actual reason under an expander. The elevated save needs no
    /// such flag - the runner adds <c>--json-stream</c>, which implies it.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> ValidateArgs(
        string? configDir,
        string job,
        bool isNew,
        JobEditorView original,
        IReadOnlyDictionary<string, string?> edited) =>
        [.. SaveArgs(configDir, job, isNew, original, edited), "--dry-run", "--json"];

    /// <summary>Whether a Save would send anything at all.</summary>
    /// <remarks>
    /// The two words <see cref="SaveArgs"/> can emit for a change, and nothing else: it once
    /// also answered to <c>--paths</c>, which nothing here has ever produced - a list goes out as
    /// one <c>--set</c> per item - and a predicate with a dead branch is one nobody can tell is
    /// right.
    /// </remarks>
    public static bool Changes(IReadOnlyList<string> args) =>
        args.Any(a => a is "--set" or "--unset");

    /// <summary>
    /// Shows the schedule the file actually means on the schedule field.
    /// </summary>
    /// <remarks>
    /// The binder gives one schedule to <c>schedule</c>, to each shorthand that is <c>true</c>
    /// and to <c>size</c>, and the last one written wins. A form that showed <c>schedule</c>
    /// alone would show an inherited daily beside a file that says <c>weekly = true</c>.
    /// </remarks>
    private static void FoldSchedule(List<JobField> fields)
    {
        var winner = fields
            .Where(f => f.IsSet && (
                string.Equals(f.Key, "schedule", StringComparison.OrdinalIgnoreCase)
                || string.Equals(f.Key, "size", StringComparison.OrdinalIgnoreCase)
                || (Shorthands.Contains(f.Key, StringComparer.OrdinalIgnoreCase)
                    && string.Equals(f.Value, "true", StringComparison.OrdinalIgnoreCase))))
            .OrderByDescending(f => f.Line)
            .FirstOrDefault();

        if (winner is null || string.Equals(winner.Key, "schedule", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var at = fields.FindIndex(f => string.Equals(f.Key, "schedule", StringComparison.OrdinalIgnoreCase));

        fields[at] = fields[at] with
        {
            Value = string.Equals(winner.Key, "size", StringComparison.OrdinalIgnoreCase) ? "size" : winner.Key,
            Line = winner.Line,
            Via = winner.Key,
        };
    }

    private static JobField Unset(JobKey row) => new()
    {
        Key = row.Key,
        Kind = row.Kind,
        Group = row.Group,
        Choices = row.Choices,
        Sample = row.Sample,
        Value = null,
        Line = 0,
        Known = true,
    };

    private static int Order(string key)
    {
        var at = 0;

        foreach (var row in JobSchema.Keys)
        {
            if (string.Equals(row.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return at;
            }

            at++;
        }

        // A key this build does not know sorts last, where it will not displace anything.
        return int.MaxValue;
    }

    /// <summary>
    /// A source value as a person should see it in a box.
    /// </summary>
    /// <remarks>
    /// An enum value is shown in the spelling the form's list offers. The binder accepts
    /// <c>schedule = "Daily"</c>, and a combo holding "daily" matched it case-sensitively against
    /// nothing - so the box showed no selection, read back null, and an untouched Save sent
    /// <c>--unset schedule</c> for a line that was valid. A value not in the list at all is kept
    /// as written, for a CLI newer than this window; the form adds it to the list so it can be
    /// handed straight back.
    /// </remarks>
    private static string Display(JobKey row, string source) => row.Kind switch
    {
        JobKeyKind.TextList => string.Join('\n', JobSchema.ListItems(source)),
        JobKeyKind.Enum => Canonical(row.Choices, Scalar(source)),
        _ => Scalar(source),
    };

    private static string Canonical(IReadOnlyList<string> choices, string value) =>
        choices.FirstOrDefault(c => string.Equals(c, value, StringComparison.OrdinalIgnoreCase)) ?? value;

    /// <summary>
    /// A quoted TOML scalar as the binder reads it, or anything else as it was written.
    /// </summary>
    /// <remarks>
    /// Through the product's own parser rather than by stripping quotes. TOML has two string
    /// spellings, and only the basic one was being unquoted: a literal <c>'D:\archive'</c> kept
    /// its quotes in the box, and a basic string's escapes were shown raw, so <c>"D:\\archive"</c>
    /// appeared with two backslashes. Somebody "fixing" that to one would have written a change
    /// to a file that already said it.
    /// </remarks>
    private static string Scalar(string source)
    {
        var trimmed = source.Trim();

        if (trimmed.Length < 2 || trimmed[0] is not ('"' or '\''))
        {
            return source;
        }

        var document = TomlFile.Parse($"x = {trimmed}", "<value>");

        return !document.HasErrors
            && document.Document.KeyValues.GetChild(0) is KeyValueSyntax { Value: StringValueSyntax { Value: { } text } }
                ? text
                : source;
    }

    /// <summary>What one field's text names, which for a list is one item per line.</summary>
    /// <remarks>
    /// Each line trimmed, which is also what drops the <c>\r</c> a Windows text box leaves on
    /// every line but the last.
    /// </remarks>
    private static IReadOnlyList<string> Items(JobKeyKind kind, string value) =>
        kind == JobKeyKind.TextList
            ? [.. value.Split('\n').Select(i => i.Trim()).Where(i => i.Length > 0)]
            : [value];

    /// <summary>
    /// Whether a box is empty, which is how a form says "inherit again".
    /// </summary>
    /// <remarks>
    /// Whitespace counts as empty. Somebody who selected a value and pressed space meant to clear
    /// it, and writing <c>rotate = " "</c> would be a refusal they would have to decode.
    /// </remarks>
    private static bool Blank(string? text) => string.IsNullOrWhiteSpace(text);
}
