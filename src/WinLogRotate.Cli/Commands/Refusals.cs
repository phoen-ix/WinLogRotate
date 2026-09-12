using WinLogRotate.Cli.Output;
using WinLogRotate.Contracts;
using WinLogRotate.Core;

namespace WinLogRotate.Cli.Commands;

/// <summary>
/// The refusals several verbs share, said through the one channel a caller reads.
/// </summary>
/// <remarks>
/// <para>
/// <c>CliEnvelope.Diagnostics</c> documents itself as "everything worth telling the caller, in
/// order. Never empty on a failure", and <c>JsonOutputSink.Line</c> is a no-op with the comment
/// "the same information is carried as diagnostics and events". Six platform guards said their
/// piece with <c>Output.Line</c> and nothing else, so under <c>--json</c> they said nothing at
/// all: <c>{"verb":"scan","ok":false,"exitCode":1,"diagnostics":[]}</c>.
/// </para>
/// <para>
/// A caller then has only the exit code, and <c>CliResult.Describe</c> turns 1 into "Completed,
/// but some files could not be rotated" - about a verb that rotates nothing, on a platform it
/// cannot run on. The Event Log heard nothing either: <c>EventLogSink</c> mirrors
/// <c>Diagnostic</c> and passes <c>Line</c> straight through.
/// </para>
/// <para>
/// One place rather than six, because <c>LR1005</c> has to be raised at one severity to stay
/// inside <c>ASingleEmitterCodeIsDocumentedAtItsOwnSeverity</c> - and because a guard copied six
/// times is a guard that will be copied a seventh.
/// </para>
/// </remarks>
internal static class Refusals
{
    /// <summary>The command line named something this verb cannot work with.</summary>
    /// <param name="what">What the value should have been, as a noun phrase: "a duration".</param>
    public static int CannotUse<T>(
        CommandContext ctx, string verb, string value, string what, string remedy)
        where T : class
    {
        ctx.Output.Diagnostic(new CliDiagnostic
        {
            Severity = Severity.Error,
            Code = DiagnosticCode.ArgumentUnusable,
            Message = $"'{value}' is not {what}.",
            Remedy = remedy,
        });

        return ctx.Output.Complete<T>(verb, ExitCode.ConfigInvalid, null);
    }

    /// <summary>This verb needs Windows, and this machine is not.</summary>
    public static int NeedsWindows<T>(CommandContext ctx, string verb, string because)
        where T : class
    {
        ctx.Output.Diagnostic(new CliDiagnostic
        {
            Severity = Severity.Error,
            Code = DiagnosticCode.NotSupportedHere,
            Message = $"'{verb}' needs Windows: {because}",
            Remedy = "Run it on the machine whose logs are being rotated.",
        });

        return ctx.Output.Complete<T>(verb, ExitCode.Errors, null);
    }
}
