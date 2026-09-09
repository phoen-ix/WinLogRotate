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
    public static SecretRequest? Ask(IWin32Window owner, string provider, string kind)
    {
        var fields = FieldsFor(kind);
        var colors = Theme.Current;

        using var form = new Form
        {
            Text = $"Credential for {provider}",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(420, 150 + (fields.Length * 24)),
            BackColor = colors.Window,
            ForeColor = colors.Text,
        };

        var picked = new List<RadioButton>();
        var y = 14;

        if (fields.Length > 1)
        {
            form.Controls.Add(new Label
            {
                Text = "Which credential:",
                Bounds = new Rectangle(16, y, 380, 18),
                ForeColor = colors.Muted,
            });

            y += 22;
        }

        foreach (var field in fields)
        {
            var radio = new RadioButton
            {
                Text = field,
                Bounds = new Rectangle(20, y, 380, 22),
                Checked = picked.Count == 0,
                Visible = fields.Length > 1,
            };

            picked.Add(radio);
            form.Controls.Add(radio);
            y += fields.Length > 1 ? 24 : 0;
        }

        var value = new TextBox
        {
            Bounds = new Rectangle(16, y + 20, 388, 24),
            UseSystemPasswordChar = true,
        };

        var again = new TextBox
        {
            Bounds = new Rectangle(16, y + 74, 388, 24),
            UseSystemPasswordChar = true,
        };

        form.Controls.Add(new Label { Text = "Value", Bounds = new Rectangle(16, y + 2, 200, 16), ForeColor = colors.Muted });
        form.Controls.Add(value);
        form.Controls.Add(new Label { Text = "Repeat it", Bounds = new Rectangle(16, y + 56, 200, 16), ForeColor = colors.Muted });
        form.Controls.Add(again);

        var ok = new Button
        {
            Text = "Store",
            Bounds = new Rectangle(228, y + 110, 84, 26),
            FlatStyle = FlatStyle.System,
            DialogResult = DialogResult.OK,
        };

        var cancel = new Button
        {
            Text = "Cancel",
            Bounds = new Rectangle(320, y + 110, 84, 26),
            FlatStyle = FlatStyle.System,
            DialogResult = DialogResult.Cancel,
        };

        LrDialog.AddShield(ok);
        form.Controls.Add(ok);
        form.Controls.Add(cancel);
        form.AcceptButton = ok;
        form.CancelButton = cancel;

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
