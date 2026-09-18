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
            return Refusals.NeedsWindows<ProbeResultDto>(
                ctx, "probe", "it asks the OS about share modes.");
        }

        return Run(ctx, path, LockProbe.Classify);
    }

    /// <summary>
    /// The verb, with its one question to the OS handed in.
    /// </summary>
    /// <remarks>
    /// The Windows guard stays on the public overload because <see cref="LockProbe"/> is what
    /// needs Windows. Everything from the verdict on is a decision about what to say, and a test
    /// on the Linux leg can hand in a verdict and check what the verb makes of it.
    /// </remarks>
    internal static int Run(CommandContext ctx, string path, Func<string, ProbeResult> classify)
    {
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

        var result = classify(path);

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
            // A Warning, because the verb exits 0: it was asked what the file supports, and it
            // answered. docs/automation.md says "ok" is exitCode == 0 and that a deliberate
            // nothing explains itself in the diagnostics; an Error riding on exit 0 was the one
            // shape that contract has no reading for, and CliResult showed a failure over a
            // success. The condition is the same one the Copy verdict warns about, one step worse.
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Warning,
                Code = DiagnosticCode.FileLocked,
                Message = "This file cannot be opened at all while its writer holds it.",
                Path = path,
                Remedy = "No lockstrategy can rotate it while that writer runs. The application has to "
                       + "release it, or roll its own logs and let kind = \"manage\" tidy the results.",
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
