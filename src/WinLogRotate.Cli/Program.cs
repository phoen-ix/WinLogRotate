using System.CommandLine;
using WinLogRotate.Cli.Commands;
using WinLogRotate.Cli.Output;

namespace WinLogRotate.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            var parse = CommandTree.Build().Parse(args);

            // Handle parse failures ourselves rather than letting System.CommandLine's default
            // action exit 1 - see ParseErrorReporter for why that distinction matters.
            //
            // The same argument disables the library's exception handler. It returns 1 too, and 1
            // is the code that says a run happened; every verb is wrapped by CommandContext.Guarded
            // and reports through its own sink, so nothing should reach the library's net - and if
            // something does, it must not be quietly relabelled as a rotation.
            return parse.Errors.Count > 0
                ? ParseErrorReporter.Report(parse, args)
                : parse.Invoke(new InvocationConfiguration { EnableDefaultExceptionHandler = false });
        }
        catch (Exception e)
        {
            // Building the tree and parsing happen outside every action, so the guard cannot see
            // them. CommandTree calls SecretPlatform.ForThisMachine() while constructing the
            // secret verb, which is the one thing here that touches the machine.
            return UnhandledReporter.Report(e);
        }
    }
}
