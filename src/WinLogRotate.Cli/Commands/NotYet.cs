using WinLogRotate.Cli.Output;
using WinLogRotate.Contracts;
using WinLogRotate.Core;

namespace WinLogRotate.Cli.Commands;

/// <summary>
/// Placeholder action for verbs whose implementation lands in a later milestone.
/// <para>
/// The whole verb tree exists from milestone 1 on purpose: the help text, the option names
/// and the JSON envelope are the contract the GUI and the installer are written against, and
/// settling them early costs nothing while changing them later is a rename across three
/// codebases. A verb that is not ready says so precisely, and exits non-zero so a script
/// never mistakes silence for success.
/// </para>
/// </summary>
internal static class NotYet
{
    public static int Run(CommandContext ctx, string verb, int milestone)
    {
        ctx.Output.Diagnostic(new CliDiagnostic
        {
            Severity = Severity.Error,
            Code = DiagnosticCode.ConfigInvalid,
            Message = $"'{verb}' is not implemented yet (milestone {milestone}).",
            Remedy = "Run 'winlogrotate --help' to see what does work in this build.",
        });
        ctx.Output.Line($"winlogrotate: '{verb}' is not implemented yet (milestone {milestone}).");
        return ctx.Output.Complete<EmptyResult>(verb, ExitCode.ConfigInvalid, null);
    }
}
