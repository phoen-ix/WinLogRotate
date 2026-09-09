using System.Text;
using WinLogRotate.Core.Secrets;

namespace WinLogRotate.Cli.Output;

/// <summary>
/// <see cref="IOutputSink"/>'s symmetric counterpart: the one place the CLI reads from a person.
/// </summary>
/// <remarks>
/// <para>
/// It lives beside the sinks because <c>ArchitectureTests.OnlyTheOutputSinksTouchTheConsole</c>
/// permits console access only here, and its pattern matches <c>Console.Read</c> as well as the
/// writes. That is the correct home rather than a way around the rule: console input is console
/// I/O, and keeping it behind an interface is also what makes a verb that reads a password
/// testable at all.
/// </para>
/// <para>
/// This exists because a secret must never arrive as a command-line argument. A command line is
/// readable by every local administrator through <c>Win32_Process</c>, is recorded in 4688
/// process-creation audit events wherever command-line auditing is on, and is captured by
/// essentially every EDR agent. There is deliberately no <c>--value</c>.
/// </para>
/// </remarks>
public interface IInputSource
{
    /// <summary>True when standard input is a pipe or a file rather than a keyboard.</summary>
    bool IsRedirected { get; }

    /// <summary>
    /// Reads one secret: from standard input when redirected, otherwise prompted and unechoed.
    /// </summary>
    /// <returns>False if nothing was given, it was too long, or the confirmation differed.</returns>
    bool TryReadSecret(string prompt, out SecretString value, out string? error);

    /// <summary>All of standard input, for <c>secret import</c>.</summary>
    string ReadAllText();
}

/// <summary>The real console.</summary>
internal sealed class ConsoleInputSource : IInputSource
{
    /// <summary>
    /// Long enough for any credential anybody actually has, short enough that piping a file in
    /// by mistake is refused rather than stored.
    /// </summary>
    internal const int MaxSecretChars = 4096;

    public bool IsRedirected => Console.IsInputRedirected;

    public string ReadAllText()
    {
        // Decoded as UTF-8 explicitly rather than through Console.In, whose encoding is the
        // console code page and therefore depends on what the operator's shell happened to be
        // doing. A password is not a place for "depends".
        using var stream = Console.OpenStandardInput();
        using var reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return reader.ReadToEnd();
    }

    public bool TryReadSecret(string prompt, out SecretString value, out string? error)
    {
        value = SecretString.None;
        error = null;

        var text = IsRedirected ? ReadPiped() : ReadTyped(prompt, out error);
        if (text is null)
        {
            error ??= "No value was given.";
            return false;
        }

        if (text.Length == 0)
        {
            error = "An empty secret is not stored. Use 'secret remove' to delete one.";
            return false;
        }

        if (text.Length > MaxSecretChars)
        {
            error = $"That is {text.Length:N0} characters, above the {MaxSecretChars:N0} limit. "
                  + "If you meant to store a file, store its contents deliberately with --from-file.";
            return false;
        }

        error = null;
        value = SecretString.From(text);
        return true;
    }

    /// <summary>
    /// Reads piped input, stripping at most one trailing newline.
    /// </summary>
    /// <remarks>
    /// One newline, and nothing else. A password may legitimately end in a space, and trimming
    /// whitespace to be helpful would silently store something other than what the operator
    /// piped in - the worst possible failure here, because it only shows up as an
    /// authentication error somewhere else entirely. <c>secret test</c> reports the length for
    /// exactly this reason.
    /// </remarks>
    private string ReadPiped()
    {
        var text = ReadAllText();

        if (text.EndsWith("\r\n", StringComparison.Ordinal))
        {
            return text[..^2];
        }

        return text.EndsWith('\n') ? text[..^1] : text;
    }

    private static string? ReadTyped(string prompt, out string? error)
    {
        error = null;

        var first = Prompt(prompt);
        if (first is null)
        {
            error = "Cancelled.";
            return null;
        }

        var again = Prompt("Repeat it");
        if (again is null)
        {
            error = "Cancelled.";
            return null;
        }

        if (!string.Equals(first, again, StringComparison.Ordinal))
        {
            error = "The two entries did not match.";
            return null;
        }

        return first;
    }

    /// <summary>Reads a line without echoing it. Null if the operator pressed Escape or Ctrl+C.</summary>
    private static string? Prompt(string prompt)
    {
        Console.Error.Write($"{prompt}: ");
        var builder = new StringBuilder();

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    Console.Error.WriteLine();
                    return builder.ToString();

                case ConsoleKey.Escape:
                    Console.Error.WriteLine();
                    return null;

                case ConsoleKey.Backspace:
                    if (builder.Length > 0)
                    {
                        builder.Length--;
                    }

                    break;

                default:
                    // Not "is it a letter": a password can contain anything the keyboard emits.
                    // Control characters other than the ones handled above are ignored, which is
                    // what stops an arrow key from becoming part of the value.
                    if (!char.IsControl(key.KeyChar))
                    {
                        builder.Append(key.KeyChar);
                    }

                    break;
            }

            // Nothing is echoed at all, not even asterisks: a shoulder-surfer counting them
            // learns the length, and the length is most of what they need.
            if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key == ConsoleKey.C)
            {
                Console.Error.WriteLine();
                return null;
            }
        }
    }
}
