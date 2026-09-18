using Shouldly;
using WinLogRotate.Gui.Cli;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// The main window's arithmetic, added up on the leg that cannot open it.
/// </summary>
/// <remarks>
/// The Jobs page's toolbar was 752 pixels of buttons in a content panel that is 662 wide when
/// the window is at its minimum, so the last two buttons wrapped onto a second row that the
/// toolbar's fixed height hid - Check configuration and Open folder were simply not there on a
/// small screen. The widths were typed beside the buttons, the minimum beside the window, and
/// nothing could add them up.
/// </remarks>
public sealed class WindowLayoutTests
{
    /// <summary>The toolbar fits the narrowest window on one row.</summary>
    [Fact]
    public void TheJobsToolbarFitsTheNarrowestWindowOnOneRow() =>
        JobsToolbar.Width.ShouldBeLessThanOrEqualTo(
            MainWindowLayout.NarrowestContentWidth,
            "a button past the edge of the narrowest window is a button nobody can press");

    /// <summary>
    /// Every button has room for its caption.
    /// </summary>
    /// <remarks>
    /// So that the fit above cannot be bought by squeezing the widths: Segoe UI at nine points
    /// averages about six pixels a character at 100 per cent, and a button pads each side.
    /// </remarks>
    [Fact]
    public void EveryToolbarButtonHasRoomForItsCaption()
    {
        foreach (var button in JobsToolbar.Buttons)
        {
            button.Width.ShouldBeGreaterThanOrEqualTo(
                (button.Text.Length * 6) + 12,
                $"'{button.Text}' would be clipped at {button.Width} pixels");
        }
    }

    /// <summary>The narrowest content width is what is left after the frame, the strip and the padding.</summary>
    [Fact]
    public void TheNarrowestContentWidthIsWhatIsLeftOfTheMinimum() =>
        MainWindowLayout.NarrowestContentWidth.ShouldBe(
            MainWindowLayout.MinimumWidth
            - MainWindowLayout.FrameWidth
            - MainWindowLayout.NavigationWidth
            - (2 * MainWindowLayout.ContentPadding));
}
