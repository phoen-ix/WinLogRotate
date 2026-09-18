namespace WinLogRotate.Cli.Output;

/// <summary>
/// What the global options said, read from the raw arguments because no parse can be asked.
/// </summary>
/// <remarks>
/// <para>
/// The two reporters that use this are the two places in the product that cannot ask
/// <c>ParseResult</c> what the caller wanted: <see cref="ParseErrorReporter"/> is asked
/// <i>because</i> the parse did not succeed, and a mistyped flag anywhere on the line can leave
/// <c>--json</c> unbound; <see cref="UnhandledReporter"/> may be asked before the command tree
/// was even built. Scanning the tokens is crude and is the only thing available.
/// </para>
/// <para>
/// One scan for both, because the first version honoured <c>--json</c> and not <c>--output</c>,
/// and the one caller that passes <c>--output</c> is the GUI's elevated child - whose console
/// cannot be read by the process that started it. Its parse errors went to a stdout nobody could
/// see, the events file stayed empty, and the dialog said "the configuration has errors" with
/// nothing underneath.
/// </para>
/// </remarks>
internal static class RawArguments
{
    /// <summary>
    /// Whether the caller asked for an envelope.
    /// </summary>
    /// <remarks>
    /// <c>--json-stream</c> counts, because <c>GlobalOptions</c> documents it as implying
    /// <c>--json</c>.
    /// </remarks>
    public static bool WantsJson(IReadOnlyList<string> args) =>
        args.Any(a => a is "--json" or "--json-stream");

    /// <summary>
    /// The file <c>--output</c> named, or null where it named nothing.
    /// </summary>
    /// <remarks>
    /// The value is the next token, or the rest of the same one after <c>=</c> or <c>:</c>, which
    /// are the three spellings System.CommandLine accepts. A next token that is itself an option
    /// is not a value: <c>--output --json</c> is the parse error being reported, not a request to
    /// write a file called <c>--json</c>.
    /// </remarks>
    public static string? OutputPath(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var token = args[i];

            if (token == "--output")
            {
                return i + 1 < args.Count && !args[i + 1].StartsWith('-') ? args[i + 1] : null;
            }

            if (token.StartsWith("--output=", StringComparison.Ordinal)
                || token.StartsWith("--output:", StringComparison.Ordinal))
            {
                var value = token["--output=".Length..];
                return value.Length > 0 ? value : null;
            }
        }

        return null;
    }

    /// <summary>
    /// The writer <c>--output</c> asked for, opened the way <c>CommandContext</c> opens it, or
    /// null so the sink falls back to stdout.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Share Delete as well as ReadWrite, for the same reason as there: the GUI tails this file
    /// while it is written and sweeps the directory afterwards.
    /// </para>
    /// <para>
    /// A file that cannot be opened is not a reason to say nothing. The guard makes the same
    /// choice when the context cannot be built: stdout is the one channel that is definitely
    /// there, so the envelope goes there rather than nowhere. Everything is caught, because this
    /// runs inside the last two handlers the process has and a throw here would leave the exit
    /// code to the runtime.
    /// </para>
    /// </remarks>
    public static TextWriter? OpenOutput(IReadOnlyList<string> args)
    {
        if (OutputPath(args) is not { } path)
        {
            return null;
        }

        try
        {
            var fs = new FileStream(Path.GetFullPath(path), FileMode.Create, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            return new StreamWriter(fs) { AutoFlush = true };
        }
        catch (Exception)
        {
            return null;
        }
    }
}
