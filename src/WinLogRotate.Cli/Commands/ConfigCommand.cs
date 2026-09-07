using WinLogRotate.Cli.Output;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Safety;

namespace WinLogRotate.Cli.Commands;

internal static class ConfigCommand
{
    public static int Check(CommandContext ctx, string? configDir)
    {
        var (loaded, paths) = Load(configDir, quarantine: false);

        foreach (var d in loaded.Diagnostics)
        {
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = d.Severity,
                Code = d.Code,
                Message = d.Message,
                Path = d.File,
                Line = d.Line == 0 ? null : d.Line,
                Column = d.Column == 0 ? null : d.Column,
                Remedy = d.Remedy,
            });

            // Deliberately no ctx.Output.Line here: the sink already rendered this diagnostic,
            // and echoing it prints everything twice for a human and does nothing for --json.
        }

        var errors = loaded.Diagnostics.Count(d => d.Severity >= Severity.Error);
        var warnings = loaded.Diagnostics.Count(d => d.Severity == Severity.Warning);

        ctx.Output.Line($"{paths.Root}: {loaded.Jobs.Count} job(s), {errors} error(s), {warnings} warning(s)");

        var exit = errors > 0 ? ExitCode.ConfigInvalid : ExitCode.Ok;

        return ctx.Output.Complete("config check", exit, new ConfigCheckResult
        {
            Root = paths.Root,
            Jobs = loaded.Jobs.Count,
            Errors = errors,
            Warnings = warnings,
            Diagnostics = loaded.Diagnostics.Select(Map).ToArray(),
        });
    }

    public static int Show(CommandContext ctx, string? configDir)
    {
        var (loaded, paths) = Load(configDir, quarantine: false);

        foreach (var job in loaded.Jobs)
        {
            ctx.Output.Line($"[{job.Name}] kind={job.Kind.ToString().ToLowerInvariant()} enabled={job.Enabled}");
            foreach (var p in job.Paths)
            {
                ctx.Output.Line($"    path      {p}");
            }

            ctx.Output.Line($"    schedule  {job.Schedule.ToString().ToLowerInvariant()}");
            ctx.Output.Line($"    rotate    {job.Rotate}" + (job.MaxAge is { } a ? $"   maxage {a}d" : ""));
            ctx.Output.Line($"    compress  {job.CompressType.ToString().ToLowerInvariant()}" +
                            (job.DelayCompress ? " (delayed)" : ""));
            if (job.Kind == JobKind.Manage)
            {
                ctx.Output.Line($"    livefiles {job.LiveFiles}   (never touched - the application is writing them)");
            }
            else
            {
                ctx.Output.Line($"    onlocked  {job.LockStrategy.ToString().ToLowerInvariant()}");
            }

            ctx.Output.Line("");
        }

        if (loaded.Jobs.Count == 0)
        {
            ctx.Output.Line($"no jobs configured in {paths.ConfigDirectory}");
        }

        return ctx.Output.Complete("config show", ExitCode.Ok, new ConfigShowResult
        {
            Root = paths.Root,
            Jobs = loaded.Jobs,
        });
    }

    private static (LoadedConfig, InstallPaths) Load(string? configDir, bool quarantine)
    {
        var paths = InstallPaths.Resolve(configDir);
        var guard = new PathGuard(new GuardOptions { Elevated = Privilege.IsElevated() });
        return (ConfigLoader.Load(paths, guard, quarantine), paths);
    }

    private static ConfigDiagnosticDto Map(ConfigDiagnostic d) => new()
    {
        Severity = d.Severity.ToString().ToLowerInvariant(),
        Code = d.Code,
        Message = d.Message,
        File = d.File,
        Line = d.Line,
        Column = d.Column,
        Remedy = d.Remedy,
    };
}
