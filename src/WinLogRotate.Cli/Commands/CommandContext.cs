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
            return new CommandContext(new TextOutputSink(verbose, color), parse);
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

        return new CommandContext(new JsonOutputSink(verbose, stream, streamTo), parse);
    }
}
