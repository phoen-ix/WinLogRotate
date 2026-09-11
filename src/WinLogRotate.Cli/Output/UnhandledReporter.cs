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
    public static int Report(Exception e)
    {
        // The type as well as the message. StackTraceSupport is off under NativeAOT, so the
        // type name is the only structural clue anyone gets, and "Access to the path ... is
        // denied" alone does not say which of a dozen things was denied.
        Console.Error.WriteLine($"winlogrotate: {e.GetType().Name}: {e.Message}");
        Console.Error.WriteLine(
            "This is a defect. Nothing about what was or was not done can be relied on.");

        return ExitCode.InternalError;
    }
}
