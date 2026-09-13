namespace WinLogRotate.Gui.Cli;

/// <summary>
/// Where everything on the credential prompt goes, for a provider with this many fields.
/// </summary>
/// <remarks>
/// The prompt sized itself as <c>150 + 24 × fields</c> and placed its buttons at a <c>y</c> that
/// had already grown by the caption, the radio rows and the two boxes. For Pushover's two fields
/// that put Store and Cancel at 194..220 in a client 198 pixels tall: four pixels of button. The
/// height is now the last thing computed rather than the first.
/// </remarks>
public sealed record SecretPromptLayout
{
    /// <summary>"Which credential:", present only when there is a choice to make.</summary>
    public Box? Which { get; init; }

    /// <summary>One radio per field when there is more than one; none when there is not.</summary>
    public required IReadOnlyList<Box> Radios { get; init; }

    public required Box ValueCaption { get; init; }
    public required Box Value { get; init; }
    public required Box RepeatCaption { get; init; }
    public required Box Again { get; init; }
    public required Box Store { get; init; }
    public required Box Cancel { get; init; }
    public required int ClientWidth { get; init; }
    public required int ClientHeight { get; init; }

    /// <summary>Every box, named, for a rule that has to look at all of them.</summary>
    public IEnumerable<(string Name, Box Box)> All
    {
        get
        {
            if (Which is { } which)
            {
                yield return ("which", which);
            }

            for (var i = 0; i < Radios.Count; i++)
            {
                yield return ($"radio {i}", Radios[i]);
            }

            yield return ("value caption", ValueCaption);
            yield return ("value", Value);
            yield return ("repeat caption", RepeatCaption);
            yield return ("again", Again);
            yield return ("store", Store);
            yield return ("cancel", Cancel);
        }
    }

    public static SecretPromptLayout Compute(int fields)
    {
        const int Width = 420;
        const int Margin = 16;
        const int Inner = Width - (2 * Margin);

        var y = 14;
        Box? which = null;
        var radios = new List<Box>();

        if (fields > 1)
        {
            which = new Box(Margin, y, Inner, 18);
            y += 22;

            for (var i = 0; i < fields; i++)
            {
                radios.Add(new Box(Margin + 4, y, Inner, 22));
                y += 24;
            }
        }

        var store = new Box(228, y + 110, 84, 26);
        var cancel = new Box(320, y + 110, 84, 26);

        return new SecretPromptLayout
        {
            Which = which,
            Radios = radios,
            ValueCaption = new Box(Margin, y + 2, 200, 16),
            Value = new Box(Margin, y + 20, Inner, 24),
            RepeatCaption = new Box(Margin, y + 56, 200, 16),
            Again = new Box(Margin, y + 74, Inner, 24),
            Store = store,
            Cancel = cancel,
            ClientWidth = Width,
            ClientHeight = cancel.Bottom + 14,
        };
    }
}

/// <summary>
/// Where everything on the message dialog goes, in either state of its details expander.
/// </summary>
/// <remarks>
/// "Show details" grew the form to 330 and placed the text box at 116..256, and left OK, Yes and
/// No where they were at 136..164 - under the text box, which painted over them. Every error in
/// the product goes through this dialog, so every error with details had its OK button covered
/// by the details. The buttons are now placed after whatever is above them, and the form is as
/// tall as they need, in both states.
/// </remarks>
public sealed record DialogLayout
{
    public required Box Message { get; init; }

    /// <summary>"Show details", present when there are any.</summary>
    public Box? Expander { get; init; }

    /// <summary>The Copy button, present when there are details to copy.</summary>
    public Box? Copy { get; init; }

    /// <summary>The details box, present only while it is shown.</summary>
    public Box? Details { get; init; }

    /// <summary>OK, or Yes.</summary>
    public required Box Affirmative { get; init; }

    /// <summary>No, for a question.</summary>
    public Box? Negative { get; init; }

    public required int ClientWidth { get; init; }
    public required int ClientHeight { get; init; }

    /// <summary>Every box that is present, named, for a rule that has to look at all of them.</summary>
    public IEnumerable<(string Name, Box Box)> All
    {
        get
        {
            yield return ("message", Message);

            if (Expander is { } expander)
            {
                yield return ("expander", expander);
            }

            if (Copy is { } copy)
            {
                yield return ("copy", copy);
            }

            if (Details is { } details)
            {
                yield return ("details", details);
            }

            yield return ("affirmative", Affirmative);

            if (Negative is { } negative)
            {
                yield return ("negative", negative);
            }
        }
    }

    /// <param name="showingDetails">Ignored when there are no details to show.</param>
    public static DialogLayout Compute(bool hasDetails, bool showingDetails, bool yesNo)
    {
        const int Width = 460;
        const int Margin = 16;
        const int Inner = Width - (2 * Margin);

        var message = new Box(Margin, 16, Inner, 70);
        var showing = hasDetails && showingDetails;

        Box? expander = hasDetails ? new Box(Margin, 92, 120, 20) : null;
        Box? copy = hasDetails ? new Box(150, 90, 70, 24) : null;
        Box? details = showing ? new Box(Margin, 116, Inner, 140) : null;

        // The buttons go under the lowest thing present - which is the whole fix. They used to
        // stay where the collapsed state put them while the details box grew over them.
        var content = details?.Bottom ?? copy?.Bottom ?? message.Bottom;
        var top = content + 20;

        var affirmative = new Box(yesNo ? 260 : 344, top, 90, 28);
        Box? negative = yesNo ? new Box(356, top, 90, 28) : null;

        return new DialogLayout
        {
            Message = message,
            Expander = expander,
            Copy = copy,
            Details = details,
            Affirmative = affirmative,
            Negative = negative,
            ClientWidth = Width,
            ClientHeight = affirmative.Bottom + Margin,
        };
    }
}
