using System.Globalization;
using System.Text;
using WinLogRotate.Core.Configuration;

namespace WinLogRotate.Core.Import;

/// <summary>One converted job, plus everything that could not be converted.</summary>
public sealed record ImportedJob
{
    public required string SuggestedFileName { get; init; }
    public required string Toml { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>True when something needed a human decision, so the job is written disabled.</summary>
    public required bool NeedsReview { get; init; }
}

/// <summary>
/// Converts a logrotate configuration into WinLogRotate job files.
/// </summary>
/// <remarks>
/// <para>
/// One way, deliberately. The Windows-specific keys - <c>lockstrategy</c>, <c>livefiles</c>,
/// <c>createsddl</c> - have no representation in logrotate's syntax, so a round trip would be
/// lossy in a way that is worse than not offering it at all.
/// </para>
/// <para>
/// Nothing is guessed. Anything that cannot be translated is written into the output <b>as a
/// comment</b> and the job is created disabled, so an import can never quietly start deleting
/// files under rules nobody reviewed. The generated file is the report.
/// </para>
/// </remarks>
public static class LogrotateImporter
{
    public static IReadOnlyList<ImportedJob> Import(string text, string sourceName)
    {
        return LogrotateParser.Parse(text)
            .Select((stanza, index) => Convert(stanza, sourceName, index))
            .ToArray();
    }

    private static ImportedJob Convert(LogrotateStanza stanza, string sourceName, int index)
    {
        var warnings = new List<string>();
        var body = new StringBuilder();
        var needsReview = false;

        var name = SuggestName(stanza, index);

        body.AppendLine("schema = 1");
        body.AppendLine();
        body.AppendLine(CultureInfo.InvariantCulture,
            $"# Imported from {sourceName} by WinLogRotate.");
        body.AppendLine("# Every directive that could not be translated is commented below rather than");
        body.AppendLine("# guessed at. Review them, then set enabled = true.");
        body.AppendLine();
        body.AppendLine("[job]");
        body.AppendLine(CultureInfo.InvariantCulture, $"name  = \"{name}\"");
        body.AppendLine("kind  = \"rotate\"");

        body.AppendLine("paths = [");
        foreach (var pattern in stanza.Patterns)
        {
            var windows = ToWindowsPath(pattern);
            if (windows is null)
            {
                warnings.Add($"'{pattern}' is a POSIX path with no Windows equivalent.");
                needsReview = true;
                body.AppendLine(CultureInfo.InvariantCulture,
                    $"    # TODO: '{pattern}' has no Windows equivalent - replace it.");
                continue;
            }

            body.AppendLine(CultureInfo.InvariantCulture, $"    \"{windows}\",");
        }

        body.AppendLine("]");
        body.AppendLine();

        foreach (var (directive, argument) in stanza.Directives)
        {
            switch (directive.ToLowerInvariant())
            {
                case "hourly" or "daily" or "weekly" or "monthly" or "yearly":
                    body.AppendLine(CultureInfo.InvariantCulture, $"{directive.ToLowerInvariant()} = true");
                    if (argument.Length > 0 && int.TryParse(argument, CultureInfo.InvariantCulture, out var day))
                    {
                        // A ternary yields a plain string rather than an interpolated-string
                        // handler, so the format-provider overload does not apply here.
                        var key = directive.Equals("weekly", StringComparison.OrdinalIgnoreCase)
                            ? "weekday"
                            : "monthday";
                        body.AppendLine(CultureInfo.InvariantCulture, $"{key} = {day}");
                    }

                    break;

                case "rotate" or "start" or "maxage" or "minage":
                    body.AppendLine(CultureInfo.InvariantCulture, $"{directive.ToLowerInvariant()} = {argument}");
                    break;

                case "size" or "minsize" or "maxsize":
                    body.AppendLine(CultureInfo.InvariantCulture, $"{directive.ToLowerInvariant()} = \"{argument}\"");
                    break;

                case "compress":
                    body.AppendLine("compress = true");
                    break;
                case "nocompress":
                    body.AppendLine("compress = false");
                    break;
                case "delaycompress":
                    body.AppendLine("delaycompress = true");
                    break;
                case "dateext":
                    body.AppendLine("dateext = true");
                    break;
                case "nodateext":
                    body.AppendLine("dateext = false");
                    break;
                case "missingok":
                    body.AppendLine("missingok = true");
                    break;
                case "nomissingok":
                    body.AppendLine("missingok = false");
                    break;
                case "notifempty":
                    body.AppendLine("notifempty = true");
                    break;
                case "ifempty":
                    body.AppendLine("notifempty = false");
                    break;
                case "copytruncate":
                    body.AppendLine("lockstrategy = \"copytruncate\"");
                    break;
                case "copy":
                    body.AppendLine("lockstrategy = \"copy\"");
                    break;
                case "olddir":
                    body.AppendLine(CultureInfo.InvariantCulture,
                        $"olddir = \"{ToWindowsPath(argument) ?? argument}\"");
                    break;
                case "createolddir":
                    body.AppendLine("createolddir = true");
                    break;

                case "dateformat":
                    // logrotate's strftime specifiers are not .NET's, and translating them
                    // silently would produce archives named nothing like what was expected.
                    var translated = TranslateDateFormat(argument);
                    if (translated is null)
                    {
                        warnings.Add($"dateformat '{argument}' could not be translated.");
                        needsReview = true;
                        body.AppendLine(CultureInfo.InvariantCulture,
                            $"# TODO: dateformat '{argument}' uses strftime specifiers - rewrite in .NET form.");
                    }
                    else
                    {
                        body.AppendLine(CultureInfo.InvariantCulture, $"dateformat = \"{translated}\"");
                    }

                    break;

                case "su":
                    warnings.Add("'su' is not implementable on Windows and was dropped.");
                    body.AppendLine(CultureInfo.InvariantCulture,
                        $"# dropped: su {argument}");
                    body.AppendLine("#   Windows has no setuid. WinLogRotate always rotates as the account it runs");
                    body.AppendLine("#   under - set that on the scheduled task's principal instead.");
                    break;

                case "create":
                    warnings.Add($"'create {argument}' is approximated: Windows has no mode bits.");
                    body.AppendLine(CultureInfo.InvariantCulture, $"# create {argument}");
                    body.AppendLine("#   Approximated as an ACL. Use createsddl for an exact one.");
                    break;

                case "sharedscripts" or "nosharedscripts":
                    // Recognised, and simply not a distinction we make: hooks run once per job.
                    body.AppendLine(CultureInfo.InvariantCulture, $"# {directive}: hooks always run once per job here.");
                    break;

                case "mail" or "mailfirst" or "maillast" or "nomail":
                case "shred" or "noshred" or "shredcycles":
                case "compresscmd" or "uncompresscmd" or "compressoptions":
                    warnings.Add($"'{directive}' is not supported and was dropped.");
                    body.AppendLine($"# dropped: {directive} {argument}".TrimEnd());
                    break;

                default:
                    body.AppendLine($"# unrecognised: {directive} {argument}".TrimEnd());
                    break;
            }
        }

        foreach (var (which, script) in stanza.Scripts)
        {
            var hook = Supported(which) ? TranslateScript(script) : null;

            if (hook is not null)
            {
                body.AppendLine();
                body.AppendLine(CultureInfo.InvariantCulture,
                    $"# translated from {which}:");
                foreach (var line in script.Split('\n'))
                {
                    body.AppendLine(CultureInfo.InvariantCulture, $"#   {line.Trim()}");
                }

                body.AppendLine(CultureInfo.InvariantCulture, $"{which.ToLowerInvariant()} = [\"{hook}\"]");
                continue;
            }

            // A shell script is not a Windows command, and firstaction, lastaction and preremove
            // are not directives this product has. Preserved commented rather than
            // half-translated, because a hook that runs the wrong thing as SYSTEM is worse
            // than one that does not run.
            //
            // This used to emit the key live for all five - so an imported firstaction landed in
            // conf.d looking exactly like a working directive, next to a postrotate that will
            // work, with the original shell quoted above it as proof of a correct translation, and
            // was then dropped in silence by a binder that reported nothing. Setting enabled = true
            // meant the service was never signalled again and nothing anywhere said so.
            needsReview = true;
            warnings.Add(Supported(which)
                ? $"the {which} script is shell and needs a Windows equivalent."
                : $"{which} has no equivalent here; hooks run once per job, either side of it.");

            var todo = Supported(which)
                ? $"# TODO: {which} was a shell script:"
                : $"# TODO: {which} is not supported; {Instead(which)}";

            body.AppendLine();
            body.AppendLine(todo);
            foreach (var line in script.Split('\n'))
            {
                body.AppendLine(CultureInfo.InvariantCulture, $"#   {line.Trim()}");
            }
        }

        body.AppendLine();
        body.AppendLine(needsReview
            ? "# Disabled until the TODOs above have been reviewed."
            : "# Review the paths, then set enabled = true.");
        body.AppendLine("enabled = false");

        return new ImportedJob
        {
            SuggestedFileName = name.ToLowerInvariant().Replace(' ', '-') + ".toml",
            Toml = body.ToString(),
            Warnings = warnings,
            NeedsReview = needsReview,
        };
    }

    private static string SuggestName(LogrotateStanza stanza, int index)
    {
        var first = stanza.Patterns.FirstOrDefault();
        if (first is null)
        {
            return $"imported-{index + 1}";
        }

        var segments = first
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Where(s => !s.Contains('*', StringComparison.Ordinal))
            .Where(s => !s.EndsWith(':'))          // drop the drive letter
            .ToArray();

        // Walk back past the generic container directories. "C:/nginx/logs/*.log" should be
        // called nginx, not logs - the last segment is almost always the least informative one.
        string[] generic = ["logs", "log", "logfiles"];
        for (var i = segments.Length - 1; i >= 0; i--)
        {
            if (!generic.Contains(segments[i], StringComparer.OrdinalIgnoreCase))
            {
                return segments[i].Split('.')[0];
            }
        }

        return segments.Length > 0 ? segments[^1].Split('.')[0] : $"imported-{index + 1}";
    }

    /// <summary>
    /// Maps a POSIX path onto Windows where an obvious equivalent exists.
    /// </summary>
    /// <remarks>
    /// Only genuinely mechanical cases are converted. <c>/var/log/nginx</c> becomes nothing
    /// useful, so it is reported rather than invented - a job pointed at a guessed directory
    /// either does nothing or does something surprising.
    /// </remarks>
    private static string? ToWindowsPath(string path)
    {
        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
        {
            return path.Replace('\\', '/');
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return path;
        }

        return path.StartsWith('/') ? null : path.Replace('\\', '/');
    }

    /// <summary>Translates the strftime specifiers logrotate permits into .NET format strings.</summary>
    private static string? TranslateDateFormat(string format)
    {
        var result = new StringBuilder();

        for (var i = 0; i < format.Length; i++)
        {
            if (format[i] != '%')
            {
                result.Append(format[i]);
                continue;
            }

            if (i + 1 >= format.Length)
            {
                return null;
            }

            var mapped = format[++i] switch
            {
                'Y' => "yyyy",
                'm' => "MM",
                'd' => "dd",
                'H' => "HH",
                'M' => "mm",
                'S' => "ss",
                'y' => "yy",
                _ => null,
            };

            if (mapped is null)
            {
                return null;
            }

            result.Append(mapped);
        }

        return result.ToString();
    }

    /// <summary>
    /// Whether a logrotate script kind maps onto something this product actually runs.
    /// </summary>
    /// <remarks>
    /// <c>prerotate</c> and <c>postrotate</c> do; the other three do not, and the differences are
    /// real rather than cosmetic. <c>firstaction</c> and <c>lastaction</c> fire whether or not any
    /// log was due, where a hook here runs only when a live log actually moves; <c>preremove</c> is
    /// handed the name of the file about to be deleted, and a job-scoped hook has nowhere to put
    /// one.
    /// </remarks>
    private static bool Supported(string which) =>
        which.Equals("prerotate", StringComparison.OrdinalIgnoreCase)
        || which.Equals("postrotate", StringComparison.OrdinalIgnoreCase);

    /// <summary>What to write instead, for the three that have no equivalent.</summary>
    private static string Instead(string which) =>
        which.Equals("preremove", StringComparison.OrdinalIgnoreCase)
            ? "it ran per condemned file, and a hook here has no way to name one."
            : "it ran whether or not anything rotated. postrotate runs only when a log really "
              + "moved, which is usually what was wanted:";

    /// <summary>
    /// Recognises the handful of shell scripts that have an exact Windows equivalent.
    /// </summary>
    /// <remarks>
    /// Only exact matches are translated. Everything else is preserved as a comment, because a
    /// hook that runs approximately the right thing as SYSTEM is worse than one that does not
    /// run at all.
    /// </remarks>
    private static string? TranslateScript(string script)
    {
        var text = script.Replace('\n', ' ').Replace('\r', ' ');

        foreach (var (needle, service) in new[]
                 {
                     ("nginx", "nginx"), ("httpd", "Apache"), ("apache2", "Apache"),
                 })
        {
            if (text.Contains(needle, StringComparison.OrdinalIgnoreCase)
                && (text.Contains("kill", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("reload", StringComparison.OrdinalIgnoreCase)))
            {
                return $"service:paramchange:{service}";
            }
        }

        if (text.Contains("systemctl reload", StringComparison.OrdinalIgnoreCase))
        {
            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var index = Array.FindIndex(parts, p => p.Equals("reload", StringComparison.OrdinalIgnoreCase));
            if (index >= 0 && index + 1 < parts.Length)
            {
                return $"service:paramchange:{parts[index + 1].Trim('\'', '"')}";
            }
        }

        return null;
    }
}
