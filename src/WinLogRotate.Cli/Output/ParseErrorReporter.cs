using System.CommandLine;
using WinLogRotate.Cli.Commands;
using WinLogRotate.Contracts;
using WinLogRotate.Core;

namespace WinLogRotate.Cli.Output;

/// <summary>
/// Renders command-line parse errors.
/// <para>
/// System.CommandLine exits 1 on a parse error, but 1 is our "the run happened and something
/// went wrong" code, and a mistyped flag means nothing was attempted at all. A scheduled task
/// that logs exit 1 should always mean "look at the logs", never "you typo'd a flag", so parse
/// errors map to <see cref="ExitCode.ConfigInvalid"/> alongside a bad config file.
/// </para>
/// <para>
/// This lives under Output/ because it is output - the architecture test that bans bare
/// Console elsewhere in the CLI is deliberately not weakened for it.
/// </para>
/// </summary>
internal static class ParseErrorReporter
{
    public static int Report(ParseResult parse, IReadOnlyList<string> args)
    {
        // The verb as a person wrote it, which is what every envelope carries. The leaf
        // command's own name made "host status --nope" report verb "status" and send the caller
        // to the help of a verb called 'status', which does not exist at the root.
        var isRoot = ReferenceEquals(parse.CommandResult, parse.RootCommandResult);
        var verb = CommandContext.VerbName(parse);
        var help = isRoot ? "winlogrotate --help" : $"winlogrotate {verb} --help";
        var remedy = $"Run '{help}' for usage.";

        if (RawArguments.WantsJson(args))
        {
            return AsEnvelope(parse, args, verb, remedy);
        }

        foreach (var error in parse.Errors)
        {
            Console.Error.WriteLine($"winlogrotate: {error.Message}");
        }

        Console.Error.WriteLine(remedy);

        return ExitCode.ConfigInvalid;
    }

    /// <summary>
    /// The same refusal, in the channel the caller asked for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A mistyped flag used to produce no envelope at all - stderr and exit 2 - so
    /// <c>ConvertFrom-Json</c> failed outright on the response to a typo.
    /// <c>README.md</c> says "Everything speaks <c>--json</c> for scripting" and
    /// <c>EnvelopeDetails</c> says a <c>--json</c> verb "writes everything to stdout and nothing
    /// to stderr"; this was the exception, and it is the one a script is likeliest to hit first.
    /// </para>
    /// <para>
    /// To the <c>--output</c> file where one was named, for the same reason
    /// <c>CommandContext</c> sends a verb's envelope there: the caller that names one is an
    /// elevated child whose stdout nobody can read.
    /// </para>
    /// <para>
    /// The exit code does not change. A parse error means nothing was attempted, which is what
    /// <see cref="ExitCode.ConfigInvalid"/> says, and the argument for keeping it distinct from 1
    /// is unaffected by how it is rendered.
    /// </para>
    /// </remarks>
    private static int AsEnvelope(ParseResult parse, IReadOnlyList<string> args, string verb, string remedy)
    {
        var sink = new JsonOutputSink(verbose: false, stream: false, RawArguments.OpenOutput(args));

        foreach (var error in parse.Errors)
        {
            sink.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Error,
                Code = DiagnosticCode.ArgumentUnusable,
                Message = error.Message,
                Remedy = remedy,
            });
        }

        return sink.Complete<EmptyResult>(verb, ExitCode.ConfigInvalid, null);
    }
}
