namespace WinLogRotate.Gui.Cli;

/// <summary>
/// A rectangle in logical pixels, with none of <c>System.Drawing</c> attached.
/// </summary>
/// <remarks>
/// This project must not acquire a drawing dependency - the same reason <see cref="CliIdentity"/>
/// reports a <c>Severity</c> rather than a colour. A form turns one of these into a
/// <c>Rectangle</c> in one line; a test reads it as four numbers.
/// </remarks>
public readonly record struct Box(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    /// <summary>Whether the two share any area. Touching edges do not count.</summary>
    public bool Overlaps(Box other) =>
        X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom;

    /// <summary>Whether this lies entirely inside a client area of the given size.</summary>
    public bool Within(int width, int height) =>
        X >= 0 && Y >= 0 && Right <= width && Bottom <= height;
}
