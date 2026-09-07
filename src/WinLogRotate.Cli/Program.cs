using WinLogRotate.Cli.Commands;
using WinLogRotate.Cli.Output;

namespace WinLogRotate.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        var parse = CommandTree.Build().Parse(args);

        // Handle parse failures ourselves rather than letting System.CommandLine's default
        // action exit 1 - see ParseErrorReporter for why that distinction matters.
        return parse.Errors.Count > 0
            ? ParseErrorReporter.Report(parse)
            : parse.Invoke();
    }
}
