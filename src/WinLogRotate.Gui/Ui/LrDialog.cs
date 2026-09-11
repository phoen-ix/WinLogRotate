using System.Runtime.InteropServices;

namespace WinLogRotate.Gui.Ui;

/// <summary>How serious a dialog is.</summary>
public enum DialogKind
{
    Info,
    Warning,
    Error,
    Question,
}

/// <summary>
/// The dialog this application uses instead of <see cref="MessageBox"/>.
/// </summary>
/// <remarks>
/// <para>
/// <c>MessageBox</c> renders light regardless of <c>Application.SetColorMode</c>, so a single
/// call ruins a dark-themed window. An architecture test fails the build if <c>MessageBox</c>
/// appears anywhere in this project.
/// </para>
/// <para>
/// Replacing it also buys two things it has never had: a details expander, for the CLI's stderr
/// when an operation fails, and a copy button - because "what exactly did it say?" is the first
/// question in every support conversation and reading an error back over the phone is nobody's
/// idea of a good time.
/// </para>
/// </remarks>
public static partial class LrDialog
{
    private const uint BcmSetShield = 0x160C;

    public static DialogResult Show(
        IWin32Window? owner, DialogKind kind, string title, string message,
        string? details = null, MessageBoxButtons buttons = MessageBoxButtons.OK,
        bool affirmativeNeedsAdmin = false)
    {
        using var form = Build(kind, title, message, details, buttons, affirmativeNeedsAdmin);
        return owner is null ? form.ShowDialog() : form.ShowDialog(owner);
    }

    public static void Info(IWin32Window? owner, string title, string message, string? details = null) =>
        Show(owner, DialogKind.Info, title, message, details);

    public static void Error(IWin32Window? owner, string title, string message, string? details = null) =>
        Show(owner, DialogKind.Error, title, message, details);

    public static bool Confirm(
        IWin32Window? owner, string title, string message, bool needsAdmin = false) =>
        Show(owner, DialogKind.Question, title, message,
            buttons: MessageBoxButtons.YesNo, affirmativeNeedsAdmin: needsAdmin) == DialogResult.Yes;

    /// <summary>
    /// Whether there is anything to expand.
    /// </summary>
    /// <remarks>
    /// Empty and absent are the same thing to a reader, and were not the same thing here: three
    /// call sites passed the elevated path's StdErr, which is the empty string because a runas
    /// child has no readable console - so a dialog opened an expander and a Copy button over
    /// nothing at all.
    /// </remarks>
    private static bool Absent(string? details) => string.IsNullOrWhiteSpace(details);

    private static Form Build(
        DialogKind kind, string title, string message, string? details,
        MessageBoxButtons buttons, bool affirmativeNeedsAdmin)
    {
        var colors = Theme.Current;

        var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(460, Absent(details) ? 150 : 180),
            BackColor = colors.Window,
            ForeColor = colors.Text,
        };

        var label = new Label
        {
            Text = message,
            AutoSize = false,
            Bounds = new Rectangle(16, 16, 428, 70),
            ForeColor = kind == DialogKind.Error ? colors.Danger
                : kind == DialogKind.Warning ? colors.Warning
                : colors.Text,
        };
        form.Controls.Add(label);

        TextBox? detailBox = null;
        if (!Absent(details))
        {
            detailBox = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font(FontFamily.GenericMonospace, 8.25f),
                Text = details,
                Bounds = new Rectangle(16, 92, 428, 0),
                Visible = false,
                BackColor = colors.Surface,
                ForeColor = colors.Text,
            };
            form.Controls.Add(detailBox);

            var expander = new LinkLabel
            {
                Text = "Show details",
                Bounds = new Rectangle(16, 92, 120, 20),
                LinkColor = colors.Accent,
            };
            expander.LinkClicked += (_, _) =>
            {
                var showing = !detailBox.Visible;
                detailBox.Visible = showing;
                detailBox.Bounds = new Rectangle(16, 116, 428, showing ? 140 : 0);
                expander.Text = showing ? "Hide details" : "Show details";
                form.ClientSize = new Size(460, showing ? 330 : 180);
            };
            form.Controls.Add(expander);

            var copy = new Button
            {
                Text = "Copy",
                Bounds = new Rectangle(150, 90, 70, 24),
                FlatStyle = FlatStyle.System,
            };
            copy.Click += (_, _) =>
            {
                // Clipboard access can fail if another process holds it; a failed copy must not
                // take down the dialog reporting the original problem.
                try
                {
                    Clipboard.SetText($"{title}\r\n\r\n{message}\r\n\r\n{details}");
                }
                catch (ExternalException)
                {
                }
            };
            form.Controls.Add(copy);
        }

        var affirmative = new Button
        {
            Text = buttons == MessageBoxButtons.YesNo ? "Yes" : "OK",
            DialogResult = buttons == MessageBoxButtons.YesNo ? DialogResult.Yes : DialogResult.OK,
            Bounds = new Rectangle(buttons == MessageBoxButtons.YesNo ? 260 : 344, 0, 90, 28),
            FlatStyle = FlatStyle.System,
        };
        affirmative.Top = form.ClientSize.Height - 44;
        form.Controls.Add(affirmative);
        form.AcceptButton = affirmative;

        if (affirmativeNeedsAdmin)
        {
            AddShield(affirmative);
        }

        if (buttons == MessageBoxButtons.YesNo)
        {
            var negative = new Button
            {
                Text = "No",
                DialogResult = DialogResult.No,
                Bounds = new Rectangle(356, affirmative.Top, 90, 28),
                FlatStyle = FlatStyle.System,
            };
            form.Controls.Add(negative);
            form.CancelButton = negative;
        }
        else
        {
            form.CancelButton = affirmative;
        }

        return form;
    }

    /// <summary>
    /// Puts the UAC shield on a button.
    /// </summary>
    /// <remarks>
    /// <c>BCM_SETSHIELD</c> is silently ignored on an owner-drawn button, which is why
    /// <see cref="FlatStyle.System"/> is set first. Half the shield code in circulation omits
    /// that and quietly does nothing at all.
    /// </remarks>
    public static void AddShield(Button button)
    {
        button.FlatStyle = FlatStyle.System;

        void Apply(object? sender, EventArgs e) =>
            SendMessage(button.Handle, BcmSetShield, 0, 1);

        if (button.IsHandleCreated)
        {
            Apply(null, EventArgs.Empty);
        }
        else
        {
            button.HandleCreated += Apply;
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);
}
