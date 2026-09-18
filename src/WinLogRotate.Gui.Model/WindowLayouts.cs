namespace WinLogRotate.Gui.Cli;

/// <summary>
/// The main window's fixed numbers, in logical pixels, where a test can add them up.
/// </summary>
/// <remarks>
/// The window has a minimum size, a navigation strip and a padded content panel, and the Jobs
/// page has a toolbar whose seven buttons were 752 pixels wide - 90 wider than what was left for
/// them at that minimum, so the last two wrapped onto a second row that the toolbar's fixed
/// height hid. Nobody could see it, because the arithmetic lived in two files of a project no
/// test on this leg can load. It lives here now, and <c>WindowLayoutTests</c> adds it up.
/// </remarks>
public static class MainWindowLayout
{
    public const int MinimumWidth = 880;
    public const int MinimumHeight = 560;
    public const int Width = 1000;
    public const int Height = 640;
    public const int NavigationWidth = 170;
    public const int ContentPadding = 16;

    /// <summary>
    /// The frame the minimum size includes: a sizable window's border is 8 pixels a side at 100
    /// per cent, and the minimum is a size of the window, not of its client area.
    /// </summary>
    public const int FrameWidth = 16;

    /// <summary>What a page has across when the window is as narrow as it is allowed to be.</summary>
    public static int NarrowestContentWidth =>
        MinimumWidth - FrameWidth - NavigationWidth - (2 * ContentPadding);
}

/// <summary>One button on a toolbar: its caption and the width it is given.</summary>
public sealed record ToolbarButton(string Text, int Width);

/// <summary>
/// The Jobs page's toolbar, whose buttons have to fit the narrowest window on one row.
/// </summary>
/// <remarks>
/// A flow panel's default margin is three pixels a side, so each button costs six more than its
/// width. The captions are kept whole - "Check configuration" says what the button does in a
/// way "Check" does not - and the widths are what the captions need at 100 per cent and no more.
/// </remarks>
public static class JobsToolbar
{
    /// <summary>A flow panel's default control margin, on each side.</summary>
    public const int Margin = 3;

    public static ToolbarButton New { get; } = new("New", 60);
    public static ToolbarButton Edit { get; } = new("Edit", 60);
    public static ToolbarButton Toggle { get; } = new("Enable/Disable", 108);
    public static ToolbarButton Remove { get; } = new("Remove", 76);
    public static ToolbarButton Refresh { get; } = new("Refresh", 76);
    public static ToolbarButton Check { get; } = new("Check configuration", 132);
    public static ToolbarButton Open { get; } = new("Open folder", 96);

    /// <summary>Every button, in the order the toolbar shows them.</summary>
    public static IReadOnlyList<ToolbarButton> Buttons { get; } =
        [New, Edit, Toggle, Remove, Refresh, Check, Open];

    /// <summary>What the row of them takes, margins included.</summary>
    public static int Width => Buttons.Sum(b => b.Width + (2 * Margin));
}

/// <summary>
/// What the Jobs page shows when there are no jobs: what a job is, and one thing to press.
/// </summary>
/// <remarks>
/// The first thing a new user reads. A grid with no rows and seven toolbar buttons said nothing
/// about what to do; three sentences and a button do.
/// </remarks>
public static class JobsEmptyState
{
    public const string Sentence =
        "No jobs yet. A job says which log files to look after, who starts a fresh file, "
        + "and how many old copies to keep. The scheduled task runs every enabled job each night.";

    public const string Hint = "Have a Linux logrotate configuration? 'winlogrotate import' converts it.";

    public static ToolbarButton Add { get; } = new("Add a job\u2026", 110);
}

