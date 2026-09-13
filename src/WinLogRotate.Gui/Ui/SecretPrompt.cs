using WinLogRotate.Gui.Cli;

namespace WinLogRotate.Gui.Ui;

/// <summary>What the operator chose to store, and where.</summary>
public sealed record SecretRequest(string Provider, string Field, char[] Value);

/// <summary>
/// Asks for a credential, twice, and never puts it in a string.
/// </summary>
/// <remarks>
/// <para>
/// The value is carried as a <c>char[]</c> from the text box to the pipe and cleared afterwards.
/// Not because that defeats a determined attacker with a debugger - it does not - but because a
/// string would live in the managed heap until a collection that may never come, and would land
/// in any crash dump taken in between. A char array can at least be zeroed.
/// </para>
/// <para>
/// It is also why nothing here builds a <c>SecretString</c>: an architecture test enumerates the
/// files allowed to call <c>Reveal</c>, no GUI file is on it, and none should be. The value goes
/// from this dialog to the pipe and is never a type the rest of the product knows about.
/// </para>
/// <para>
/// Two boxes, matching what the console does. There is no "show" toggle: the console deliberately
/// echoes nothing at all, not even asterisks, because a shoulder-surfer counting them learns the
/// length and the length is most of what they need.
/// </para>
/// </remarks>
public static class SecretPrompt
{
    /// <summary>Fields a provider of this kind can be given, matching the CLI's own list.</summary>
    public static string[] FieldsFor(string kind) => kind.ToLowerInvariant() switch
    {
        "email" => ["password"],
        "pushover" => ["token", "user_key"],
        _ => ["url"],
    };

    /// <summary>Null if the operator cancelled, or if the two entries differed.</summary>
    /// <remarks>
    /// Every position comes from <see cref="SecretPromptLayout"/>. The form used to size itself
    /// first and place its buttons last, from a <c>y</c> that had grown past the height it had
    /// chosen - so for a provider with two fields, Store and Cancel had four visible pixels.
    /// </remarks>
    public static SecretRequest? Ask(IWin32Window owner, string provider, string kind)
    {
        var fields = FieldsFor(kind);
        var colors = Theme.Current;
        var layout = SecretPromptLayout.Compute(fields.Length);

        using var form = new Form
        {
            Text = $"Credential for {provider}",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(layout.ClientWidth, layout.ClientHeight),
            BackColor = colors.Window,
            ForeColor = colors.Text,
        };

        var picked = new List<RadioButton>();

        if (layout.Which is { } which)
        {
            form.Controls.Add(new Label
            {
                Text = "Which credential:",
                Bounds = which.ToRectangle(),
                ForeColor = colors.Muted,
            });
        }

        // One radio per field when there is a choice, and none when there is not: a single
        // radio for a single field is a question with one answer.
        for (var i = 0; i < layout.Radios.Count; i++)
        {
            var radio = new RadioButton
            {
                Text = fields[i],
                Bounds = layout.Radios[i].ToRectangle(),
                Checked = i == 0,
            };

            picked.Add(radio);
            form.Controls.Add(radio);
        }

        var value = new TextBox
        {
            Bounds = layout.Value.ToRectangle(),
            UseSystemPasswordChar = true,
        };

        var again = new TextBox
        {
            Bounds = layout.Again.ToRectangle(),
            UseSystemPasswordChar = true,
        };

        form.Controls.Add(new Label { Text = "Value", Bounds = layout.ValueCaption.ToRectangle(), ForeColor = colors.Muted });
        form.Controls.Add(value);
        form.Controls.Add(new Label { Text = "Repeat it", Bounds = layout.RepeatCaption.ToRectangle(), ForeColor = colors.Muted });
        form.Controls.Add(again);

        var ok = new Button
        {
            Text = "Store",
            Bounds = layout.Store.ToRectangle(),
            FlatStyle = FlatStyle.System,
            DialogResult = DialogResult.OK,
        };

        var cancel = new Button
        {
            Text = "Cancel",
            Bounds = layout.Cancel.ToRectangle(),
            FlatStyle = FlatStyle.System,
            DialogResult = DialogResult.Cancel,
        };

        LrDialog.AddShield(ok);
        form.Controls.Add(ok);
        form.Controls.Add(cancel);
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        // The same walk the main window got. It was coloured by hand here, which left the text
        // boxes drawing their own white on a dark form - survivable, inconsistent, and one more
        // place for the next control added to be missed.
        Theme.Apply(form);

        var answered = owner is null ? form.ShowDialog() : form.ShowDialog(owner);

        try
        {
            if (answered != DialogResult.OK)
            {
                return null;
            }

            if (value.Text.Length == 0)
            {
                LrDialog.Error(owner, "Credential",
                    "An empty value is not stored.",
                    "Use 'winlogrotate secret remove' to delete one instead.");
                return null;
            }

            if (!string.Equals(value.Text, again.Text, StringComparison.Ordinal))
            {
                LrDialog.Error(owner, "Credential", "The two entries did not match.");
                return null;
            }

            var field = picked.FirstOrDefault(r => r.Checked)?.Text ?? fields[0];
            return new SecretRequest(provider, field, value.Text.ToCharArray());
        }
        finally
        {
            // The boxes are cleared either way. What the framework already copied into its own
            // buffers is out of reach, and pretending otherwise would be the wrong lesson.
            value.Clear();
            again.Clear();
        }
    }
}
