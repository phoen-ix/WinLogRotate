using System.CommandLine;
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
        var verb = parse.CommandResult.Command.Name;
        var help = verb is "winlogrotate" or "" ? "winlogrotate --help" : $"winlogrotate {verb} --help";
        var remedy = $"Run '{help}' for usage.";

        if (WantsJson(args))
        {
            return AsEnvelope(parse, verb, remedy);
        }

        foreach (var error in parse.Errors)
        {
            Console.Error.WriteLine($"winlogrotate: {error.Message}");
        }

        Console.Error.WriteLine(remedy);

        return ExitCode.ConfigInvalid;
    }

    /// <summary>
    /// Read from the raw arguments, because the parse that would have told us failed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the one place in the product that cannot ask <c>ParseResult</c> what the caller
    /// wanted: the question is being asked <i>because</i> the parse did not succeed, and a
    /// mistyped flag anywhere on the line can leave <c>--json</c> unbound. Scanning the tokens is
    /// crude and is the only thing available.
    /// </para>
    /// <para>
    /// <c>--json-stream</c> counts, because <c>GlobalOptions</c> documents it as implying
    /// <c>--json</c>.
    /// </para>
    /// </remarks>
    private static bool WantsJson(IReadOnlyList<string> args) =>
        args.Any(a => a is "--json" or "--json-stream");

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
    /// The exit code does not change. A parse error means nothing was attempted, which is what
    /// <see cref="ExitCode.ConfigInvalid"/> says, and the argument for keeping it distinct from 1
    /// is unaffected by how it is rendered.
    /// </para>
    /// </remarks>
    private static int AsEnvelope(ParseResult parse, string verb, string remedy)
    {
        var sink = new JsonOutputSink(verbose: false, stream: false);

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
