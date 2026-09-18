using System.CommandLine;
using WinLogRotate.Cli.Commands;
using WinLogRotate.Contracts;
using WinLogRotate.Core;

namespace WinLogRotate.Cli.Output;

/// <summary>
/// The last resort, for an exception that escaped even the guard.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Commands.CommandContext"/>'s guard catches anything a verb throws and turns it
/// into a diagnostic and an envelope, which is the answer for every case that has a sink to
/// report through. This is for the cases that do not: building the command tree, parsing, and
/// whatever System.CommandLine itself may throw before an action is reached.
/// </para>
/// <para>
/// Under <c>Output/</c> for the same reason <see cref="ParseErrorReporter"/> is - the
/// architecture test that bans bare Console elsewhere in the CLI is deliberately not weakened
/// for it.
/// </para>
/// </remarks>
internal static class UnhandledReporter
{
    /// <param name="parse">
    /// The parse, where one was reached. Null means the tree itself could not be built, and the
    /// envelope then carries the executable's name where a verb would go, as a bare invocation's
    /// parse error does.
    /// </param>
    public static int Report(Exception e, IReadOnlyList<string> args, ParseResult? parse = null)
    {
        // The type as well as the message. StackTraceSupport is off under NativeAOT, so the
        // type name is the only structural clue anyone gets, and "Access to the path ... is
        // denied" alone does not say which of a dozen things was denied.
        if (RawArguments.WantsJson(args))
        {
            return AsEnvelope(e, args, parse);
        }

        Console.Error.WriteLine($"winlogrotate: {e.GetType().Name}: {e.Message}");
        Console.Error.WriteLine(
            "This is a defect. Nothing about what was or was not done can be relied on.");

        return ExitCode.InternalError;
    }

    /// <summary>
    /// The same report, as the envelope a <c>--json</c> caller is reading for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This wrote to stderr whatever the caller asked, so a script that had been promised "one
    /// object on stdout" got an empty stdout and exit 4, and the GUI - which reads the
    /// <c>--output</c> file and nothing else - got an empty file. <c>ExitCode.InternalError</c>
    /// is the one code whose remedy says nothing can be relied on, which makes it the one a
    /// caller most needs to see written down.
    /// </para>
    /// <para>
    /// The diagnostic is the guard's own, in shape and in code, so a defect looks the same to a
    /// reader whichever side of the guard it fell on.
    /// </para>
    /// </remarks>
    private static int AsEnvelope(Exception e, IReadOnlyList<string> args, ParseResult? parse)
    {
        var sink = new JsonOutputSink(verbose: false, stream: false, RawArguments.OpenOutput(args));

        sink.Diagnostic(new CliDiagnostic
        {
            Severity = Severity.Error,
            Code = DiagnosticCode.InternalError,
            Message = $"The invocation failed before its verb could report: {e.GetType().Name}: {e.Message}",
            Remedy = "This is a defect. Nothing about what was or was not done can be relied on; "
                   + "please report it, with the command line you used.",
        });

        var verb = parse is null ? RootCommand.ExecutableName : CommandContext.VerbName(parse);

        return sink.Complete<EmptyResult>(verb, ExitCode.InternalError, null);
    }
}
