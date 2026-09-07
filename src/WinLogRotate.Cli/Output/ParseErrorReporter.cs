using System.CommandLine;
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
    public static int Report(ParseResult parse)
    {
        foreach (var error in parse.Errors)
        {
            Console.Error.WriteLine($"winlogrotate: {error.Message}");
        }

        var verb = parse.CommandResult.Command.Name;
        var help = verb is "winlogrotate" or "" ? "winlogrotate --help" : $"winlogrotate {verb} --help";
        Console.Error.WriteLine($"Run '{help}' for usage.");

        return ExitCode.ConfigInvalid;
    }
}
