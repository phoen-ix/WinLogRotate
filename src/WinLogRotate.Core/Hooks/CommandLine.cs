using WinLogRotate.Core.Safety;

namespace WinLogRotate.Core.Hooks;

/// <summary>Why a command line could not be turned into a program and its arguments.</summary>
public enum CommandLineError
{
    None,

    /// <summary>Blank.</summary>
    Empty,

    /// <summary>Where the program ends cannot be determined without guessing.</summary>
    Ambiguous,

    /// <summary>Not an absolute path, so which program runs would depend on PATH.</summary>
    NotAbsolute,

    /// <summary>An opening quote with no closing one.</summary>
    Unterminated,

    /// <summary>A batch file, which CreateProcess cannot start on its own.</summary>
    NotExecutable,
}

/// <summary>
/// Splits a hook's command line into a program and its arguments, or refuses to.
/// </summary>
/// <remarks>
/// <para>
/// There is no shell - <see cref="Notify.HookScheme.Command"/>'s own doc comment says "Run a
/// program. No shell, ever." - so something has to decide where the program ends and its
/// arguments begin. For <c>C:\Program Files\Reload\go.exe --now</c> that is genuinely ambiguous,
/// and this <b>refuses rather than guesses</b>.
/// </para>
/// <para>
/// Deliberately not Windows' own rule. <c>CreateProcess</c>, handed an unquoted path with spaces,
/// probes <c>C:\Program.exe</c> first and only then <c>C:\Program Files\Reload\go.exe</c>. That
/// behaviour is a well-known privilege-escalation class: anyone who can write <c>C:\</c> wins, and
/// this code path runs as SYSTEM from a scheduled task. An operator who quotes the program gets a
/// working hook; one who does not gets a refusal naming the fix. Neither gets a surprise.
/// </para>
/// <para>
/// The program must also be an absolute path. <c>TaskRunHost</c> already carries the reason:
/// "resolving a bare name through PATH would let a planted executable in a writable directory run
/// as SYSTEM." <c>command:net stop x</c> is refused; <c>command:C:\Windows\System32\net.exe stop
/// x</c> is not, and says out loud which program it means.
/// </para>
/// </remarks>
public static class CommandLine
{
    /// <summary>
    /// Splits <paramref name="commandLine"/>, or explains why it will not.
    /// </summary>
    /// <param name="exists">
    /// Whether a path names a file. Injected so the whole of this runs on the Linux leg - it is
    /// the rule that decides what executes as SYSTEM, so it deserves tests that need no Windows.
    /// </param>
    public static bool TrySplit(
        string commandLine, Func<string, bool> exists,
        out string program, out string[] arguments, out CommandLineError error, out string? detail)
    {
        program = string.Empty;
        arguments = [];
        error = CommandLineError.None;
        detail = null;

        var text = commandLine.Trim();

        if (text.Length == 0)
        {
            error = CommandLineError.Empty;
            detail = "The hook names no program.";
            return false;
        }

        if (text[0] == '"')
        {
            var close = text.IndexOf('"', 1);
            if (close < 0)
            {
                error = CommandLineError.Unterminated;
                detail = "The program is opened with a quote that is never closed.";
                return false;
            }

            program = text[1..close];
            arguments = SplitArguments(text[(close + 1)..]);
            return Absolute(ref program, ref error, ref detail);
        }

        var space = text.IndexOf(' ');

        if (space < 0)
        {
            // No arguments, so nothing to be ambiguous about.
            program = text;
            return Absolute(ref program, ref error, ref detail);
        }

        var head = text[..space];

        // An extension settles it without asking the disk anything, which is the better answer
        // twice over: a hook is accepted or refused identically on every machine, and whoever
        // reviews the configuration file can tell which it will be by reading it. The probe below
        // only has to cover programs written without one.
        //
        // Nothing is ever appended. CreateProcess' rule - try "C:\Program", then "C:\Program.exe"
        // - is the whole reason this type exists.
        if (LooksExecutable(head) || exists(head))
        {
            program = head;
            arguments = SplitArguments(text[space..]);
            return Absolute(ref program, ref error, ref detail);
        }

        // The whole string might be one path containing spaces and no arguments at all.
        if (LooksExecutable(text) || exists(text))
        {
            program = text;
            return Absolute(ref program, ref error, ref detail);
        }

        error = CommandLineError.Ambiguous;
        detail = $"'{head}' is not a program, so where '{text}' ends and its arguments begin "
               + "cannot be determined.";
        return false;
    }

    /// <summary>
    /// Whether a token ends in an extension that makes it the program rather than an argument.
    /// </summary>
    /// <remarks>
    /// <c>.bat</c> and <c>.cmd</c> are here even though <c>CreateProcess</c> cannot start one.
    /// Recognising them is what lets <see cref="Executable"/> refuse them <i>by name</i>: leave
    /// them out and <c>C:\tools\reload.bat --now</c> comes back as merely ambiguous, which sends
    /// somebody off to add quotes that will not help.
    /// </remarks>
    private static bool LooksExecutable(string token) =>
        token.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        || token.EndsWith(".com", StringComparison.OrdinalIgnoreCase)
        || token.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
        || token.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);

    private static bool Executable(ref string program, ref CommandLineError error, ref string? detail)
    {
        if (!program.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
            && !program.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        error = CommandLineError.NotExecutable;
        detail = $"'{program}' is a batch file, and Windows cannot start one directly.";
        return false;
    }

    /// <summary>The remedy for a refusal, in the operator's own terms.</summary>
    public static string Remedy(CommandLineError error, string commandLine) => error switch
    {
        CommandLineError.Ambiguous =>
            "Quote the program: command:\"C:\\Program Files\\App\\reload.exe\" --now",

        CommandLineError.NotAbsolute =>
            "Give the full path, so which program runs cannot depend on PATH: "
            + "command:C:\\Windows\\System32\\net.exe stop MyService",

        CommandLineError.NotExecutable =>
            "Run it through the interpreter that understands it: "
            + $"command:C:\\Windows\\System32\\cmd.exe /c \"{commandLine}\"",

        CommandLineError.Unterminated => "Close the quote around the program.",
        _ => "Write the hook as command:C:\\path\\to\\program.exe arguments.",
    };

    private static bool Absolute(ref string program, ref CommandLineError error, ref string? detail)
    {
        if (WinPath.IsAbsolute(program))
        {
            return Executable(ref program, ref error, ref detail);
        }

        error = CommandLineError.NotAbsolute;
        detail = $"'{program}' is not an absolute path, so which program runs would depend on PATH.";
        return false;
    }

    /// <summary>
    /// Splits the argument tail on whitespace, with double quotes grouping.
    /// </summary>
    /// <remarks>
    /// The result is handed to <c>ProcessStartInfo.ArgumentList</c>, which re-quotes each element
    /// correctly on the way out - so this only has to recover what the operator meant, not produce
    /// something a command interpreter would accept. There is no interpreter involved at any
    /// point, which is the property the whole scheme is built on.
    /// </remarks>
    private static string[] SplitArguments(string tail)
    {
        var arguments = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        var any = false;

        foreach (var c in tail)
        {
            if (c == '"')
            {
                quoted = !quoted;
                any = true;
                continue;
            }

            if (!quoted && char.IsWhiteSpace(c))
            {
                if (any)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                    any = false;
                }

                continue;
            }

            current.Append(c);
            any = true;
        }

        if (any)
        {
            arguments.Add(current.ToString());
        }

        return [.. arguments];
    }
}
