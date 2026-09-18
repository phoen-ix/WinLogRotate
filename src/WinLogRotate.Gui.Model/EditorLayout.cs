namespace WinLogRotate.Gui.Cli;

/// <summary>One typed row of the Advanced view, in the content panel's own coordinates.</summary>
public sealed record AdvancedRow(
    string Key, Box Caption, Box KeyLabel, Box Editor, Box? Unit, Box Source, Box Inherit, Box Description);

/// <summary>One group of the Advanced view: its heading and its rows.</summary>
public sealed record AdvancedSection(string Title, Box Header, IReadOnlyList<AdvancedRow> Rows);

/// <summary>A key the file writes that this build does not read: shown, and removable.</summary>
public sealed record ForeignRow(string Key, Box Text, Box Remove);

/// <summary>
/// Where everything in the job editor goes, for both of its views.
/// </summary>
/// <remarks>
/// <para>
/// One window, one size: a header that names the job and its files, a body that is either the
/// Basics view or the Advanced view, and the same status line, hint and buttons under both. The
/// toggle between the views is a visibility flip, never a resize, so nothing has to be placed
/// after the window has scaled.
/// </para>
/// <para>
/// The Advanced view is taller than any screen, so its rows are laid out in a virtual area that
/// a scrolling panel shows through <see cref="Viewport"/>. Fixed boxes are in client
/// coordinates; <see cref="Content"/> boxes are in the content panel's.
/// </para>
/// <para>
/// Pure arithmetic in logical pixels at 100 per cent; the window scales it once. Here rather
/// than on the form so every rule - nothing overlaps, everything fits, the buttons are whole -
/// holds on the leg that cannot open a window.
/// </para>
/// </remarks>
public sealed record EditorLayout
{
    public const int Width = 720;
    public const int Height = 576;

    /// <summary>The height one Advanced row takes, description line included.</summary>
    public const int RowPitch = 48;

    /// <summary>The height a multi-line row takes.</summary>
    public const int LinesPitch = 88;

    private const int Margin = 16;
    private const int Right = Width - Margin;
    private const int CaptionWidth = 120;
    private const int Editor = Margin + CaptionWidth + 4;
    private const int EditorHeight = 24;
    private const int CaptionHeight = 18;
    private const int ButtonWidth = 84;
    private const int ButtonHeight = 26;
    private const int ScrollbarAllowance = 20;

    // ---- header, in both views -------------------------------------------------------------

    public required Box NameCaption { get; init; }
    public required Box Name { get; init; }
    public required Box Enabled { get; init; }
    public required Box FilesCaption { get; init; }
    public required Box Files { get; init; }
    public required Box Browse { get; init; }
    public required Box FilesHint { get; init; }
    public required Box Preview { get; init; }

    // ---- the Basics body -------------------------------------------------------------------

    public required Box HowCaption { get; init; }

    /// <summary>The four answers to "how is the log taken away", top to bottom.</summary>
    public required IReadOnlyList<Box> How { get; init; }

    /// <summary>One line under the radios that follows the chosen answer.</summary>
    public required Box HowHint { get; init; }
    public required Box WhenCaption { get; init; }
    public required Box Schedule { get; init; }
    public required Box EarlyLead { get; init; }
    public required Box MaxSize { get; init; }
    public required Box WhenHint { get; init; }
    public required Box KeepCaption { get; init; }
    public required Box Rotate { get; init; }
    public required Box KeepLead { get; init; }
    public required Box MaxAge { get; init; }
    public required Box KeepUnit { get; init; }
    public required Box KeepHint { get; init; }
    public required Box CopiesCaption { get; init; }
    public required Box Names { get; init; }
    public required Box Compression { get; init; }
    public required Box FolderCaption { get; init; }
    public required Box OldDir { get; init; }
    public required Box BrowseFolder { get; init; }
    public required Box CreateOldDir { get; init; }
    public required Box Summary { get; init; }

    // ---- the Advanced body -----------------------------------------------------------------

    /// <summary>Where the scrolling panel sits, in client coordinates.</summary>
    public required Box Viewport { get; init; }

    public required IReadOnlyList<AdvancedSection> Sections { get; init; }

    /// <summary>The heading over the foreign keys, or null when the file has none.</summary>
    public Box? ForeignHeader { get; init; }

    public required IReadOnlyList<ForeignRow> Foreign { get; init; }

    /// <summary>The virtual area the rows are laid out in.</summary>
    public required int ContentWidth { get; init; }

    public required int ContentHeight { get; init; }

    // ---- under both ------------------------------------------------------------------------

    public required Box Status { get; init; }
    public required Box ButtonsHint { get; init; }
    public required Box Toggle { get; init; }

    /// <summary>The preview of what a rotation of the saved job would do.</summary>
    public required Box WhatWouldHappen { get; init; }

    public required Box Check { get; init; }
    public required Box Save { get; init; }
    public required Box Cancel { get; init; }

    public int ClientWidth => Width;
    public int ClientHeight => Height;

    /// <summary>Every box in client coordinates, by name: the header, the Basics body, the viewport and the chrome.</summary>
    public IEnumerable<(string Name, Box Box)> Fixed
    {
        get
        {
            yield return ("name caption", NameCaption);
            yield return ("name", Name);
            yield return ("enabled", Enabled);
            yield return ("files caption", FilesCaption);
            yield return ("files", Files);
            yield return ("browse", Browse);
            yield return ("files hint", FilesHint);
            yield return ("preview", Preview);

            foreach (var basic in Basics)
            {
                yield return basic;
            }

            yield return ("viewport", Viewport);
            yield return ("status", Status);
            yield return ("buttons hint", ButtonsHint);
            yield return ("toggle", Toggle);
            yield return ("what would happen", WhatWouldHappen);
            yield return ("check", Check);
            yield return ("save", Save);
            yield return ("cancel", Cancel);
        }
    }

    /// <summary>The Basics body's boxes alone, which share the client area with the viewport by turns.</summary>
    public IEnumerable<(string Name, Box Box)> Basics
    {
        get
        {
            yield return ("how caption", HowCaption);
            for (var i = 0; i < How.Count; i++)
            {
                yield return ($"how {i}", How[i]);
            }

            yield return ("how hint", HowHint);
            yield return ("when caption", WhenCaption);
            yield return ("schedule", Schedule);
            yield return ("early lead", EarlyLead);
            yield return ("maxsize", MaxSize);
            yield return ("when hint", WhenHint);
            yield return ("keep caption", KeepCaption);
            yield return ("rotate", Rotate);
            yield return ("keep lead", KeepLead);
            yield return ("maxage", MaxAge);
            yield return ("keep unit", KeepUnit);
            yield return ("keep hint", KeepHint);
            yield return ("copies caption", CopiesCaption);
            yield return ("names", Names);
            yield return ("compression", Compression);
            yield return ("folder caption", FolderCaption);
            yield return ("olddir", OldDir);
            yield return ("browse folder", BrowseFolder);
            yield return ("create olddir", CreateOldDir);
            yield return ("summary", Summary);
        }
    }

    /// <summary>Every box in the content panel's coordinates, by name.</summary>
    public IEnumerable<(string Name, Box Box)> Content
    {
        get
        {
            foreach (var section in Sections)
            {
                yield return ($"{section.Title} header", section.Header);

                foreach (var row in section.Rows)
                {
                    yield return ($"{row.Key} caption", row.Caption);
                    yield return ($"{row.Key} key", row.KeyLabel);
                    yield return ($"{row.Key} editor", row.Editor);
                    if (row.Unit is { } unit)
                    {
                        yield return ($"{row.Key} unit", unit);
                    }

                    yield return ($"{row.Key} source", row.Source);
                    yield return ($"{row.Key} inherit", row.Inherit);
                    yield return ($"{row.Key} description", row.Description);
                }
            }

            if (ForeignHeader is { } header)
            {
                yield return ("foreign header", header);
            }

            foreach (var row in Foreign)
            {
                yield return ($"{row.Key} text", row.Text);
                yield return ($"{row.Key} remove", row.Remove);
            }
        }
    }

    /// <summary>Lays the window out for these sections and these foreign keys.</summary>
    public static EditorLayout Compute(IReadOnlyList<JobSection> sections, IReadOnlyList<string> foreignKeys)
    {
        const int HintWidth = Right - Editor;

        // Header. The files box is the widest thing on the form and the Browse button sits at
        // its right; the hint and the preview each take a line under it.
        var name = new Box(Editor, 12, 240, EditorHeight);
        var files = new Box(Editor, 44, 456, 44);
        var browse = new Box(files.Right + 8, files.Y, Right - files.Right - 8, ButtonHeight);
        var filesHint = new Box(Editor, files.Bottom + 4, HintWidth, CaptionHeight);
        var preview = new Box(Editor, filesHint.Bottom, HintWidth, CaptionHeight);
        var bodyTop = preview.Bottom + 8;

        // Chrome, from the bottom up, so the body is whatever is left.
        var cancel = new Box(Right - ButtonWidth, Height - Margin - ButtonHeight, ButtonWidth, ButtonHeight);
        var save = cancel with { X = cancel.X - 8 - ButtonWidth };
        var check = save with { X = save.X - 8 - ButtonWidth };
        var toggle = new Box(Margin, cancel.Y + 4, 220, CaptionHeight);
        var whatWouldHappen = new Box(check.X - 8 - 150, cancel.Y, 150, ButtonHeight);
        var buttonsHint = new Box(Margin, cancel.Y - 8 - 32, Right - Margin, 32);
        var status = new Box(Margin, buttonsHint.Y - 8 - 34, Right - Margin, 34);
        var bodyBottom = status.Y - 8;

        // Basics body: four answers to "how", and one hint that follows the chosen one.
        var how = Enumerable.Range(0, 4).Select(i => new Box(Editor, bodyTop + (i * 20), HintWidth, 20)).ToArray();
        var howHint = new Box(Editor + 18, how[^1].Bottom, HintWidth - 18, CaptionHeight);

        var schedule = new Box(Editor, howHint.Bottom + 8, 150, EditorHeight);
        var earlyLead = new Box(schedule.Right + 8, schedule.Y + 4, 170, CaptionHeight);
        var maxSize = new Box(earlyLead.Right + 4, schedule.Y, 80, EditorHeight);
        var whenHint = new Box(Editor, schedule.Bottom + 4, HintWidth, CaptionHeight);

        var rotate = new Box(Editor, whenHint.Bottom + 6, 56, EditorHeight);
        var keepLead = new Box(rotate.Right + 8, rotate.Y + 4, 230, CaptionHeight);
        var maxAge = new Box(keepLead.Right + 4, rotate.Y, 56, EditorHeight);
        var keepUnit = new Box(maxAge.Right + 8, rotate.Y + 4, 60, CaptionHeight);
        var keepHint = new Box(Editor, rotate.Bottom + 4, HintWidth, CaptionHeight);

        var names = new Box(Editor, keepHint.Bottom + 6, 260, EditorHeight);
        var compression = new Box(names.Right + 8, names.Y, 200, EditorHeight);

        var oldDir = new Box(Editor, names.Bottom + 8, 300, EditorHeight);
        var browseFolder = new Box(oldDir.Right + 8, oldDir.Y - 1, 96, ButtonHeight);
        var createOldDir = new Box(browseFolder.Right + 8, oldDir.Y, Right - browseFolder.Right - 8, 22);

        var summary = new Box(Editor, oldDir.Bottom + 8, HintWidth, bodyBottom - (oldDir.Bottom + 8));

        // Advanced body: the viewport takes the whole body, and the rows are laid out inside it.
        var viewport = new Box(Margin, bodyTop, Right - Margin, bodyBottom - bodyTop);
        var contentWidth = viewport.Width - ScrollbarAllowance;
        var (laidOut, foreignHeader, foreign, contentHeight) = LayOutContent(sections, foreignKeys, contentWidth);

        return new EditorLayout
        {
            NameCaption = new Box(Margin, name.Y + 4, CaptionWidth, CaptionHeight),
            Name = name,
            Enabled = new Box(name.Right + 16, name.Y + 1, 120, 22),
            FilesCaption = new Box(Margin, files.Y + 4, CaptionWidth, CaptionHeight),
            Files = files,
            Browse = browse,
            FilesHint = filesHint,
            Preview = preview,

            HowCaption = new Box(Margin, how[0].Y + 2, CaptionWidth, CaptionHeight),
            How = how,
            HowHint = howHint,
            WhenCaption = new Box(Margin, schedule.Y + 4, CaptionWidth, CaptionHeight),
            Schedule = schedule,
            EarlyLead = earlyLead,
            MaxSize = maxSize,
            WhenHint = whenHint,
            KeepCaption = new Box(Margin, rotate.Y + 4, CaptionWidth, CaptionHeight),
            Rotate = rotate,
            KeepLead = keepLead,
            MaxAge = maxAge,
            KeepUnit = keepUnit,
            KeepHint = keepHint,
            CopiesCaption = new Box(Margin, names.Y + 4, CaptionWidth, CaptionHeight),
            Names = names,
            Compression = compression,
            FolderCaption = new Box(Margin, oldDir.Y + 4, CaptionWidth, CaptionHeight),
            OldDir = oldDir,
            BrowseFolder = browseFolder,
            CreateOldDir = createOldDir,
            Summary = summary,

            Viewport = viewport,
            Sections = laidOut,
            ForeignHeader = foreignHeader,
            Foreign = foreign,
            ContentWidth = contentWidth,
            ContentHeight = contentHeight,

            Status = status,
            ButtonsHint = buttonsHint,
            Toggle = toggle,
            WhatWouldHappen = whatWouldHappen,
            Check = check,
            Save = save,
            Cancel = cancel,
        };
    }

    private static (IReadOnlyList<AdvancedSection>, Box?, IReadOnlyList<ForeignRow>, int) LayOutContent(
        IReadOnlyList<JobSection> sections, IReadOnlyList<string> foreignKeys, int contentWidth)
    {
        const int Left = 8;
        const int Caption = 180;
        const int EditorX = Left + Caption + 8;
        const int EditorWidth = 200;
        const int UnitX = EditorX + EditorWidth + 8;
        const int SourceX = UnitX + 48;
        const int InheritWidth = 64;
        var right = contentWidth - Left;
        var inheritX = right - InheritWidth;

        var y = 8;
        var laidOut = new List<AdvancedSection>();

        foreach (var section in sections)
        {
            var header = new Box(Left, y, right - Left, 20);
            y = header.Bottom + 6;

            var rows = new List<AdvancedRow>();

            foreach (var field in section.Fields)
            {
                var shown = JobEditorModel.Presentation(field);
                var lines = shown.Editor == FieldEditor.Lines;
                var editor = new Box(EditorX, y, EditorWidth, lines ? 64 : EditorHeight);

                rows.Add(new AdvancedRow(
                    field.Key,
                    Caption: new Box(Left, y + 4, Caption, CaptionHeight),
                    KeyLabel: new Box(Left, y + 26, Caption, 14),
                    Editor: editor,
                    Unit: shown.Unit is null ? null : new Box(UnitX, y + 4, 40, CaptionHeight),
                    Source: new Box(SourceX, y + 4, inheritX - 8 - SourceX, CaptionHeight),
                    Inherit: new Box(inheritX, y + 4, InheritWidth, CaptionHeight),
                    Description: new Box(EditorX, editor.Bottom + 2, right - EditorX, 16)));

                y += lines ? LinesPitch : RowPitch;
            }

            laidOut.Add(new AdvancedSection(section.Title, header, rows));
            y += 8;
        }

        Box? foreignHeader = null;
        var foreign = new List<ForeignRow>();

        if (foreignKeys.Count > 0)
        {
            foreignHeader = new Box(Left, y, right - Left, 20);
            y = foreignHeader.Value.Bottom + 6;

            foreach (var key in foreignKeys)
            {
                foreign.Add(new ForeignRow(
                    key,
                    Text: new Box(Left, y, inheritX - 8 - Left, 20),
                    Remove: new Box(inheritX, y, InheritWidth, 20)));
                y += 26;
            }
        }

        return (laidOut, foreignHeader, foreign, y + 8);
    }
}
