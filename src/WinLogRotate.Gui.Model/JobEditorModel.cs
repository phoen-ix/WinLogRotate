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
}

/// <summary>
/// The decisions a job editor makes, with nothing that needs a window.
/// </summary>
/// <remarks>
/// <para>
/// Two questions, and they are the whole form: given <c>job show --json</c>, which fields are set
/// here and which are inherited; and given what somebody typed, what command line says that. The
/// second is the testable one - <c>JobEditorArgsTests</c> parses what this produces against the
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
    /// <summary>The keys a job file owns and <c>[defaults]</c> cannot supply.</summary>
    /// <remarks>
    /// Shown on the main form rather than in the advanced grid, because they are the ones
    /// without an inherited state to explain: a job either says them or has none.
    /// </remarks>
    public static IReadOnlyList<string> Structural { get; } =
        [.. JobSchema.Keys.Where(k => k.PerJobOnly).Select(k => k.Key)];

    /// <summary>The keys whose value is a command this product would run.</summary>
    /// <remarks>
    /// Named here rather than derived from <see cref="JobKeyGroup.Hooks"/>, because
    /// <c>hook_timeout</c> is in that group and is a duration, not a command - warning that a
    /// timeout will never run would be nonsense.
    /// </remarks>
    public static IReadOnlyList<string> Hooks { get; } = ["prerotate", "postrotate"];

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

        foreach (var (key, now) in edited.OrderBy(e => Order(e.Key)))
        {
            // The name is the job's identity, carried as the argument above. It is refused from
            // --set, so sending it would turn every Save into a refusal.
            if (string.Equals(key, "name", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var was = before.TryGetValue(key, out var found) ? found : null;
            var value = Blank(now) ? null : now;

            if (string.Equals(was, value, StringComparison.Ordinal))
            {
                continue;
            }

            if (value is null)
            {
                args.Add("--unset");
                args.Add(key);
                continue;
            }

            // A list is one --set per item, because the verb accumulates a repeated key and
            // deliberately never splits on a separator - a Windows path may contain any of them.
            foreach (var item in Items(kinds.GetValueOrDefault(key, JobKeyKind.Text), value))
            {
                args.Add("--set");
                args.Add($"{key}={item}");
            }
        }

        return CliArgs.For(configDir, [.. args]);
    }

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
    /// Four words, because the verb and the name are always there: <c>job set NAME</c> plus the
    /// configuration directory. Anything longer names a change.
    /// </remarks>
    public static bool Changes(IReadOnlyList<string> args) =>
        args.Any(a => a is "--set" or "--unset" or "--paths");

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
