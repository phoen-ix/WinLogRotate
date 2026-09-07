using WinLogRotate.Cli.Output;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Core.Globbing;
using WinLogRotate.Core.Safety;

namespace WinLogRotate.Cli.Commands;

internal static class GlobCommand
{
    public static int Run(CommandContext ctx, string pattern)
    {
        var guard = new PathGuard(new GuardOptions { Elevated = Privilege.IsElevated() });
        var decision = guard.CheckPattern(pattern);

        if (!decision.IsAllowed)
        {
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Critical,
                Code = DiagnosticCode.DangerousPathRefused,
                Message = decision.Message ?? $"'{pattern}' was refused.",
                Remedy = decision.Remedy,
                Path = pattern,
            });
            ctx.Output.Line($"refused: {decision.Message}");
            if (decision.Remedy is not null)
            {
                ctx.Output.Line($"         {decision.Remedy}");
            }

            return ctx.Output.Complete<GlobResult>("glob", ExitCode.ConfigInvalid, null);
        }

        if (decision.Overridden)
        {
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Warning,
                Code = DiagnosticCode.DangerousPathRefused,
                Message = decision.Message ?? "Permitted by an explicit override.",
                Path = pattern,
            });
        }

        var matches = FileEnumerator.Resolve(pattern);
        var count = guard.CheckMatchCount(pattern, matches.Count);

        if (!count.IsAllowed)
        {
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Critical,
                Code = DiagnosticCode.DangerousPathRefused,
                Message = count.Message ?? "Too many matches.",
                Remedy = count.Remedy,
                Path = pattern,
            });
            ctx.Output.Line($"refused: {count.Message}");
            return ctx.Output.Complete<GlobResult>("glob", ExitCode.ConfigInvalid, null);
        }

        foreach (var match in matches)
        {
            ctx.Output.Line($"  {match.Path}  {Humanize(match.Length)}");
        }

        ctx.Output.Line(matches.Count == 0
            ? $"no files match {pattern}"
            : $"{matches.Count} file(s), {Humanize(matches.Sum(m => m.Length))} total");

        var result = new GlobResult
        {
            Pattern = pattern,
            Anchor = Glob.LiteralPrefix(WinPath.Normalize(pattern)),
            Count = matches.Count,
            TotalBytes = matches.Sum(m => m.Length),
            Files = matches.Select(m => m.Path).ToArray(),
        };

        return ctx.Output.Complete("glob", ExitCode.Ok, result);
    }

    internal static string Humanize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):N1} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):N1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):N1} KB",
        _ => $"{bytes} B",
    };
}
