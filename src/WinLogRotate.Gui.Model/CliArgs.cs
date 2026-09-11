namespace WinLogRotate.Gui.Cli;

/// <summary>
/// Builds the command line for a verb the GUI runs.
/// </summary>
/// <remarks>
/// <para>
/// Here rather than on the form it grew on: every page used a static reachable only through
/// <c>MainForm</c>, and a builder that no test can see is a builder whose output no test can
/// check. The command lines this produces are now parsed by the real command tree in
/// <c>CliArgsTests</c>, which is the first time any of them has been.
/// </para>
/// <para>
/// The configuration directory is appended <b>unconditionally</b> whenever there is one. That is
/// right for every verb the GUI runs today and wrong for anything the root command owns -
/// <c>--version</c> most of all, which declares no <c>--config-dir</c> at all, so passing one
/// turns the invocation into a parse error that never reaches a handler and emits no envelope.
/// A caller that wants a root-level verb must not come through here.
/// </para>
/// </remarks>
public static class CliArgs
{
    public static string[] For(string? configDir, params string[] verb) =>
        configDir is null ? verb : [.. verb, "--config-dir", configDir];
}
