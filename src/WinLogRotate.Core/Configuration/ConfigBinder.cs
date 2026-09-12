using System.Globalization;
using Tomlyn.Syntax;
using WinLogRotate.Contracts;

using WinLogRotate.Core.Notify;
using WinLogRotate.Core.Secrets;

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
        if (table is null)
        {
            return null;
        }

        ReportUnknownDefaultsKeys(table, file.Path, diagnostics);
        return BindSettings(table, file.Path, diagnostics);
    }

    /// <summary>
    /// Reports a key in <c>[defaults]</c> that is not a setting.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>[defaults]</c> reported nothing at all until milestone 16 - not a misspelling, not a job
    /// key that cannot be inherited, nothing. It is the one table the installer writes by hand,
    /// and the identical defect one table over is what <c>JobKeyTests</c> exists to prevent.
    /// </para>
    /// <para>
    /// <c>allowdangerous</c> gets its own sentence rather than the generic one, the way
    /// <c>BindNotify</c> special-cases <c>insecure</c>: an operator who wrote it there has a
    /// specific misunderstanding, and "not a setting" would not correct it.
    /// </para>
    /// </remarks>
    private static void ReportUnknownDefaultsKeys(TableSyntaxBase table, string file, DiagnosticBag d)
    {
        foreach (var kv in table.Items.OfType<KeyValueSyntax>())
        {
            var key = KeyName(kv);

            if (DefaultsKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (key.Equals("allowdangerous", StringComparison.OrdinalIgnoreCase))
            {
                d.Warn(file, DiagnosticCode.ConfigInvalid,
                    "allowdangerous is per job and is deliberately not inheritable, so an entry "
                    + "here is ignored.",
                    LineOf(kv), ColumnOf(kv),
                    remedy: "Put it in the job that needs it. A file-wide override is the switch "
                          + "everyone flips once during an incident and never flips back, which "
                          + "is why it does not exist.");
                continue;
            }

            d.Warn(file, DiagnosticCode.ConfigInvalid,
                $"'{key}' is not a [defaults] setting, and is ignored.",
                LineOf(kv), ColumnOf(kv),
                remedy: Suggest(key) is { } near
                    ? $"Did you mean '{near}'?"
                    : "Remove it, or check it against docs/configuration.md.");
        }
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
            Notify = GetBool(table, "notify", file.Path, diagnostics) ?? defaults.Notify,
        };
    }

    /// <summary>
    /// Binds the <c>[notify]</c> table from <c>config.toml</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Like <see cref="BindJournal"/>, this deliberately does not call ReportSyntaxErrors or
    /// CheckSchema: BindDefaults has already run over the same file, and repeating them reports
    /// every syntax error in config.toml twice. It returns a non-null Default when the table is
    /// absent, for the same reason.
    /// </para>
    /// <para>
    /// Unlike every other table here, unknown keys are reported. That is a deliberate
    /// divergence: a mistyped rotation directive falls back to a documented default and rotates
    /// slightly differently, but a mistyped <c>tresh0ld</c> silently disables the alerting, and
    /// the discovery happens during the incident it was supposed to warn about.
    /// </para>
    /// </remarks>
    public static NotifySettings BindNotify(TomlFile file, DiagnosticBag diagnostics)
    {
        var table = FindTable(file.Document, "notify");
        if (table is null)
        {
            return NotifySettings.Default;
        }

        var defaults = NotifySettings.Default;

        string[] known =
        [
            "enabled", "on", "threshold", "remind_after", "budget", "retries",
            "breaker_after", "breaker_cooldown", "to", "redact",
            "proxy", "no_proxy", "server_cert_thumbprint",
        ];

        foreach (var kv in table.Items.OfType<KeyValueSyntax>())
        {
            var key = KeyName(kv);

            // "insecure" is reported separately, and better, just below. Two warnings for one
            // line reads like two problems.
            if (string.Equals(key, "insecure", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!known.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                diagnostics.Warn(file.Path, DiagnosticCode.NotifyMisconfigured,
                    $"'{key}' is not a [notify] setting, and is ignored.",
                    LineOf(kv), ColumnOf(kv),
                    remedy: $"Valid settings: {string.Join(", ", known)}.");
            }
        }

        // There is deliberately no "insecure" here, and adding one would undo the point of
        // server_cert_thumbprint. A switch that disables verification gets set once during an
        // incident and is never unset.
        if (Find(table, "insecure") is { } insecure)
        {
            diagnostics.Warn(file.Path, DiagnosticCode.NotifyMisconfigured,
                "There is no 'insecure' setting, and TLS verification is never skipped.",
                LineOf(insecure), ColumnOf(insecure),
                remedy: "For an internal relay with a self-signed certificate, pin it instead: "
                      + "server_cert_thumbprint = \"<SHA-256>\".");
        }

        return new NotifySettings
        {
            Enabled = GetBool(table, "enabled", file.Path, diagnostics) ?? defaults.Enabled,
            On = GetEnum<NotifyOn>(table, "on", file.Path, diagnostics) ?? defaults.On,
            Threshold = GetEnum<Severity>(table, "threshold", file.Path, diagnostics) ?? defaults.Threshold,
            RemindAfter = GetDuration(table, "remind_after", file.Path, diagnostics) ?? defaults.RemindAfter,
            Budget = GetDuration(table, "budget", file.Path, diagnostics) ?? defaults.Budget,
            Retries = Clamp(GetInt(table, "retries", file.Path, diagnostics) ?? defaults.Retries, 0, 5),
            // Clamped rather than trusted. A negative breaker_after opens the circuit on the
            // first success; a negative cooldown never closes it. Neither is a configuration
            // anybody meant to write, and both are silent.
            BreakerAfter = Clamp(GetInt(table, "breaker_after", file.Path, diagnostics) ?? defaults.BreakerAfter, 1, 1000),
            BreakerCooldown = Clamp(GetInt(table, "breaker_cooldown", file.Path, diagnostics) ?? defaults.BreakerCooldown, 1, 1000),
            To = GetStringListOrNull(table, "to", file.Path, diagnostics) ?? defaults.To,
            Redact = GetStringListOrNull(table, "redact", file.Path, diagnostics) ?? defaults.Redact,
            Proxy = GetString(table, "proxy", file.Path, diagnostics) ?? defaults.Proxy,
            NoProxy = GetStringListOrNull(table, "no_proxy", file.Path, diagnostics) ?? defaults.NoProxy,
            ServerCertThumbprint =
                GetString(table, "server_cert_thumbprint", file.Path, diagnostics)
                ?? defaults.ServerCertThumbprint,
        };
    }

    /// <summary>
    /// Binds every <c>[notify.KIND.NAME]</c> provider table.
    /// </summary>
    /// <remarks>
    /// Credentials are bound as <see cref="SecretRef"/>, never as a value, so nothing here ever
    /// holds a password in a field that could be printed. Whether the reference resolves is a
    /// question for the validator, which has a secret lookup; this only records what was asked
    /// for and where it was written.
    /// </remarks>
    public static IReadOnlyList<NotifyProvider> BindProviders(TomlFile file, DiagnosticBag diagnostics)
    {
        var providers = new List<NotifyProvider>();

        foreach (var (kind, kindName) in new[]
                 {
                     (NotifyProviderKind.Email, "email"),
                     (NotifyProviderKind.Pushover, "pushover"),
                     (NotifyProviderKind.Webhook, "webhook"),
                 })
        {
            foreach (var (table, name) in FindTablesUnder(file.Document, "notify", kindName))
            {
                providers.Add(BindProvider(table, kind, $"{kindName}.{name}", file.Path, diagnostics));
            }
        }

        // [[notify.email]] is the shape somebody reaches for who has met arrays of tables. It
        // parses as a different node type entirely, so without this it is silently ignored and
        // the provider simply never exists.
        foreach (var array in file.Document.Tables.OfType<TableArraySyntax>())
        {
            var parts = KeyParts(array);
            if (parts.Length >= 2 && string.Equals(parts[0], "notify", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Error(file.Path, DiagnosticCode.NotifyMisconfigured,
                    $"[[{string.Join('.', parts)}]] is an array of tables; notification providers are named tables.",
                    LineOf(array), ColumnOf(array),
                    remedy: $"Write [{string.Join('.', parts)}.a-name] instead.");
            }
        }

        return providers;
    }

    private static NotifyProvider BindProvider(
        TableSyntaxBase table, NotifyProviderKind kind, string name, string file, DiagnosticBag d)
    {
        var provider = new NotifyProvider
        {
            Name = name,
            Kind = kind,
            Line = LineOf(table),
            Column = ColumnOf(table),
            Enabled = GetBool(table, "enabled", file, d) ?? true,
        };

        return kind switch
        {
            NotifyProviderKind.Email => provider with
            {
                Host = GetString(table, "host", file, d),
                Port = GetInt(table, "port", file, d) ?? 25,
                Auth = GetEnum<SmtpAuth>(table, "auth", file, d) ?? SmtpAuth.None,
                Tls = GetEnum<SmtpTls>(table, "tls", file, d) ?? SmtpTls.Opportunistic,
                Delivery = GetEnum<SmtpDelivery>(table, "delivery", file, d) ?? SmtpDelivery.Network,
                PickupDirectory = GetString(table, "pickup_directory", file, d),
                From = GetString(table, "from", file, d),
                To = GetStringListOrNull(table, "to", file, d) ?? [],
                Username = GetString(table, "username", file, d),
                SubjectPrefix = GetString(table, "subject_prefix", file, d),
                Password = Secret(table, "password", file, d),
            },

            NotifyProviderKind.Pushover => provider with
            {
                Token = Secret(table, "token", file, d),
                UserKey = Secret(table, "user_key", file, d),
                Priority = GetInt(table, "priority", file, d) ?? 0,
            },

            _ => provider with
            {
                Url = Secret(table, "url", file, d),
                Method = GetString(table, "method", file, d) ?? "POST",
                ContentType = GetString(table, "content_type", file, d) ?? "application/json",
                Body = GetString(table, "body", file, d),
                MaxMessage = GetInt(table, "max_message", file, d),
            },
        };
    }

    /// <summary>
    /// Reads a credential as a reference, carrying the position of the VALUE.
    /// </summary>
    /// <remarks>
    /// The value's column rather than the key's, so "your password is written in the clear here"
    /// points at the password. An editor jumping to column 1 makes the operator find it
    /// themselves, on the one line they would rather not have to read twice.
    /// </remarks>
    private static SecretRef Secret(TableSyntaxBase table, string key, string file, DiagnosticBag d)
    {
        var kv = Find(table, key);
        if (kv is null)
        {
            return SecretRef.None;
        }

        var at = (SyntaxNode?)kv.Value ?? kv;
        return SecretRef.Parse(GetString(table, key, file, d), LineOf(at), ColumnOf(at));
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

        // "*" is how findings that belong to the run rather than to any one job are recorded in
        // notify.json. A job of that name would share an outcome, an aggregation group and a
        // fingerprint with them - so an unrelated job going green could report the configuration
        // as recovered while conf.d was still world-writable.
        //
        // Referenced through the constant rather than as a literal so the coupling is greppable,
        // and so the doc comment on RunScope claiming this cannot happen becomes true.
        if (string.Equals(name, State.NotifyStateDocument.RunScope, StringComparison.Ordinal))
        {
            diagnostics.Error(file.Path, DiagnosticCode.ConfigInvalid,
                $"'{name}' is not a usable job name: it is how run-wide findings are recorded.",
                LineOf(table), ColumnOf(table),
                "Any other name will do.");
            return null;
        }

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
        ReportUnknownJobKeys(table, file.Path, diagnostics);

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
            Notify = settings.Notify,
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
            HookTimeout = settings.HookTimeout,
        };
    }

    private static JobSettings BindSettings(TableSyntaxBase table, string file, DiagnosticBag d)
    {
        // Every key that decides when a job is due writes the same field, and the last one
        // written IN THE FILE wins - so this walks the document in its own order.
        //
        // It used to probe a fixed list, which made that list's order the precedence whatever the
        // file said: yearly beat monthly beat weekly beat daily beat hourly, and `size`, read
        // after the loop, beat all five. The comment that stood here, the Schedule enum's own
        // summary, docs/configuration.md and the "behaviour we reproduce exactly" table all said
        // last-one-wins while `weekly = true` above `daily = true` bound Weekly.
        //
        // logrotate 3.21.0 was run against seven orderings to settle it, and it is explicit:
        // it prints "note: 'daily' overrides previously specified 'weekly'" as it reads, and the
        // reverse for the reverse. `size` is included - it is one more assignment to the same
        // field, and a later `daily` beats it.
        Schedule? schedule = null;
        long? size = null;
        var read = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var kv in table.Items.OfType<KeyValueSyntax>())
        {
            var key = KeyName(kv);

            // One reading per key. Two spellings differing only in case are one key to this
            // binder and two to the parser; reading the value again here would only double
            // whatever diagnostic it produces.
            if (!read.Add(key))
            {
                continue;
            }

            if (Frequency(key) is { } frequency)
            {
                if (GetBool(table, key, file, d) == true)
                {
                    schedule = frequency;
                }
            }
            else if (string.Equals(key, "size", StringComparison.OrdinalIgnoreCase))
            {
                size = GetSize(table, key, file, d);
                if (size is not null)
                {
                    schedule = Schedule.Size;
                }
            }
            else if (string.Equals(key, "schedule", StringComparison.OrdinalIgnoreCase)
                     && GetString(table, key, file, d) is { } scheduleText)
            {
                if (Enum.TryParse<Schedule>(scheduleText, ignoreCase: true, out var parsed))
                {
                    schedule = parsed;
                }
                else
                {
                    // The offending key rather than the [job] header, which is where this
                    // pointed while the value was read outside any walk of the document.
                    d.Error(file, DiagnosticCode.ConfigInvalid,
                        $"'{scheduleText}' is not a schedule.",
                        LineOf(kv), ColumnOf(kv),
                        "Use hourly, daily, weekly, monthly, yearly or size.");
                }
            }
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
            Notify = GetBool(table, "notify", file, d),
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
            HookTimeout = GetDuration(table, "hook_timeout", file, d),
        };
    }

    /// <summary>
    /// Every key a <c>[job]</c> table may carry.
    /// </summary>
    /// <remarks>
    /// Deliberately next to <see cref="BindSettings"/> rather than beside the unknown-key loop
    /// that consumes it: a key added to the binder and forgotten here starts being reported as a
    /// mistake the moment it works, which is loud, immediate and impossible to ship past.
    /// </remarks>
    internal static readonly string[] JobKeys =
    [
        // Structural - read by BindJob itself.
        "name", "paths", "kind", "enabled", "allowdangerous",

        // Schedule.
        "hourly", "daily", "weekly", "monthly", "yearly", "schedule", "size",
        "weekday", "monthday",

        // Retention.
        "rotate", "start", "maxage", "minage", "minsize", "maxsize", "maxfiles",

        // Archiving.
        "compress", "compresstype", "delaycompress", "dateext", "dateformat",
        "olddir", "createolddir",

        // Behaviour.
        "missingok", "notify", "notifempty", "lockstrategy", "livefiles",
        "retrycount", "retryinterval",

        // Hooks.
        "prerotate", "postrotate", "hook_timeout",
    ];

    /// <summary>Keys that name or scope one job, and so mean nothing file-wide.</summary>
    private static readonly string[] Structural =
        ["name", "paths", "kind", "enabled", "allowdangerous"];

    /// <summary>
    /// Every key <c>[defaults]</c> may carry: the job keys, minus the ones that describe a
    /// particular job rather than how any job behaves.
    /// </summary>
    /// <remarks>
    /// Derived from <see cref="JobKeys"/> rather than listed again, so a key added to one is never
    /// missing from the other. It has to be declared after both of them: static initialisers run
    /// in declaration order, and the compiler's nullable analysis is what caught this reading them
    /// while they were still null.
    /// </remarks>
    internal static readonly string[] DefaultsKeys =
        [.. JobKeys.Except(Structural, StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// Reports a key in <c>[job]</c> that is not a setting.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A mistyped <c>postrotae</c> used to bind cleanly, validate cleanly and do nothing forever.
    /// So did <c>firstaction</c>, <c>lastaction</c> and <c>preremove</c> - which the importer
    /// wrote into generated configuration files as real-looking directives, next to a
    /// <c>postrotate</c> that works, with the original shell script quoted above them as proof of
    /// a correct translation.
    /// </para>
    /// <para>
    /// A warning rather than an error, and for the reason <c>[notify]</c> gives for its own: this
    /// runs over every file in conf.d, and promoting one misspelling to an error would stop the
    /// whole run - every healthy job included - over a key that is optional in the first place.
    /// </para>
    /// </remarks>
    private static void ReportUnknownJobKeys(TableSyntaxBase table, string file, DiagnosticBag d)
    {
        foreach (var kv in table.Items.OfType<KeyValueSyntax>())
        {
            var key = KeyName(kv);

            if (JobKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            d.Warn(file, DiagnosticCode.ConfigInvalid,
                $"'{key}' is not a [job] setting, and is ignored.",
                LineOf(kv), ColumnOf(kv),
                remedy: Suggest(key) is { } near
                    ? $"Did you mean '{near}'?"
                    : "Remove it, or check it against docs/configuration.md "
                      + "(hooks are in docs/hooks.md).");
        }
    }

    /// <summary>The known key one edit away from what was written, if there is exactly one.</summary>
    /// <remarks>
    /// Only substitutions, insertions and deletions of a single character - enough for
    /// <c>postrotae</c> and <c>compres</c>, and not enough to confidently propose
    /// <c>postrotate</c> for <c>firstaction</c>, which is a different directive rather than a
    /// misspelling of this one.
    /// </remarks>
    private static string? Suggest(string key)
    {
        string? best = null;

        foreach (var known in JobKeys)
        {
            if (Math.Abs(known.Length - key.Length) > 1 || !WithinOneEdit(key, known))
            {
                continue;
            }

            if (best is not null)
            {
                return null;
            }

            best = known;
        }

        return best;
    }

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

        return j <= i && k <= i;
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

    /// <summary>
    /// The dotted parts of a table header, taken from the syntax tree rather than by splitting
    /// its text.
    /// </summary>
    /// <remarks>
    /// <c>[notify . email . relay]</c> and <c>["notify".email.relay]</c> are both legal TOML for
    /// the same table, and neither survives a string split on '.'. Walking the key nodes is what
    /// makes those behave the same as the ordinary spelling.
    /// </remarks>
    internal static string[] KeyParts(TableSyntaxBase table)
    {
        var key = table.Name;
        if (key is null)
        {
            return [];
        }

        var parts = new List<string> { Unquote(key.Key?.ToString()?.Trim()) };
        foreach (var dotted in key.DotKeys)
        {
            parts.Add(Unquote(dotted.Key?.ToString()?.Trim()));
        }

        return [.. parts];
    }

    internal static string Unquote(string? text)
    {
        var value = text ?? string.Empty;
        return value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0]
            ? value[1..^1]
            : value;
    }

    /// <summary>Tables whose header is exactly <paramref name="prefix"/> plus one more part.</summary>
    private static IEnumerable<(TableSyntaxBase Table, string Name)> FindTablesUnder(
        DocumentSyntax doc, params string[] prefix)
    {
        foreach (var table in doc.Tables)
        {
            var parts = KeyParts(table);
            if (parts.Length != prefix.Length + 1)
            {
                continue;
            }

            var matches = true;
            for (var i = 0; i < prefix.Length; i++)
            {
                if (!string.Equals(parts[i], prefix[i], StringComparison.OrdinalIgnoreCase))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                yield return (table, parts[^1]);
            }
        }
    }

    /// <summary>The schedule a frequency keyword selects, or null when the key is not one.</summary>
    private static Schedule? Frequency(string key) => key.ToLowerInvariant() switch
    {
        "hourly" => Schedule.Hourly,
        "daily" => Schedule.Daily,
        "weekly" => Schedule.Weekly,
        "monthly" => Schedule.Monthly,
        "yearly" => Schedule.Yearly,
        _ => null,
    };

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
    /// Reads a duration written either as "7d"/"30m"/"45s" or as a TimeSpan like "01:00:00".
    /// </summary>
    /// <remarks>
    /// Both forms because both already appear: host pause takes "01:00:00" today, and nobody
    /// writing a reminder interval wants to count hours into a colon-separated triple.
    /// </remarks>
    private static TimeSpan? GetDuration(TableSyntaxBase t, string key, string file, DiagnosticBag d)
    {
        var text = GetString(t, key, file, d);
        if (text is null)
        {
            return null;
        }

        if (TryParseDuration(text, out var parsed))
        {
            return parsed;
        }

        var kv = Find(t, key);
        d.Error(file, DiagnosticCode.ConfigInvalid,
            $"'{key}' is not a duration.",
            kv is null ? 0 : LineOf(kv), kv is null ? 0 : ColumnOf(kv),
            remedy: "Write it as 30s, 15m, 2h, 7d, or as 01:00:00.");
        return null;
    }

    private static int Clamp(int value, int min, int max) => Math.Clamp(value, min, max);

    /// <summary>Parses "7d", "30m", "45s", "2h" or a TimeSpan.</summary>
    /// <remarks>
    /// Public because the CLI parses <c>--run-deadline</c> with it. An operator who has written
    /// <c>remind_after = "7d"</c> in the configuration will write <c>--run-deadline 45m</c> on the
    /// command line, and two grammars for one concept is how a flag silently means something else.
    /// </remarks>
    public static bool TryParseDuration(string text, out TimeSpan value)
    {
        value = default;
        var trimmed = text.Trim();

        if (trimmed.Length == 0)
        {
            return false;
        }

        var unit = trimmed[^1];
        if (char.IsAsciiDigit(unit))
        {
            return TimeSpan.TryParse(trimmed, System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        if (!double.TryParse(
                trimmed[..^1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var count) || count < 0)
        {
            return false;
        }

        switch (char.ToLowerInvariant(unit))
        {
            case 's': value = TimeSpan.FromSeconds(count); return true;
            case 'm': value = TimeSpan.FromMinutes(count); return true;
            case 'h': value = TimeSpan.FromHours(count); return true;
            case 'd': value = TimeSpan.FromDays(count); return true;
            default: return false;
        }
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
