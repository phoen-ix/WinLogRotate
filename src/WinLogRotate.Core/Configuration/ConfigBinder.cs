using System.Globalization;
using Tomlyn.Syntax;
using WinLogRotate.Contracts;

namespace WinLogRotate.Core.Configuration;

/// <summary>
/// Reads a TOML syntax tree into the config model.
/// </summary>
/// <remarks>
/// Binding is done by hand against the syntax tree rather than by a reflection-based
/// deserializer, for two reasons. It keeps the whole path AOT-clean, and - more usefully - it
/// means every diagnostic carries the exact line and column of the offending token, so
/// <c>winlogrotate config check</c> points at the typo instead of describing it.
/// </remarks>
public static class ConfigBinder
{
    /// <summary>Binds <c>config.toml</c>: the schema header and the <c>[defaults]</c> table.</summary>
    public static JobSettings? BindDefaults(TomlFile file, DiagnosticBag diagnostics)
    {
        ReportSyntaxErrors(file, diagnostics);
        CheckSchema(file, diagnostics);

        var table = FindTable(file.Document, "defaults");
        return table is null ? null : BindSettings(table, file.Path, diagnostics);
    }

    /// <summary>Binds the <c>[journal]</c> table from <c>config.toml</c>.</summary>
    public static JournalSettings BindJournal(TomlFile file, DiagnosticBag diagnostics)
    {
        var table = FindTable(file.Document, "journal");
        if (table is null)
        {
            return JournalSettings.Default;
        }

        var defaults = JournalSettings.Default;

        return new JournalSettings
        {
            Enabled = GetBool(table, "enabled", file.Path, diagnostics) ?? defaults.Enabled,
            Retain = GetInt(table, "retain", file.Path, diagnostics) ?? defaults.Retain,
            Compress = GetEnum<CompressType>(table, "compress", file.Path, diagnostics) ?? defaults.Compress,
            MaxSize = GetSize(table, "maxsize", file.Path, diagnostics) ?? defaults.MaxSize,
        };
    }

    /// <summary>Binds one <c>conf.d</c> file into a job, or null if it is unusable.</summary>
    public static JobConfig? BindJob(TomlFile file, DiagnosticBag diagnostics)
    {
        ReportSyntaxErrors(file, diagnostics);
        CheckSchema(file, diagnostics);

        var table = FindTable(file.Document, "job");
        if (table is null)
        {
            diagnostics.Error(file.Path, DiagnosticCode.ConfigInvalid,
                "No [job] table found.",
                remedy: "Every file in conf.d describes exactly one job and must contain a [job] table.");
            return null;
        }

        var name = GetString(table, "name", file.Path, diagnostics)
                   ?? Path.GetFileNameWithoutExtension(file.Path);

        var paths = GetStringList(table, "paths", file.Path, diagnostics);
        if (paths.Count == 0)
        {
            diagnostics.Error(file.Path, DiagnosticCode.ConfigInvalid,
                $"Job '{name}' lists no paths.",
                LineOf(table), ColumnOf(table),
                "Add paths = [\"C:/logs/app/*.log\"].");
            return null;
        }

        var kind = JobKind.Rotate;
        if (GetString(table, "kind", file.Path, diagnostics) is { } kindText)
        {
            if (Enum.TryParse<JobKind>(kindText, ignoreCase: true, out var parsed))
            {
                kind = parsed;
            }
            else
            {
                diagnostics.Error(file.Path, DiagnosticCode.ConfigInvalid,
                    $"'{kindText}' is not a job kind.",
                    LineOf(table), ColumnOf(table),
                    "Use kind = \"rotate\" (we rotate) or kind = \"manage\" (the application rotates; we compress and retain).");
                return null;
            }
        }

        var settings = BindSettings(table, file.Path, diagnostics);

        return new JobConfig
        {
            Name = name,
            Kind = kind,
            Paths = paths,
            Enabled = GetBool(table, "enabled", file.Path, diagnostics) ?? true,
            SourceFile = file.Path,
            AllowDangerous = GetStringList(table, "allowdangerous", file.Path, diagnostics),
            Schedule = settings.Schedule,
            Weekday = settings.Weekday,
            MonthDay = settings.MonthDay,
            Rotate = settings.Rotate,
            Start = settings.Start,
            MaxAge = settings.MaxAge,
            MinAge = settings.MinAge,
            MinSize = settings.MinSize,
            MaxSize = settings.MaxSize,
            SizeThreshold = settings.SizeThreshold,
            Compress = settings.Compress,
            CompressType = settings.CompressType,
            DelayCompress = settings.DelayCompress,
            DateExt = settings.DateExt,
            DateFormat = settings.DateFormat,
            MissingOk = settings.MissingOk,
            NotIfEmpty = settings.NotIfEmpty,
            OldDir = settings.OldDir,
            CreateOldDir = settings.CreateOldDir,
            LockStrategy = settings.LockStrategy,
            LiveFiles = settings.LiveFiles,
            MaxFiles = settings.MaxFiles,
            RetryCount = settings.RetryCount,
            RetryIntervalMs = settings.RetryIntervalMs,
            PreRotate = settings.PreRotate,
            PostRotate = settings.PostRotate,
        };
    }

    private static JobSettings BindSettings(TableSyntaxBase table, string file, DiagnosticBag d)
    {
        // The frequency keywords are mutually exclusive and last-one-wins, exactly as in
        // logrotate, so they all write the same field rather than coexisting.
        Schedule? schedule = null;
        foreach (var (key, value) in new[]
                 {
                     ("hourly", Schedule.Hourly), ("daily", Schedule.Daily),
                     ("weekly", Schedule.Weekly), ("monthly", Schedule.Monthly),
                     ("yearly", Schedule.Yearly),
                 })
        {
            if (GetBool(table, key, file, d) == true)
            {
                schedule = value;
            }
        }

        if (GetString(table, "schedule", file, d) is { } scheduleText)
        {
            if (Enum.TryParse<Schedule>(scheduleText, ignoreCase: true, out var parsed))
            {
                schedule = parsed;
            }
            else
            {
                d.Error(file, DiagnosticCode.ConfigInvalid,
                    $"'{scheduleText}' is not a schedule.",
                    LineOf(table), ColumnOf(table),
                    "Use hourly, daily, weekly, monthly, yearly or size.");
            }
        }

        var size = GetSize(table, "size", file, d);
        if (size is not null)
        {
            schedule = Schedule.Size;
        }

        return new JobSettings
        {
            Schedule = schedule,
            Weekday = GetInt(table, "weekday", file, d),
            MonthDay = GetInt(table, "monthday", file, d),
            Rotate = GetInt(table, "rotate", file, d),
            Start = GetInt(table, "start", file, d),
            MaxAge = GetInt(table, "maxage", file, d),
            MinAge = GetInt(table, "minage", file, d),
            MinSize = GetSize(table, "minsize", file, d),
            MaxSize = GetSize(table, "maxsize", file, d),
            SizeThreshold = size,
            Compress = GetBool(table, "compress", file, d),
            CompressType = GetEnum<CompressType>(table, "compresstype", file, d),
            DelayCompress = GetBool(table, "delaycompress", file, d),
            DateExt = GetBool(table, "dateext", file, d),
            DateFormat = GetString(table, "dateformat", file, d),
            MissingOk = GetBool(table, "missingok", file, d),
            NotIfEmpty = GetBool(table, "notifempty", file, d),
            OldDir = GetString(table, "olddir", file, d),
            CreateOldDir = GetBool(table, "createolddir", file, d),
            LockStrategy = GetEnum<LockStrategy>(table, "lockstrategy", file, d),
            LiveFiles = GetInt(table, "livefiles", file, d),
            MaxFiles = GetInt(table, "maxfiles", file, d),
            RetryCount = GetInt(table, "retrycount", file, d),
            RetryIntervalMs = GetInt(table, "retryinterval", file, d),
            PreRotate = GetStringListOrNull(table, "prerotate", file, d),
            PostRotate = GetStringListOrNull(table, "postrotate", file, d),
        };
    }

    // ---- syntax-tree helpers ------------------------------------------------------------

    private static void ReportSyntaxErrors(TomlFile file, DiagnosticBag d)
    {
        foreach (var error in file.Errors)
        {
            d.Error(file.Path, DiagnosticCode.ConfigInvalid, error.Message,
                error.Span.Start.Line + 1, error.Span.Start.Column + 1);
        }
    }

    private static void CheckSchema(TomlFile file, DiagnosticBag d)
    {
        var schema = file.Document.KeyValues
            .FirstOrDefault(kv => KeyName(kv) == "schema");

        if (schema is null)
        {
            d.Warn(file.Path, DiagnosticCode.ConfigInvalid,
                "No 'schema' key. Assuming schema = 1.",
                remedy: "Add 'schema = 1' at the top so future versions can migrate this file.");
            return;
        }

        if (schema.Value is IntegerValueSyntax { Value: > 1 } v)
        {
            d.Error(file.Path, DiagnosticCode.ConfigInvalid,
                $"This file declares schema {v.Value}, but this build only understands 1.",
                schema.Span.Start.Line + 1, schema.Span.Start.Column + 1,
                "Upgrade WinLogRotate, or hand-edit the file back to schema 1.");
        }
    }

    private static TableSyntaxBase? FindTable(DocumentSyntax doc, string name) =>
        doc.Tables.FirstOrDefault(t =>
            string.Equals(t.Name?.ToString().Trim(), name, StringComparison.OrdinalIgnoreCase));

    private static string KeyName(KeyValueSyntax kv) =>
        kv.Key?.ToString().Trim() ?? string.Empty;

    private static KeyValueSyntax? Find(TableSyntaxBase table, string key) =>
        table.Items.OfType<KeyValueSyntax>()
            .FirstOrDefault(kv => string.Equals(KeyName(kv), key, StringComparison.OrdinalIgnoreCase));

    private static int LineOf(SyntaxNode node) => node.Span.Start.Line + 1;

    private static int ColumnOf(SyntaxNode node) => node.Span.Start.Column + 1;

    private static string? GetString(TableSyntaxBase t, string key, string file, DiagnosticBag d)
    {
        var kv = Find(t, key);
        if (kv is null)
        {
            return null;
        }

        if (kv.Value is StringValueSyntax s)
        {
            return s.Value;
        }

        d.Error(file, DiagnosticCode.ConfigInvalid,
            $"'{key}' must be a quoted string.", LineOf(kv), ColumnOf(kv));
        return null;
    }

    private static bool? GetBool(TableSyntaxBase t, string key, string file, DiagnosticBag d)
    {
        var kv = Find(t, key);
        if (kv is null)
        {
            return null;
        }

        if (kv.Value is BooleanValueSyntax b)
        {
            return b.Value;
        }

        d.Error(file, DiagnosticCode.ConfigInvalid,
            $"'{key}' must be true or false.", LineOf(kv), ColumnOf(kv));
        return null;
    }

    private static int? GetInt(TableSyntaxBase t, string key, string file, DiagnosticBag d)
    {
        var kv = Find(t, key);
        if (kv is null)
        {
            return null;
        }

        if (kv.Value is IntegerValueSyntax i)
        {
            return (int)i.Value;
        }

        d.Error(file, DiagnosticCode.ConfigInvalid,
            $"'{key}' must be a whole number.", LineOf(kv), ColumnOf(kv));
        return null;
    }

    private static T? GetEnum<T>(TableSyntaxBase t, string key, string file, DiagnosticBag d)
        where T : struct, Enum
    {
        var text = GetString(t, key, file, d);
        if (text is null)
        {
            return null;
        }

        if (Enum.TryParse<T>(text, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        var kv = Find(t, key)!;
        d.Error(file, DiagnosticCode.ConfigInvalid,
            $"'{text}' is not a valid value for '{key}'.",
            LineOf(kv), ColumnOf(kv),
            $"Valid values: {string.Join(", ", Enum.GetNames<T>().Select(n => n.ToLowerInvariant()))}.");
        return null;
    }

    /// <summary>
    /// Reads a size, accepting either a plain byte count or a human suffix.
    /// <para>
    /// The suffixes are binary (<c>k</c> = 1024), matching logrotate rather than SI, because
    /// someone converting a logrotate config expects <c>100M</c> to mean the same thing here.
    /// </para>
    /// </summary>
    private static long? GetSize(TableSyntaxBase t, string key, string file, DiagnosticBag d)
    {
        var kv = Find(t, key);
        if (kv is null)
        {
            return null;
        }

        if (kv.Value is IntegerValueSyntax i)
        {
            return i.Value;
        }

        if (kv.Value is StringValueSyntax { Value: { } sizeText } && TryParseSize(sizeText, out var bytes))
        {
            return bytes;
        }

        d.Error(file, DiagnosticCode.ConfigInvalid,
            $"'{key}' must be a byte count or a size such as \"100M\".",
            LineOf(kv), ColumnOf(kv),
            "Suffixes k, M and G are binary (1 k = 1024 bytes), as in logrotate.");
        return null;
    }

    public static bool TryParseSize(string text, out long bytes)
    {
        bytes = 0;
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        var multiplier = 1L;
        var last = char.ToUpperInvariant(trimmed[^1]);

        if (last is 'K' or 'M' or 'G' or 'T')
        {
            multiplier = last switch
            {
                'K' => 1L << 10,
                'M' => 1L << 20,
                'G' => 1L << 30,
                _ => 1L << 40,
            };
            trimmed = trimmed[..^1].TrimEnd();
        }

        if (!long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        bytes = value * multiplier;
        return true;
    }

    private static IReadOnlyList<string> GetStringList(
        TableSyntaxBase t, string key, string file, DiagnosticBag d) =>
        GetStringListOrNull(t, key, file, d) ?? [];

    private static IReadOnlyList<string>? GetStringListOrNull(
        TableSyntaxBase t, string key, string file, DiagnosticBag d)
    {
        var kv = Find(t, key);
        if (kv is null)
        {
            return null;
        }

        // A single string is accepted where a list is expected. Requiring brackets for one
        // path is the kind of friction that makes people write the config wrong once and then
        // stop reading the docs.
        if (kv.Value is StringValueSyntax { Value: { } single })
        {
            return [single];
        }

        if (kv.Value is ArraySyntax array)
        {
            var values = new List<string>();
            foreach (var item in array.Items)
            {
                if (item.Value is StringValueSyntax { Value: { } text })
                {
                    values.Add(text);
                }
                else
                {
                    d.Error(file, DiagnosticCode.ConfigInvalid,
                        $"Every entry in '{key}' must be a quoted string.",
                        LineOf(kv), ColumnOf(kv));
                }
            }

            return values;
        }

        d.Error(file, DiagnosticCode.ConfigInvalid,
            $"'{key}' must be a string or a list of strings.", LineOf(kv), ColumnOf(kv));
        return null;
    }
}
