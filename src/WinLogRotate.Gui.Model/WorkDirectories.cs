namespace WinLogRotate.Gui.Cli;

/// <summary>
/// The scratch directories an elevated child writes its event stream into, and when one is
/// safe to remove.
/// </summary>
/// <remarks>
/// <para>
/// <c>CliRunner</c> deletes each directory when its child exits, and says that whatever it could
/// not delete - a child still exiting and holding its file - is swept on the next start. Nothing
/// swept anything: the comment described a mechanism that did not exist, and every such directory
/// was left in the operator's temporary folder for ever.
/// </para>
/// <para>
/// The decision lives here, away from the file system, so that the one part of the sweep that can
/// be wrong in an interesting way - deleting the directory of a child that is still running - is
/// asserted where the tests run.
/// </para>
/// </remarks>
public static class WorkDirectories
{
    /// <summary>What every scratch directory's name starts with.</summary>
    public const string Prefix = "WinLogRotate-op-";

    /// <summary>The pattern that finds them, and nothing else, in a temporary folder.</summary>
    public const string Pattern = Prefix + "*";

    /// <summary>
    /// How long a directory nobody has written to is left alone.
    /// </summary>
    /// <remarks>
    /// A child that is alive appends to its file as it works, so its directory is never this old
    /// while it has anything left to say. A GUI that was killed mid-operation leaves one that is.
    /// </remarks>
    public static readonly TimeSpan Grace = TimeSpan.FromHours(1);

    /// <summary>The name for one operation's directory.</summary>
    public static string Name(Guid operation) => $"{Prefix}{operation:N}";

    /// <summary>
    /// Whether a directory last written at <paramref name="lastWrite"/> may be removed.
    /// </summary>
    /// <remarks>
    /// A write in the future - a clock that was set back - is treated as recent. Deleting the
    /// directory of a running child because the clock moved would be the worse mistake.
    /// </remarks>
    public static bool IsStale(DateTimeOffset lastWrite, DateTimeOffset now) => now - lastWrite > Grace;
}
