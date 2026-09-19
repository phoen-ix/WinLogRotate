namespace WinLogRotate.Gui.Cli;

/// <summary>One typed row under a page's divider, in client coordinates.</summary>
public sealed record AdvancedRow(
    string Key, Box Caption, Box KeyLabel, Box Editor, Box? Unit, Box Source, Box Inherit, Box Description);

/// <summary>A key the file writes that this build does not read: shown, and removable.</summary>
public sealed record ForeignRow(string Key, Box Text, Box Remove);

/// <summary>
/// One page of the editor: what it draws of its own, and the rows under its divider.
/// </summary>
/// <remarks>
/// Every page is laid out in client coordinates and shares the client area with the others by
/// turns, so the rules - nothing overlaps, everything fits - hold per page against the chrome.
/// </remarks>
public abstract record PageLayout(JobPageId Id, Box? MoreDivider, IReadOnlyList<AdvancedRow> Rows)
{
    /// <summary>Every box the page draws, by name.</summary>
    public IEnumerable<(string Name, Box Box)> Boxes
    {
        get
        {
            foreach (var own in Own)
            {
                yield return own;
            }

            if (MoreDivider is { } divider)
            {
                yield return ("more divider", divider);
            }

            foreach (var row in Rows)
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
    }

    protected abstract IEnumerable<(string Name, Box Box)> Own { get; }
}

public sealed record FilesPageLayout(
    Box NameCaption, Box Name, Box Enabled, Box FilesCaption, Box Files, Box Browse, Box FilesHint, Box Preview,
    Box? MoreDivider, IReadOnlyList<AdvancedRow> Rows) : PageLayout(JobPageId.Files, MoreDivider, Rows)
{
    protected override IEnumerable<(string Name, Box Box)> Own =>
    [
        ("name caption", NameCaption), ("name", Name), ("enabled", Enabled), ("files caption", FilesCaption),
        ("files", Files), ("browse", Browse), ("files hint", FilesHint), ("preview", Preview),
    ];
}

public sealed record HowPageLayout(
    IReadOnlyList<Box> How, Box HowHint,
    Box? MoreDivider, IReadOnlyList<AdvancedRow> Rows) : PageLayout(JobPageId.How, MoreDivider, Rows)
{
    protected override IEnumerable<(string Name, Box Box)> Own =>
        How.Select((box, i) => ($"how {i}", box)).Append(("how hint", HowHint));
}

public sealed record WhenPageLayout(
    Box ScheduleCaption, Box Schedule, Box EarlyLead, Box MaxSize, Box WhenHint,
    Box? MoreDivider, IReadOnlyList<AdvancedRow> Rows) : PageLayout(JobPageId.When, MoreDivider, Rows)
{
    protected override IEnumerable<(string Name, Box Box)> Own =>
    [
        ("schedule caption", ScheduleCaption), ("schedule", Schedule), ("early lead", EarlyLead),
        ("maxsize", MaxSize), ("when hint", WhenHint),
    ];
}

public sealed record KeepPageLayout(
    Box KeepCaption, Box Rotate, Box KeepLead, Box MaxAge, Box KeepUnit,
    Box? MoreDivider, IReadOnlyList<AdvancedRow> Rows) : PageLayout(JobPageId.Keep, MoreDivider, Rows)
{
    protected override IEnumerable<(string Name, Box Box)> Own =>
    [
        ("keep caption", KeepCaption), ("rotate", Rotate), ("keep lead", KeepLead), ("maxage", MaxAge), ("keep unit", KeepUnit),
    ];
}

public sealed record CopiesPageLayout(
    Box NamesCaption, Box Names, Box Compression, Box FolderCaption, Box OldDir, Box BrowseFolder, Box CreateOldDir,
    Box? MoreDivider, IReadOnlyList<AdvancedRow> Rows) : PageLayout(JobPageId.Copies, MoreDivider, Rows)
{
    protected override IEnumerable<(string Name, Box Box)> Own =>
    [
        ("names caption", NamesCaption), ("names", Names), ("compression", Compression), ("folder caption", FolderCaption),
        ("olddir", OldDir), ("browse folder", BrowseFolder), ("create olddir", CreateOldDir),
    ];
}

/// <summary>A page that is nothing but rows: Before &amp; after, If things go wrong.</summary>
public sealed record RowsPageLayout(JobPageId Id, IReadOnlyList<AdvancedRow> Rows) : PageLayout(Id, null, Rows)
{
    protected override IEnumerable<(string Name, Box Box)> Own => [];
}

/// <summary>The keys this build does not read, each with its Remove link.</summary>
public sealed record OtherPageLayout(IReadOnlyList<ForeignRow> Foreign) : PageLayout(JobPageId.Other, null, [])
{
    protected override IEnumerable<(string Name, Box Box)> Own =>
        Foreign.SelectMany(row => new[] { ($"{row.Key} text", row.Text), ($"{row.Key} remove", row.Remove) });
}

/// <summary>
/// Where everything in the job editor goes.
/// </summary>
/// <remarks>
/// <para>
/// One window, one size: a menu of the pages down the left, the page's title and the page
/// itself on the right, and under both the summary sentence, the status line and the buttons.
/// Choosing a page is a visibility flip, never a resize, so nothing is placed after the window
/// has scaled - and nothing scrolls, so a mouse wheel can never change a drop-down by accident.
/// </para>
/// <para>
/// Pure arithmetic in logical pixels at 100 per cent; the window scales it once. Here rather
/// than on the form so every rule - nothing overlaps, everything fits, the buttons are whole -
/// holds on the leg that cannot open a window.
/// </para>
/// </remarks>
public sealed record EditorLayout
{
    public const int Width = 800;
    public const int Height = 576;

    /// <summary>The height one typed row takes, description line included.</summary>
    public const int RowPitch = 48;

    /// <summary>The height a multi-line row takes.</summary>
    public const int LinesPitch = 88;

    private const int Margin = 16;
    private const int Right = Width - Margin;
    private const int MenuWidth = 150;
    private const int BodyLeft = Margin + MenuWidth + 12;
    private const int BodyWidth = Right - BodyLeft;
    private const int CaptionWidth = 100;
    private const int Field = BodyLeft + CaptionWidth + 4;
    private const int EditorHeight = 24;
    private const int CaptionHeight = 18;
    private const int ButtonWidth = 84;
    private const int ButtonHeight = 26;
    private const int Gap = 10;

    // Rows under a divider: caption and key on the left, editor, unit, where the value comes
    // from, the Inherit link at the right edge, and the description under the editor.
    private const int RowCaption = 170;
    private const int RowEditorX = BodyLeft + RowCaption + 8;
    private const int RowEditorWidth = 180;
    private const int RowUnitX = RowEditorX + RowEditorWidth + 8;
    private const int RowSourceX = RowUnitX + 48;
    private const int InheritWidth = 64;
    private const int RowInheritX = Right - InheritWidth;

    public required Box Menu { get; init; }
    public required Box PageTitle { get; init; }
    public required IReadOnlyList<PageLayout> Pages { get; init; }

    /// <summary>The sentence that restates the whole job, under every page.</summary>
    public required Box Summary { get; init; }
    public required Box Status { get; init; }

    /// <summary>The preview of what a rotation of the saved job would do.</summary>
    public required Box WhatWouldHappen { get; init; }
    public required Box Check { get; init; }
    public required Box Save { get; init; }
    public required Box Cancel { get; init; }

    public int ClientWidth => Width;
    public int ClientHeight => Height;

    public FilesPageLayout Files => Pages.OfType<FilesPageLayout>().Single();
    public HowPageLayout How => Pages.OfType<HowPageLayout>().Single();
    public WhenPageLayout When => Pages.OfType<WhenPageLayout>().Single();
    public KeepPageLayout Keep => Pages.OfType<KeepPageLayout>().Single();
    public CopiesPageLayout Copies => Pages.OfType<CopiesPageLayout>().Single();
    public OtherPageLayout? Other => Pages.OfType<OtherPageLayout>().SingleOrDefault();

    /// <summary>The chrome every page shares, by name.</summary>
    public IEnumerable<(string Name, Box Box)> Fixed
    {
        get
        {
            yield return ("menu", Menu);
            yield return ("page title", PageTitle);
            yield return ("summary", Summary);
            yield return ("status", Status);
            yield return ("what would happen", WhatWouldHappen);
            yield return ("check", Check);
            yield return ("save", Save);
            yield return ("cancel", Cancel);
        }
    }

    /// <summary>Lays the window out for these pages and these foreign keys.</summary>
    public static EditorLayout Compute(IReadOnlyList<JobPage> pages, IReadOnlyList<string> foreignKeys)
    {
        var pageTitle = new Box(BodyLeft, Margin, BodyWidth, 22);
        var top = pageTitle.Bottom + Gap;

        // Chrome, from the bottom up, so the body is whatever is left.
        var cancel = new Box(Right - ButtonWidth, Height - Margin - ButtonHeight, ButtonWidth, ButtonHeight);
        var save = cancel with { X = cancel.X - 8 - ButtonWidth };
        var check = save with { X = save.X - 8 - ButtonWidth };
        var whatWouldHappen = new Box(check.X - 8 - 150, cancel.Y, 150, ButtonHeight);
        var status = new Box(Margin, cancel.Y - 8 - 34, Right - Margin, 34);
        var summary = new Box(Margin, status.Y - 6 - 36, Right - Margin, 36);
        var bottom = summary.Y - Gap;
        var menu = new Box(Margin, Margin, MenuWidth, bottom - Margin);

        var laidOut = new List<PageLayout>();

        foreach (var page in pages)
        {
            laidOut.Add(page.Id switch
            {
                JobPageId.Files => FilesPage(page, top),
                JobPageId.How => HowPage(page, top),
                JobPageId.When => WhenPage(page, top),
                JobPageId.Keep => KeepPage(page, top),
                JobPageId.Copies => CopiesPage(page, top),
                JobPageId.Other => OtherPage(foreignKeys, top),
                _ => new RowsPageLayout(page.Id, Rows(page.More, top).Rows),
            });
        }

        return new EditorLayout
        {
            Menu = menu,
            PageTitle = pageTitle,
            Pages = laidOut,
            Summary = summary,
            Status = status,
            WhatWouldHappen = whatWouldHappen,
            Check = check,
            Save = save,
            Cancel = cancel,
        };
    }

    private static FilesPageLayout FilesPage(JobPage page, int top)
    {
        var name = new Box(Field, top, 240, EditorHeight);
        var enabled = new Box(name.Right + 16, top + 1, 120, 22);

        // The files box is the one thing on the form that deserves height: one line per file,
        // and a job that looks after a folder of services has a dozen.
        var filesY = name.Bottom + Gap;
        var browse = new Box(Right - ButtonWidth, filesY, ButtonWidth, ButtonHeight);
        var files = new Box(Field, filesY, browse.X - 8 - Field, 150);
        var filesHint = new Box(Field, files.Bottom + 4, Right - Field, CaptionHeight);
        var preview = new Box(Field, filesHint.Bottom, Right - Field, CaptionHeight);

        var (divider, rows) = More(page, preview.Bottom + Gap);

        return new FilesPageLayout(
            NameCaption: new Box(BodyLeft, top + 4, CaptionWidth, CaptionHeight),
            Name: name,
            Enabled: enabled,
            FilesCaption: new Box(BodyLeft, filesY + 4, CaptionWidth, CaptionHeight),
            Files: files,
            Browse: browse,
            FilesHint: filesHint,
            Preview: preview,
            MoreDivider: divider,
            Rows: rows);
    }

    private static HowPageLayout HowPage(JobPage page, int top)
    {
        // Five answers at a comfortable pitch, and a note that has two whole lines to itself,
        // because the longest of them was cut off mid-sentence on one.
        var how = Enumerable.Range(0, 5).Select(i => new Box(BodyLeft, top + (i * 24), BodyWidth, 22)).ToArray();
        var hint = new Box(BodyLeft + 18, how[^1].Bottom + 4, BodyWidth - 18, 36);

        var (divider, rows) = More(page, hint.Bottom + Gap);

        return new HowPageLayout(how, hint, divider, rows);
    }

    private static WhenPageLayout WhenPage(JobPage page, int top)
    {
        var schedule = new Box(Field, top, 150, EditorHeight);
        var earlyLead = new Box(schedule.Right + 8, top + 4, 170, CaptionHeight);
        var maxSize = new Box(earlyLead.Right + 4, top, 80, EditorHeight);
        var whenHint = new Box(Field, schedule.Bottom + 4, Right - Field, CaptionHeight);

        var (divider, rows) = More(page, whenHint.Bottom + Gap);

        return new WhenPageLayout(
            ScheduleCaption: new Box(BodyLeft, top + 4, CaptionWidth, CaptionHeight),
            Schedule: schedule,
            EarlyLead: earlyLead,
            MaxSize: maxSize,
            WhenHint: whenHint,
            MoreDivider: divider,
            Rows: rows);
    }

    private static KeepPageLayout KeepPage(JobPage page, int top)
    {
        var rotate = new Box(Field, top, 56, EditorHeight);
        var keepLead = new Box(rotate.Right + 8, top + 4, 204, CaptionHeight);
        var maxAge = new Box(keepLead.Right + 4, top, 56, EditorHeight);
        var keepUnit = new Box(maxAge.Right + 8, top + 4, 60, CaptionHeight);

        var (divider, rows) = More(page, rotate.Bottom + Gap);

        return new KeepPageLayout(
            KeepCaption: new Box(BodyLeft, top + 4, CaptionWidth, CaptionHeight),
            Rotate: rotate,
            KeepLead: keepLead,
            MaxAge: maxAge,
            KeepUnit: keepUnit,
            MoreDivider: divider,
            Rows: rows);
    }

    private static CopiesPageLayout CopiesPage(JobPage page, int top)
    {
        var names = new Box(Field, top, 240, EditorHeight);
        var compression = new Box(names.Right + 8, top, 200, EditorHeight);

        var folderY = names.Bottom + Gap;
        var oldDir = new Box(Field, folderY, 250, EditorHeight);
        var browseFolder = new Box(oldDir.Right + 8, folderY - 1, 96, ButtonHeight);
        var createOldDir = new Box(browseFolder.Right + 8, folderY, Right - browseFolder.Right - 8, 22);

        var (divider, rows) = More(page, oldDir.Bottom + Gap);

        return new CopiesPageLayout(
            NamesCaption: new Box(BodyLeft, top + 4, CaptionWidth, CaptionHeight),
            Names: names,
            Compression: compression,
            FolderCaption: new Box(BodyLeft, folderY + 4, CaptionWidth, CaptionHeight),
            OldDir: oldDir,
            BrowseFolder: browseFolder,
            CreateOldDir: createOldDir,
            MoreDivider: divider,
            Rows: rows);
    }

    private static OtherPageLayout OtherPage(IReadOnlyList<string> foreignKeys, int top)
    {
        var y = top;
        var foreign = new List<ForeignRow>();

        foreach (var key in foreignKeys)
        {
            foreign.Add(new ForeignRow(
                key,
                Text: new Box(BodyLeft, y, RowInheritX - 8 - BodyLeft, 20),
                Remove: new Box(RowInheritX, y, InheritWidth, 20)));
            y += 26;
        }

        return new OtherPageLayout(foreign);
    }

    /// <summary>The divider and the rows under a page's own controls; no divider when there are no rows.</summary>
    private static (Box? Divider, IReadOnlyList<AdvancedRow> Rows) More(JobPage page, int y)
    {
        if (page.More.Count == 0)
        {
            return (null, []);
        }

        var divider = new Box(BodyLeft, y, BodyWidth, 20);
        return (divider, Rows(page.More, divider.Bottom + 8).Rows);
    }

    private static (IReadOnlyList<AdvancedRow> Rows, int Bottom) Rows(IReadOnlyList<JobField> fields, int y)
    {
        var rows = new List<AdvancedRow>();

        foreach (var field in fields)
        {
            var shown = JobEditorModel.Presentation(field);
            var lines = shown.Editor == FieldEditor.Lines;
            var editor = new Box(RowEditorX, y, RowEditorWidth, lines ? 64 : EditorHeight);

            rows.Add(new AdvancedRow(
                field.Key,
                Caption: new Box(BodyLeft, y + 4, RowCaption, CaptionHeight),
                KeyLabel: new Box(BodyLeft, y + 26, RowCaption, 14),
                Editor: editor,
                Unit: shown.Unit is null ? null : new Box(RowUnitX, y + 4, 40, CaptionHeight),
                Source: new Box(RowSourceX, y + 4, RowInheritX - 8 - RowSourceX, CaptionHeight),
                Inherit: new Box(RowInheritX, y + 4, InheritWidth, CaptionHeight),
                Description: new Box(RowEditorX, editor.Bottom + 2, Right - RowEditorX, 16)));

            y += lines ? LinesPitch : RowPitch;
        }

        return (rows, y);
    }
}
