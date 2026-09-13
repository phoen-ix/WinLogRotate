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

/// <summary>One common field: the caption to its left, and the box it is edited in.</summary>
public sealed record LabelledBox(Box Caption, Box Editor);

/// <summary>
/// Where everything on the job editor goes, computed rather than typed.
/// </summary>
/// <remarks>
/// <para>
/// The form laid itself out top-down from constants and gave the advanced grid whatever height
/// was left: <c>470 - y - 130</c>, where <c>y</c> was the bottom of eight common rows. That was
/// -52. WinForms stores a negative height without complaint, so the grid's bottom edge landed
/// above its top, the status line was placed behind the last two rows of fields, and the
/// twenty-five keys the grid exists to show - <c>lockstrategy</c>, <c>dateext</c>,
/// <c>prerotate</c>, and the warning that a hook will never run here - were unreachable from
/// the window that promised them. The class comment said "nothing is unreachable either way".
/// </para>
/// <para>
/// Bottom-up, then: the grid is given a height, the status line sits under it, the buttons
/// under that, and the window is as tall as that needs. The common fields go in two columns so
/// the whole thing still fits a 768-pixel screen at 125 per cent. Every box is asserted positive,
/// disjoint and inside the client area by <c>JobEditorLayoutTests</c>, on the leg that cannot
/// open the form.
/// </para>
/// </remarks>
public sealed record JobEditorLayout
{
    private const int Margin = 16;
    private const int CaptionWidth = 96;
    private const int RowPitch = 30;
    private const int EditorHeight = 24;
    private const int CaptionHeight = 18;
    private const int ButtonWidth = 84;
    private const int ButtonHeight = 26;

    public required Box NameCaption { get; init; }
    public required Box Name { get; init; }
    public required Box PathsCaption { get; init; }
    public required Box Paths { get; init; }

    /// <summary>"One glob per line", beside the paths box rather than over it.</summary>
    public required Box PathsHint { get; init; }

    public required Box KindCaption { get; init; }
    public required Box Kind { get; init; }
    public required Box Enabled { get; init; }

    /// <summary>One slot per common key, in the order the model lists them.</summary>
    public required IReadOnlyList<LabelledBox> Common { get; init; }

    public required Box Advanced { get; init; }
    public required Box Status { get; init; }
    public required Box Check { get; init; }
    public required Box Save { get; init; }
    public required Box Cancel { get; init; }

    public required int ClientWidth { get; init; }
    public required int ClientHeight { get; init; }

    /// <summary>Every box, named, for a rule that has to look at all of them.</summary>
    public IEnumerable<(string Name, Box Box)> All
    {
        get
        {
            yield return ("name caption", NameCaption);
            yield return ("name", Name);
            yield return ("paths caption", PathsCaption);
            yield return ("paths", Paths);
            yield return ("paths hint", PathsHint);
            yield return ("kind caption", KindCaption);
            yield return ("kind", Kind);
            yield return ("enabled", Enabled);

            for (var i = 0; i < Common.Count; i++)
            {
                yield return ($"common {i} caption", Common[i].Caption);
                yield return ($"common {i} editor", Common[i].Editor);
            }

            yield return ("advanced", Advanced);
            yield return ("status", Status);
            yield return ("check", Check);
            yield return ("save", Save);
            yield return ("cancel", Cancel);
        }
    }

    /// <summary>The layout for a form with this many common fields.</summary>
    public static JobEditorLayout Compute(int commonFields)
    {
        const int Width = 620;
        const int Right = Width - Margin;
        const int Editor = Margin + CaptionWidth + 4;

        var common = new List<LabelledBox>();

        // Two columns. The right-hand column starts past the middle, and each editor runs to
        // its column's edge - the right one to the margin, so nothing sticks out.
        const int SecondCaption = 316;
        const int SecondEditor = SecondCaption + CaptionWidth + 4;

        var y = 152;

        for (var i = 0; i < commonFields; i++)
        {
            var top = y + (RowPitch * (i / 2));

            common.Add(i % 2 == 0
                ? new LabelledBox(
                    new Box(Margin, top + 4, CaptionWidth, CaptionHeight),
                    new Box(Editor, top, SecondCaption - 20 - Editor, EditorHeight))
                : new LabelledBox(
                    new Box(SecondCaption, top + 4, CaptionWidth, CaptionHeight),
                    new Box(SecondEditor, top, Right - SecondEditor, EditorHeight)));
        }

        var rows = (commonFields + 1) / 2;
        y += RowPitch * rows;

        var advanced = new Box(Margin, y + 8, Right - Margin, 180);
        var status = new Box(Margin, advanced.Bottom + 8, Right - Margin, 34);

        var buttons = status.Bottom + 8;
        var cancel = new Box(Right - ButtonWidth, buttons, ButtonWidth, ButtonHeight);
        var save = new Box(cancel.X - 8 - ButtonWidth, buttons, ButtonWidth, ButtonHeight);
        var check = new Box(save.X - 8 - ButtonWidth, buttons, ButtonWidth, ButtonHeight);

        return new JobEditorLayout
        {
            NameCaption = new Box(Margin, 20, CaptionWidth, CaptionHeight),
            Name = new Box(Editor, 16, 260, EditorHeight),
            PathsCaption = new Box(Margin, 52, CaptionWidth, CaptionHeight),
            Paths = new Box(Editor, 48, 340, 64),
            PathsHint = new Box(Editor + 340 + 8, 52, Right - (Editor + 340 + 8), CaptionHeight),
            KindCaption = new Box(Margin, 124, CaptionWidth, CaptionHeight),
            Kind = new Box(Editor, 120, 160, EditorHeight),
            Enabled = new Box(Editor + 160 + 20, 120, 120, EditorHeight),
            Common = common,
            Advanced = advanced,
            Status = status,
            Check = check,
            Save = save,
            Cancel = cancel,
            ClientWidth = Width,
            ClientHeight = cancel.Bottom + Margin,
        };
    }
}
