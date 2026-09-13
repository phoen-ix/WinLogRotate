using Shouldly;
using WinLogRotate.Gui.Cli;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// Where the two small windows put things, asserted where neither can be opened.
/// </summary>
/// <remarks>
/// <para>
/// <c>SecretPrompt</c> sized itself as <c>150 + 24 × fields</c>, so for Pushover's two fields
/// its Store and Cancel buttons sat at 194..220 in a 198-pixel client: four pixels of button.
/// <c>LrDialog</c>'s "Show details" grew the form and placed the text box over the OK button,
/// which stayed where the collapsed layout had put it - and every error in the product goes
/// through that dialog.
/// </para>
/// <para>
/// Both layouts are records now, and the same four rules the job editor's layout answers to are
/// asked of them here, in every state each window can be in.
/// </para>
/// </remarks>
public sealed class DialogLayoutTests
{
    // ---- the credential prompt ------------------------------------------------------------

    /// <summary>One field is email; two is Pushover; three is a provider kind not yet written.</summary>
    public static TheoryData<int> Fields => [1, 2, 3];

    [Theory]
    [MemberData(nameof(Fields))]
    public void ThePromptsBoxesArePositiveDisjointAndInsideTheWindow(int fields)
    {
        var layout = SecretPromptLayout.Compute(fields);
        var boxes = layout.All.ToArray();

        foreach (var (name, box) in boxes)
        {
            box.Width.ShouldBeGreaterThan(0, $"{name} has no width");
            box.Height.ShouldBeGreaterThan(0, $"{name} has no height");
            box.Within(layout.ClientWidth, layout.ClientHeight).ShouldBeTrue($"{name} is outside the window");
        }

        boxes
            .SelectMany((a, i) => boxes.Skip(i + 1)
                .Where(b => a.Box.Overlaps(b.Box))
                .Select(b => $"{a.Name} overlaps {b.Name}"))
            .ShouldBeEmpty();
    }

    /// <summary>The buttons are whole, whatever the provider asks for.</summary>
    /// <remarks>
    /// The defect: with two fields the buttons had four visible pixels. Named separately from the
    /// rule above because a prompt whose Store button cannot be pressed stores nothing.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Fields))]
    public void TheStoreAndCancelButtonsAreWhole(int fields)
    {
        var layout = SecretPromptLayout.Compute(fields);

        layout.Store.Bottom.ShouldBeLessThan(layout.ClientHeight);
        layout.Cancel.Bottom.ShouldBeLessThan(layout.ClientHeight);
        layout.Store.Y.ShouldBeGreaterThanOrEqualTo(layout.Again.Bottom, "the buttons are under the boxes");
    }

    /// <summary>A choice is offered only when there is one.</summary>
    /// <remarks>
    /// A single radio for a single field is a question with one answer. The form used to build it
    /// invisible, at a position that overlapped the caption below - harmless while hidden, and a
    /// box a layout rule would rightly refuse.
    /// </remarks>
    [Fact]
    public void AChoiceIsOfferedOnlyWhenThereIsOne()
    {
        var one = SecretPromptLayout.Compute(1);
        one.Which.ShouldBeNull();
        one.Radios.ShouldBeEmpty();

        var two = SecretPromptLayout.Compute(2);
        two.Which.ShouldNotBeNull();
        two.Radios.Count.ShouldBe(2);
    }

    // ---- the message dialog -----------------------------------------------------------------

    /// <summary>Every state the dialog can be in.</summary>
    public static TheoryData<bool, bool, bool> States
    {
        get
        {
            var states = new TheoryData<bool, bool, bool>();

            foreach (var hasDetails in new[] { false, true })
            {
                foreach (var showing in new[] { false, true })
                {
                    foreach (var yesNo in new[] { false, true })
                    {
                        states.Add(hasDetails, showing, yesNo);
                    }
                }
            }

            return states;
        }
    }

    [Theory]
    [MemberData(nameof(States))]
    public void TheDialogsBoxesArePositiveDisjointAndInsideTheWindow(bool hasDetails, bool showing, bool yesNo)
    {
        var layout = DialogLayout.Compute(hasDetails, showing, yesNo);
        var boxes = layout.All.ToArray();

        foreach (var (name, box) in boxes)
        {
            box.Width.ShouldBeGreaterThan(0, $"{name} has no width");
            box.Height.ShouldBeGreaterThan(0, $"{name} has no height");
            box.Within(layout.ClientWidth, layout.ClientHeight).ShouldBeTrue($"{name} is outside the window");
        }

        boxes
            .SelectMany((a, i) => boxes.Skip(i + 1)
                .Where(b => a.Box.Overlaps(b.Box))
                .Select(b => $"{a.Name} overlaps {b.Name}"))
            .ShouldBeEmpty();
    }

    /// <summary>
    /// The buttons move under the details when the details are shown.
    /// </summary>
    /// <remarks>
    /// The defect itself. Expanding grew the window and placed the text box at 116..256 while OK
    /// stayed at 136..164, so the box was painted over the button that closes the dialog.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheButtonsAreUnderTheDetailsWhenTheyAreShown(bool yesNo)
    {
        var collapsed = DialogLayout.Compute(hasDetails: true, showingDetails: false, yesNo);
        var expanded = DialogLayout.Compute(hasDetails: true, showingDetails: true, yesNo);

        collapsed.Details.ShouldBeNull();

        var details = expanded.Details.ShouldNotBeNull();
        expanded.Affirmative.Y.ShouldBeGreaterThanOrEqualTo(details.Bottom);
        expanded.Negative?.Y.ShouldBeGreaterThanOrEqualTo(details.Bottom);

        expanded.ClientHeight.ShouldBeGreaterThan(collapsed.ClientHeight, "the window grows to hold the details");
        expanded.Affirmative.Bottom.ShouldBeLessThan(expanded.ClientHeight);
    }

    /// <summary>A dialog with nothing to expand offers nothing to expand.</summary>
    [Fact]
    public void ADialogWithNoDetailsHasNoExpander()
    {
        var plain = DialogLayout.Compute(hasDetails: false, showingDetails: true, yesNo: false);

        plain.Expander.ShouldBeNull();
        plain.Copy.ShouldBeNull();
        plain.Details.ShouldBeNull("there is nothing to show, whatever the toggle says");
        plain.Negative.ShouldBeNull();
    }

    /// <summary>A question has both answers, side by side and whole.</summary>
    [Fact]
    public void AQuestionHasBothAnswers()
    {
        var question = DialogLayout.Compute(hasDetails: false, showingDetails: false, yesNo: true);

        var no = question.Negative.ShouldNotBeNull();
        no.Y.ShouldBe(question.Affirmative.Y);
        no.X.ShouldBeGreaterThanOrEqualTo(question.Affirmative.Right);
        no.Right.ShouldBeLessThanOrEqualTo(question.ClientWidth);
    }
}
