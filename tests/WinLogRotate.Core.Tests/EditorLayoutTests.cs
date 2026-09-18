using Shouldly;
using WinLogRotate.Gui.Cli;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// The job editor's arithmetic, for both views, judged without a window.
/// </summary>
/// <remarks>
/// The rules the grid-height defect taught: every box has a size, no two boxes share an area,
/// everything is inside the area it is drawn in, and the buttons can be pressed. Here they hold
/// for the fixed part of the window, for the Basics body that shares the client area with the
/// scrolled viewport, and for every row inside the viewport's virtual area.
/// </remarks>
public sealed class EditorLayoutTests
{
    private const string Shown = """
        {"schema":1,"ok":true,"exitCode":0,"result":{"job":"iis","path":"C:\\pd\\conf.d\\iis.toml","diagnostics":[],
         "keys":[{"key":"name","source":"\"iis\"","line":4,"known":true},
                 {"key":"rotate","source":"14","line":6,"known":true},
                 {"key":"ownr","source":"\"team-web\"","line":8,"known":false},
                 {"key":"colour","source":"\"blue\"","line":9,"known":false}]}}
        """;

    public static TheoryData<string> Views => ["blank", "loaded", "empty"];

    private static EditorLayout Layout(string which) => which switch
    {
        "blank" => EditorLayout.Compute(JobEditorModel.Sections(JobEditorModel.Blank()), []),
        "loaded" => EditorLayout.Compute(
            JobEditorModel.Sections(JobEditorModel.From(Shown)),
            [.. JobEditorModel.Foreign(JobEditorModel.From(Shown)).Select(f => f.Key)]),
        _ => EditorLayout.Compute([], []),
    };

    [Theory]
    [MemberData(nameof(Views))]
    public void EveryBoxHasAPositiveSize(string which)
    {
        var layout = Layout(which);

        foreach (var (name, box) in layout.Fixed.Concat(layout.Content))
        {
            box.Width.ShouldBeGreaterThan(0, $"{name} has no width");
            box.Height.ShouldBeGreaterThan(0, $"{name} has no height");
        }
    }

    /// <summary>
    /// The Basics body and the viewport take turns in the same area, so each is judged against
    /// the header and the chrome, never against the other.
    /// </summary>
    [Theory]
    [MemberData(nameof(Views))]
    public void NoTwoBoxesThatAreShownTogetherOverlap(string which)
    {
        var layout = Layout(which);
        var basics = layout.Basics.Select(b => b.Name).ToHashSet();

        var withBasics = layout.Fixed.Where(b => b.Name != "viewport").ToArray();
        var withViewport = layout.Fixed.Where(b => !basics.Contains(b.Name)).ToArray();

        Overlaps(withBasics).ShouldBeEmpty();
        Overlaps(withViewport).ShouldBeEmpty();
        Overlaps(layout.Content.ToArray()).ShouldBeEmpty();
    }

    private static string[] Overlaps((string Name, Box Box)[] boxes) =>
        [.. boxes.SelectMany((a, i) => boxes.Skip(i + 1)
            .Where(b => a.Box.Overlaps(b.Box))
            .Select(b => $"{a.Name} overlaps {b.Name}"))];

    [Theory]
    [MemberData(nameof(Views))]
    public void EverythingIsInsideTheAreaItIsDrawnIn(string which)
    {
        var layout = Layout(which);

        layout.Fixed
            .Where(b => !b.Box.Within(layout.ClientWidth, layout.ClientHeight))
            .Select(b => b.Name)
            .ShouldBeEmpty("outside the window");

        layout.Content
            .Where(b => !b.Box.Within(layout.ContentWidth, layout.ContentHeight))
            .Select(b => b.Name)
            .ShouldBeEmpty("outside the scrolled area");

        foreach (var button in new[] { layout.Check, layout.Save, layout.Cancel })
        {
            button.Bottom.ShouldBeLessThan(layout.ClientHeight, "a button off the bottom edge cannot be pressed");
            button.Right.ShouldBeLessThanOrEqualTo(layout.ClientWidth);
        }
    }

    [Theory]
    [MemberData(nameof(Views))]
    public void TheBodiesSitAboveTheStatusLineAndTheButtonsBelowTheHint(string which)
    {
        var layout = Layout(which);

        layout.Summary.Bottom.ShouldBeLessThanOrEqualTo(layout.Status.Y);
        layout.Viewport.Bottom.ShouldBeLessThanOrEqualTo(layout.Status.Y);
        layout.Viewport.Y.ShouldBeGreaterThanOrEqualTo(layout.Preview.Bottom);
        layout.ButtonsHint.Y.ShouldBeGreaterThanOrEqualTo(layout.Status.Bottom);
        layout.Check.Y.ShouldBeGreaterThanOrEqualTo(layout.ButtonsHint.Bottom);
    }

    /// <summary>Six rows at a time, or the Advanced view is a keyhole.</summary>
    [Fact]
    public void TheViewportShowsAtLeastSixRows() =>
        Layout("blank").Viewport.Height.ShouldBeGreaterThanOrEqualTo(6 * EditorLayout.RowPitch);

    [Theory]
    [MemberData(nameof(Views))]
    public void ThereIsARowForEveryFieldTheModelSections(string which)
    {
        var view = which switch
        {
            "blank" => JobEditorModel.Blank(),
            "loaded" => JobEditorModel.From(Shown),
            _ => null,
        };
        var layout = Layout(which);

        if (view is null)
        {
            layout.Sections.ShouldBeEmpty();
            layout.Foreign.ShouldBeEmpty();
            layout.ForeignHeader.ShouldBeNull();
            return;
        }

        var sections = JobEditorModel.Sections(view);
        layout.Sections.Select(s => s.Title).ShouldBe(sections.Select(s => s.Title));

        foreach (var (model, drawn) in sections.Zip(layout.Sections))
        {
            drawn.Rows.Select(r => r.Key).ShouldBe(model.Fields.Select(f => f.Key));

            foreach (var (field, row) in model.Fields.Zip(drawn.Rows))
            {
                var shown = JobEditorModel.Presentation(field);
                (row.Unit is not null).ShouldBe(shown.Unit is not null, $"{field.Key} unit box");
                row.Editor.Height.ShouldBe(shown.Editor == FieldEditor.Lines ? 64 : 24, field.Key);
                row.Description.Y.ShouldBeGreaterThanOrEqualTo(row.Editor.Bottom, "the description sits under its editor");
            }
        }

        var foreign = JobEditorModel.Foreign(view).Select(f => f.Key).ToArray();
        layout.Foreign.Select(r => r.Key).ShouldBe(foreign);
        (layout.ForeignHeader is not null).ShouldBe(foreign.Length > 0);
    }

    [Fact]
    public void TheWindowFitsASmallScreenAtACommonScale()
    {
        var layout = Layout("blank");

        (layout.ClientHeight * 1.25).ShouldBeLessThanOrEqualTo(720, "at 125 per cent, with a title bar, on 768 rows");
        (layout.ClientWidth * 1.25).ShouldBeLessThanOrEqualTo(1024);
    }

    /// <summary>The content is wider than nothing and narrower than the viewport less a scrollbar.</summary>
    [Fact]
    public void TheContentLeavesRoomForAScrollbar() =>
        Layout("blank").ContentWidth.ShouldBeLessThan(Layout("blank").Viewport.Width);

    /// <summary>The arithmetic every rule above leans on.</summary>
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

