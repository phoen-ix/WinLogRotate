using Shouldly;
using WinLogRotate.Gui.Cli;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// The rules the job editor's layout has to keep on every page, held here because the window
/// cannot be opened on the leg that runs these.
/// </summary>
public sealed class EditorLayoutTests
{
    private const string Shown = """
        {"schema":1,"ok":true,"exitCode":0,"result":{"job":"iis","path":"C:\\pd\\conf.d\\iis.toml","diagnostics":[],
         "keys":[{"key":"name","source":"\"iis\"","line":4,"known":true},
                 {"key":"rotate","source":"14","line":6,"known":true},
                 {"key":"ownr","source":"\"team-web\"","line":8,"known":false},
                 {"key":"colour","source":"\"blue\"","line":9,"known":false}]}}
        """;

    public static TheoryData<string> Views => ["blank", "loaded"];

    private static JobEditorView View(string which) =>
        which == "blank" ? JobEditorModel.Blank() : JobEditorModel.From(Shown);

    private static EditorLayout Layout(string which)
    {
        var view = View(which);
        return EditorLayout.Compute(
            JobEditorModel.Pages(view),
            [.. JobEditorModel.Foreign(view).Select(f => f.Key)]);
    }

    [Theory]
    [MemberData(nameof(Views))]
    public void EveryBoxHasAPositiveSize(string which)
    {
        var layout = Layout(which);

        foreach (var (name, box) in layout.Fixed.Concat(layout.Pages.SelectMany(p => p.Boxes)))
        {
            box.Width.ShouldBeGreaterThan(0, $"{name} has no width");
            box.Height.ShouldBeGreaterThan(0, $"{name} has no height");
        }
    }

    /// <summary>Each page shares the window with the chrome, never with another page.</summary>
    [Theory]
    [MemberData(nameof(Views))]
    public void NothingShownTogetherOverlaps(string which)
    {
        var layout = Layout(which);

        foreach (var page in layout.Pages)
        {
            Overlaps([.. layout.Fixed, .. page.Boxes]).ShouldBeEmpty($"on the {page.Id} page");
        }
    }

    private static string[] Overlaps((string Name, Box Box)[] boxes) =>
        [.. boxes.SelectMany((a, i) => boxes.Skip(i + 1)
            .Where(b => a.Box.Overlaps(b.Box))
            .Select(b => $"{a.Name} overlaps {b.Name}"))];

    [Theory]
    [MemberData(nameof(Views))]
    public void EverythingIsInsideTheWindow(string which)
    {
        var layout = Layout(which);

        layout.Fixed.Concat(layout.Pages.SelectMany(p => p.Boxes))
            .Where(b => !b.Box.Within(layout.ClientWidth, layout.ClientHeight))
            .Select(b => b.Name)
            .ShouldBeEmpty("outside the window");

        foreach (var button in new[] { layout.Check, layout.Save, layout.Cancel, layout.WhatWouldHappen })
        {
            button.Bottom.ShouldBeLessThan(layout.ClientHeight, "a button off the bottom edge cannot be pressed");
            button.Right.ShouldBeLessThanOrEqualTo(layout.ClientWidth);
        }
    }

    /// <summary>Title, then the page, then the summary, the status line and the buttons.</summary>
    [Theory]
    [MemberData(nameof(Views))]
    public void EveryPageSitsBetweenItsTitleAndTheSummary(string which)
    {
        var layout = Layout(which);

        foreach (var page in layout.Pages)
        {
            foreach (var (name, box) in page.Boxes)
            {
                box.Y.ShouldBeGreaterThanOrEqualTo(layout.PageTitle.Bottom, $"{name} above the title");
                box.Bottom.ShouldBeLessThanOrEqualTo(layout.Summary.Y, $"{name} runs into the summary");
                box.X.ShouldBeGreaterThanOrEqualTo(layout.Menu.Right, $"{name} runs into the menu");
            }
        }

        layout.Menu.Bottom.ShouldBeLessThanOrEqualTo(layout.Summary.Y);
        layout.Status.Y.ShouldBeGreaterThanOrEqualTo(layout.Summary.Bottom);
        layout.Check.Y.ShouldBeGreaterThanOrEqualTo(layout.Status.Bottom);
    }

    /// <summary>The pages drawn are the model's pages, and every row on one is the model's field.</summary>
    [Theory]
    [MemberData(nameof(Views))]
    public void ThereIsARowForEveryFieldOnEveryPage(string which)
    {
        var view = View(which);
        var layout = Layout(which);
        var pages = JobEditorModel.Pages(view);

        layout.Pages.Select(p => p.Id).ShouldBe(pages.Select(p => p.Id));

        foreach (var (model, drawn) in pages.Zip(layout.Pages))
        {
            drawn.Rows.Select(r => r.Key).ShouldBe(model.More.Select(f => f.Key), model.Menu);
            (drawn.MoreDivider is not null).ShouldBe(model.More.Count > 0 && drawn is not RowsPageLayout, $"{model.Menu} divider");

            foreach (var (field, row) in model.More.Zip(drawn.Rows))
            {
                var shown = JobEditorModel.Presentation(field);
                (row.Unit is not null).ShouldBe(shown.Unit is not null, $"{field.Key} unit box");
                row.Editor.Height.ShouldBe(shown.Editor == FieldEditor.Lines ? 64 : 24, field.Key);
                row.Description.Y.ShouldBeGreaterThanOrEqualTo(row.Editor.Bottom, "the description sits under its editor");
                row.Caption.Y.ShouldBeGreaterThanOrEqualTo(drawn.MoreDivider?.Bottom ?? 0, "a row under the divider");
            }
        }

        var foreign = JobEditorModel.Foreign(view).Select(f => f.Key).ToArray();
        (layout.Other is not null).ShouldBe(foreign.Length > 0);
        (layout.Other?.Foreign.Select(r => r.Key) ?? []).ShouldBe(foreign);
    }

    [Fact]
    public void TheMenuHasRoomForEveryEntry()
    {
        var layout = Layout("loaded");

        foreach (var page in JobEditorModel.Pages(View("loaded")))
        {
            layout.Menu.Width.ShouldBeGreaterThanOrEqualTo((JobEditorModel.MenuText(page, hasProblem: true).Length * 6) + 12, page.Menu);
        }
    }

    /// <summary>What the screenshot showed cramped, given room: the files box, the How note, the radios.</summary>
    [Fact]
    public void TheThingsThatWereCrampedHaveRoom()
    {
        var layout = Layout("blank");

        layout.Files.Files.Height.ShouldBeGreaterThanOrEqualTo(120, "one line per file, and a dozen is normal");
        layout.How.HowHint.Height.ShouldBeGreaterThanOrEqualTo(36, "the longest note needs two lines");
        layout.How.How.Count.ShouldBe(5);
        (layout.How.How[1].Y - layout.How.How[0].Y).ShouldBeGreaterThanOrEqualTo(24, "radio pitch");
        layout.Summary.Height.ShouldBeGreaterThanOrEqualTo(36, "the sentence wraps to two lines");
        layout.Keep.KeepLead.Width.ShouldBeGreaterThanOrEqualTo((JobEditorText.KeepLead.Length * 6) + 6);
        layout.When.EarlyLead.Width.ShouldBeGreaterThanOrEqualTo((JobEditorText.EarlyLead.Length * 6) + 6);
        layout.Copies.CreateOldDir.Width.ShouldBeGreaterThanOrEqualTo((JobEditorText.CreateOldDir.Length * 6) + 20);
    }

    [Fact]
    public void TheWindowFitsASmallScreenAtACommonScale()
    {
        var layout = Layout("blank");

        (layout.ClientHeight * 1.25).ShouldBeLessThanOrEqualTo(720, "at 125 per cent, with a title bar, on 768 rows");
        (layout.ClientWidth * 1.25).ShouldBeLessThanOrEqualTo(1024);
    }

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
