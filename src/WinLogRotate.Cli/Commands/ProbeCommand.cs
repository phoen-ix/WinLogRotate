using WinLogRotate.Cli.Output;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Core.Io;
using WinLogRotate.Core.State;

namespace WinLogRotate.Cli.Commands;

/// <summary>
/// Reports which rotation strategies a given file actually supports.
/// </summary>
/// <remarks>
/// The answer is a property of whoever is writing the file, not of the file itself, so it can
/// only be discovered by asking. This is what turns "lockstrategy is explicit" from a guessing
/// game into an informed choice - and it is what the GUI calls when a job is added.
/// </remarks>
internal static class ProbeCommand
{
    public static int Run(CommandContext ctx, string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            ctx.Output.Line("winlogrotate: 'probe' needs Windows - it asks the OS about share modes.");
            return ctx.Output.Complete<ProbeResultDto>("probe", ExitCode.Errors, null);
        }

        if (!File.Exists(path))
        {
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Error,
                Code = DiagnosticCode.FileMissing,
                Message = $"'{path}' does not exist.",
                Path = path,
            });
            return ctx.Output.Complete<ProbeResultDto>("probe", ExitCode.Errors, null);
        }

        var result = LockProbe.Classify(path);

        ctx.Output.Line($"{path}");
        ctx.Output.Line($"  verdict     {result.Verdict}");
        ctx.Output.Line($"  {result.Explanation}");
        ctx.Output.Line("");
        ctx.Output.Line("  strategy        supported");

        foreach (var strategy in Enum.GetValues<Core.Configuration.LockStrategy>())
        {
            var supported = ProbeSupport.Supports(result.Verdict, strategy);
            ctx.Output.Line($"  {strategy.ToString().ToLowerInvariant(),-16}{(supported ? "yes" : "no")}");
        }

        var best = ProbeSupport.Best(result.Verdict);
        if (best is not null)
        {
            ctx.Output.Line("");
            ctx.Output.Line($"  Suggested:  lockstrategy = \"{best.ToString()!.ToLowerInvariant()}\"");
        }

        if (result.Verdict == ProbeVerdict.Copy)
        {
            // Worth saying out loud: this is what log4net's default ExclusiveLock looks like,
            // and the honest answer is that the application has to change, not us.
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Warning,
                Code = DiagnosticCode.StrategyUnavailable,
                Message = "This file can only be copied - the original cannot be emptied, so it will keep growing.",
                Path = path,
                Remedy = "The writer is holding it exclusively. log4net's default ExclusiveLock does this; "
                       + "switching it to MinimalLock, or letting the application roll its own logs and using "
                       + "kind = \"manage\" here, are the two real fixes.",
            });
        }

        if (result.Verdict == ProbeVerdict.None)
        {
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Error,
                Code = DiagnosticCode.FileLocked,
                Message = "This file cannot be opened at all while its writer holds it.",
                Path = path,
            });
        }

        return ctx.Output.Complete("probe", ExitCode.Ok, new ProbeResultDto
        {
            Path = path,
            Verdict = result.Verdict.ToString(),
            Explanation = result.Explanation ?? "",
            Suggested = best?.ToString()?.ToLowerInvariant(),
            BlockingError = result.BlockingError,
            BlockingErrorText = result.BlockingError == 0 ? null : Win32Error.Describe(result.BlockingError),
            Unlocked = result.Unlocked,
        });
    }
}
