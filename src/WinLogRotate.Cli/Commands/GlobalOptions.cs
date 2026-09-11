using System.CommandLine;

namespace WinLogRotate.Cli.Commands;

/// <summary>
/// Options every verb accepts. Declared once and added to each command, so `--json` means
/// the same thing everywhere and the GUI can rely on it.
/// </summary>
internal static class GlobalOptions
{
    public static readonly Option<bool> Json =
        new("--json") { Description = "Emit one machine-readable envelope on stdout instead of text." };

    public static readonly Option<bool> JsonStream =
        new("--json-stream") { Description = "Emit newline-delimited JSON events as work happens. Implies --json." };

    public static readonly Option<FileInfo?> Output =
        new("--output") { Description = "Write the JSON stream to this file instead of stdout. Used by the GUI: an elevated \"runas\" child cannot have its pipes redirected, so it writes to a file the GUI tails." };

    public static readonly Option<bool> Verbose =
        new("--verbose", "-v") { Description = "Report what is happening, not just what went wrong." };

    public static readonly Option<bool> NoColor =
        new("--no-color") { Description = "Suppress ANSI colour. Implied when stdout is redirected." };

    public static readonly Option<bool> NoEventLog =
        new("--no-event-log") { Description = "Do not mirror warnings and errors to the Windows Event Log." };

    public static readonly Option<DirectoryInfo?> ConfigDir =
        new("--config-dir") { Description = "Override the configuration directory. Defaults to the installed location." };

    /// <param name="configDir">
    /// False for a verb that does not read configuration. Eight commands declared
    /// <c>--config-dir</c> and never passed it to <c>InstallPaths.Resolve</c>, so it parsed, was
    /// accepted, appeared in --help and changed nothing - the same shape as the unread options
    /// milestone 15 deleted from the command tree. <c>glob</c> is the one where it looked
    /// plausible: it tests a single pattern typed on a command line, builds its own guard, and
    /// has no job, so there is nothing in a configuration directory for it to read.
    /// </param>
    public static void AddTo(Command command, bool configDir = true)
    {
        command.Options.Add(Json);
        command.Options.Add(JsonStream);
        command.Options.Add(Output);
        command.Options.Add(Verbose);
        command.Options.Add(NoColor);
        command.Options.Add(NoEventLog);

        if (configDir)
        {
            command.Options.Add(ConfigDir);
        }
    }
}
