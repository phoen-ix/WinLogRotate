using System.CommandLine;
using WinLogRotate.Cli.Output;

namespace WinLogRotate.Cli.Commands;

/// <summary>Everything a verb's handler needs, built once from the parse result.</summary>
internal sealed class CommandContext(IOutputSink output, ParseResult parse)
{
    public IOutputSink Output { get; } = output;
    public ParseResult Parse { get; } = parse;

    /// <summary>Builds the sink the invocation asked for.</summary>
    public static CommandContext From(ParseResult parse)
    {
        var verbose = parse.GetValue(GlobalOptions.Verbose);
        var stream = parse.GetValue(GlobalOptions.JsonStream);
        var json = parse.GetValue(GlobalOptions.Json) || stream;
        var outputFile = parse.GetValue(GlobalOptions.Output);

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
