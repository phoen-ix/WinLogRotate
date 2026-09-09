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

        if (IsRedirected)
        {
            return FromPiped(ReadAllText(), out value, out error);
        }

        var text = ReadTyped(prompt, out error);

        // "Cancelled." and "The two entries did not match." come from the typed path and are more
        // specific than anything SecretInput could say, so they survive.
        return text is not null && SecretInput.Validate(text, out value, out error);
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
    private string ReadPiped() => SecretInput.StripOneNewline(ReadAllText());

    /// <summary>
    /// The piped path's rules, without the console.
    /// </summary>
    /// <remarks>
    /// A seam, and a deliberate one. Console.OpenStandardInput cannot be redirected from a test,
    /// so without this the only way to cover the console reader is to assert against SecretInput
    /// directly - which proves the rules work and says nothing about whether this reader still
    /// applies them. That is the shape of test that goes green after somebody stops calling them.
    /// </remarks>
    internal static bool FromPiped(string raw, out SecretString value, out string? error) =>
        SecretInput.Validate(SecretInput.StripOneNewline(raw), out value, out error);

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
