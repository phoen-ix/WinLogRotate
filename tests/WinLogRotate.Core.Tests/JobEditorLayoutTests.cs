using Shouldly;
using WinLogRotate.Gui.Cli;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// Where the job editor puts things, asserted where the editor cannot be opened.
/// </summary>
/// <remarks>
/// <para>
/// The form gave its advanced grid the height that was left after eight common rows:
/// <c>470 - y - 130</c>, which was -52. WinForms stores a negative height without complaint, so
/// the grid's bottom edge sat above its top, the status line was drawn behind the last two rows
/// of fields, and twenty-five keys - including the warning that a hook will never run here - were
/// unreachable from the window whose comment said nothing was unreachable either way.
/// </para>
/// <para>
/// None of that could be asserted while the arithmetic lived beside the controls: a test project
/// that references the GUI carries a Microsoft.WindowsDesktop.App framework reference and cannot
/// run on the Linux leg. The arithmetic is a record now, and these are the four things any layout
/// of it has to satisfy for every number of common fields it might be given.
/// </para>
/// </remarks>
public sealed class JobEditorLayoutTests
{
    /// <summary>The counts worth trying: none, one column, the real count, and more than now.</summary>
    public static TheoryData<int> Counts => [0, 1, 2, 7, JobEditorModel.Common.Count, 12];

    /// <summary>Every box has a size that can be seen.</summary>
    /// <remarks>
    /// The assertion the defect fails directly. A height of -52 is a box the framework accepts
    /// and a person never sees.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Counts))]
    public void EveryBoxHasAPositiveSize(int commonFields)
    {
        foreach (var (name, box) in JobEditorLayout.Compute(commonFields).All)
        {
            box.Width.ShouldBeGreaterThan(0, $"{name} has no width");
            box.Height.ShouldBeGreaterThan(0, $"{name} has no height");
        }
    }

    /// <summary>No two boxes share any area.</summary>
    /// <remarks>
    /// The status line behind the field rows, and the "one glob per line" hint drawn over the
    /// paths box, were both this. Pairwise, because a rule about columns or rows would only catch
    /// the overlaps its author had already noticed.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Counts))]
    public void NoTwoBoxesOverlap(int commonFields)
    {
        var boxes = JobEditorLayout.Compute(commonFields).All.ToArray();

        var overlapping = boxes
            .SelectMany((a, i) => boxes.Skip(i + 1)
                .Where(b => a.Box.Overlaps(b.Box))
                .Select(b => $"{a.Name} overlaps {b.Name}"))
            .ToArray();

        overlapping.ShouldBeEmpty();
    }

    /// <summary>Everything is inside the window, the buttons included.</summary>
    /// <remarks>
    /// Named separately for the buttons because a window whose Save is off the bottom edge is a
    /// window that cannot save, which is a different order of failure from a caption clipped.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Counts))]
    public void EverythingIsInsideTheClientArea(int commonFields)
    {
        var layout = JobEditorLayout.Compute(commonFields);

        layout.All
            .Where(b => !b.Box.Within(layout.ClientWidth, layout.ClientHeight))
            .Select(b => b.Name)
            .ShouldBeEmpty();

        foreach (var button in new[] { layout.Check, layout.Save, layout.Cancel })
        {
            button.Bottom.ShouldBeLessThan(layout.ClientHeight, "a button off the bottom edge cannot be pressed");
            button.Right.ShouldBeLessThanOrEqualTo(layout.ClientWidth);
        }
    }

    /// <summary>
    /// The grid can be seen, the status line sits under it, and the buttons under that.
    /// </summary>
    /// <remarks>
    /// Bottom-up is the whole fix. A grid one row tall would satisfy every rule above and still
    /// hide twenty-four keys behind a scrollbar; a status line that fits but sits above the grid
    /// would put the verdict where nobody reads it.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Counts))]
    public void TheGridIsTallEnoughToReadAndTheVerdictIsUnderIt(int commonFields)
    {
        var layout = JobEditorLayout.Compute(commonFields);

        layout.Advanced.Height.ShouldBeGreaterThanOrEqualTo(120, "six rows and a header, at least");
        layout.Status.Y.ShouldBeGreaterThanOrEqualTo(layout.Advanced.Bottom);
        layout.Check.Y.ShouldBeGreaterThanOrEqualTo(layout.Status.Bottom);

        foreach (var slot in layout.Common)
        {
            slot.Editor.Bottom.ShouldBeLessThanOrEqualTo(layout.Advanced.Y, "a field under the grid");
        }
    }

    /// <summary>The layout has a slot for every common key the model lists, and no more.</summary>
    /// <remarks>
    /// The form asks for <c>JobEditorModel.Common.Count</c> slots and then places the keys by
    /// index, so the two counts have to agree or a key is placed off the end of the list.
    /// </remarks>
    [Fact]
    public void ThereIsASlotForEveryCommonKey()
    {
        JobEditorLayout.Compute(JobEditorModel.Common.Count).Common.Count
            .ShouldBe(JobEditorModel.Common.Count);

        JobEditorLayout.Compute(0).Common.ShouldBeEmpty();
    }

    /// <summary>
    /// The window fits a small screen at a common scale.
    /// </summary>
    /// <remarks>
    /// The reason the common fields are in two columns rather than one: eight rows in a single
    /// column made the client 672 pixels tall, which at 125 per cent is 840 - taller than a
    /// 1080-pixel screen leaves once the taskbar and title bar have taken theirs, and taller than
    /// a 768-pixel laptop screen at any scale.
    /// </remarks>
    [Fact]
    public void TheWindowFitsASmallScreenAtACommonScale()
    {
        var layout = JobEditorLayout.Compute(JobEditorModel.Common.Count);

        (layout.ClientHeight * 1.25).ShouldBeLessThanOrEqualTo(720, "at 125 per cent, with a title bar, on 768 rows");
        (layout.ClientWidth * 1.25).ShouldBeLessThanOrEqualTo(1024);
    }

    /// <summary>A box knows its own edges, and touching is not overlapping.</summary>
    [Fact]
    public void ABoxKnowsItsEdges()
    {
        var box = new Box(10, 20, 30, 40);

        box.Right.ShouldBe(40);
        box.Bottom.ShouldBe(60);

        box.Overlaps(new Box(40, 20, 5, 5)).ShouldBeFalse("shares an edge, not an area");
        box.Overlaps(new Box(39, 59, 5, 5)).ShouldBeTrue();
        box.Within(40, 60).ShouldBeTrue();
        box.Within(39, 60).ShouldBeFalse();
    }
}
