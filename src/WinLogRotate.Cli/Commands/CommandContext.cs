using System.CommandLine;
using System.CommandLine.Parsing;
using WinLogRotate.Cli.Output;
using WinLogRotate.Contracts;
using WinLogRotate.Core;

namespace WinLogRotate.Cli.Commands;

/// <summary>Everything a verb's handler needs, built once from the parse result.</summary>
internal sealed class CommandContext(IOutputSink output, ParseResult parse)
{
    public IOutputSink Output { get; } = output;
    public ParseResult Parse { get; } = parse;

    /// <summary>
    /// Runs a verb, and makes sure it says something whatever happens.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing used to catch anything. RunCommand has no try across fifteen unguarded I/O,
    /// registry, ACL, mutex and network calls; none of the tree's actions had one; Main had
    /// none. What caught an escape instead was System.CommandLine's default handler, which
    /// printed a stack-free line to a stderr the GUI cannot read and returned exit 1 - the code
    /// this product defines as "the run happened and something went wrong". A crash before the
    /// rotation gate was even taken reported itself to monitoring as a rotation that happened.
    /// ParseErrorReporter already refused that exact bargain for parse errors; this is the other
    /// half of it.
    /// </para>
    /// <para>
    /// The verb goes in a lambda rather than being called with the context as an argument, and
    /// that is not cosmetic. Every action used to read <c>From(parse)</c> as its first argument
    /// while its other arguments - GetRequiredValue, SecretPlatform.ForThisMachine, an input
    /// source - were evaluated afterwards, with the --output file already open. A throw there
    /// escaped with the handle held. Inside the lambda they are inside the try.
    /// </para>
    /// <para>
    /// From is private so that this is the only way to obtain a context: a rule the compiler
    /// keeps is worth more than one a reviewer has to.
    /// </para>
    /// </remarks>
    public static int Guarded(ParseResult parse, Func<CommandContext, int> verb)
    {
        CommandContext ctx;

        try
        {
            ctx = From(parse);
        }
        catch (Exception e)
        {
            // The context itself could not be built, which in practice means --output named a
            // file that could not be opened. Nothing was opened, so nothing is leaked - and a
            // --json caller still gets an envelope, on stdout, because that is the one channel
            // that is definitely there.
            return Report(From(parse, useOutputFile: false), parse, e);
        }

        try
        {
            return verb(ctx);
        }
        catch (Exception e)
        {
            return Report(ctx, parse, e);
        }
    }

    /// <summary>Says what went wrong through whatever channels this invocation asked for.</summary>
    private static int Report(CommandContext ctx, ParseResult parse, Exception e)
    {
        ctx.Output.Diagnostic(new CliDiagnostic
        {
            Severity = Severity.Error,
            Code = DiagnosticCode.InternalError,

            // The type as well as the message, because neither is enough alone here.
            // StackTraceSupport is off, so the type name is the only structural clue that
            // survives; and UseSystemResourceKeys is on, so a framework exception's Message is
            // its resource key rather than a sentence - "IO_FileExists_Name, C:\path" and not
            // "The file already exists". Both are deliberate size decisions for the AOT binary
            // and neither is worth undoing for this, but the key is stable and greppable, which
            // for a diagnostic an operator pastes into a bug report is arguably the better half
            // of the trade.
            Message = $"{e.GetType().Name}: {e.Message}",
            Remedy = "This is a defect. Nothing about what was or was not done can be relied on; "
                   + "check the journal for what the run had reached.",
        });

        return ctx.Output.Complete<EmptyResult>(VerbName(parse), ExitCode.InternalError, null);
    }

    /// <summary>
    /// The verb as a person wrote it, so "notify status" does not come back as "status".
    /// </summary>
    /// <remarks>
    /// Walks up from the command that was parsed and drops the root, which is what every verb
    /// already passes to Complete by hand.
    /// </remarks>
    private static string VerbName(ParseResult parse)
    {
        var parts = new List<string>();

        for (var result = parse.CommandResult; result is not null; result = result.Parent as CommandResult)
        {
            parts.Insert(0, result.Command.Name);
        }

        // The root is the executable, not a verb. A bare invocation keeps it rather than
        // reporting an empty string.
        return parts.Count > 1 ? string.Join(' ', parts.Skip(1)) : parts[0];
    }

    /// <summary>Builds the sink the invocation asked for.</summary>
    private static CommandContext From(ParseResult parse, bool useOutputFile = true)
    {
        var verbose = parse.GetValue(GlobalOptions.Verbose);
        var stream = parse.GetValue(GlobalOptions.JsonStream);
        var json = parse.GetValue(GlobalOptions.Json) || stream;
        var outputFile = useOutputFile ? parse.GetValue(GlobalOptions.Output) : null;

        if (!json)
        {
            // Colour off when redirected: nobody wants escape codes in a log file, and
            // Task Scheduler captures stdout verbatim.
            var color = !parse.GetValue(GlobalOptions.NoColor) && !Console.IsOutputRedirected;
            return new CommandContext(WithEventLog(new TextOutputSink(verbose, color), parse), parse);
        }

        TextWriter? streamTo = null;
        if (outputFile is not null)
        {
            // Share Delete as well as ReadWrite: the GUI tails this file while we append, and
            // sweeps the directory afterwards. Denying it would make the sweep fail.
            var fs = new FileStream(outputFile.FullName, FileMode.Create, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            streamTo = new StreamWriter(fs) { AutoFlush = true };
        }

        // Deliberately no Event Log mirroring on the JSON paths. --json means the caller reads
        // the output itself, and the caller is the GUI: it polls "doctor --json" from four
        // different pages on every refresh. On a machine with a loosened conf.d that is a
        // Critical diagnostic, so mirroring it would put an event in the Application log every
        // time somebody clicked a tab - which is precisely how an administrator decides this
        // source is noise and filters away the one event that mattered. The scheduled task
        // passes no --json, so the run that actually needs a record still gets one.
        return new CommandContext(new JsonOutputSink(verbose, stream, streamTo), parse);
    }

    /// <summary>
    /// Wraps the sink so warnings and errors also reach the Windows Event Log.
    /// </summary>
    /// <remarks>
    /// The platform check is what CA1416 needs to see; <see cref="EventLogWriter"/> also
    /// tolerates an unregistered source, which is the normal state after a per-user install.
    /// </remarks>
    private static IOutputSink WithEventLog(IOutputSink inner, ParseResult parse)
    {
        if (!OperatingSystem.IsWindows() || parse.GetValue(GlobalOptions.NoEventLog))
        {
            return inner;
        }

        return new EventLogSink(inner);
    }
}
