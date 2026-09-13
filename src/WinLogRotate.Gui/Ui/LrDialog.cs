using System.Runtime.InteropServices;

using WinLogRotate.Core;
using WinLogRotate.Gui.Cli;

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

    /// <summary>
    /// Builds the dialog, with every position taken from <see cref="DialogLayout"/>.
    /// </summary>
    /// <remarks>
    /// The layout is computed twice - collapsed and expanded - and applied whole each time the
    /// expander is clicked. The old code grew the form and the details box by hand and left the
    /// buttons where the collapsed state had put them, so the details were painted over the OK
    /// button of every error dialog in the product.
    /// </remarks>
    private static Form Build(
        DialogKind kind, string title, string message, string? details,
        MessageBoxButtons buttons, bool affirmativeNeedsAdmin)
    {
        var colors = Theme.Current;
        var hasDetails = !Absent(details);
        var yesNo = buttons == MessageBoxButtons.YesNo;

        var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            BackColor = colors.Window,
            ForeColor = colors.Text,
        };

        var label = new Label
        {
            Text = message,
            AutoSize = false,
            ForeColor = kind == DialogKind.Error ? colors.Danger
                : kind == DialogKind.Warning ? colors.Warning
                : colors.Text,
        };
        form.Controls.Add(label);

        TextBox? detailBox = null;
        LinkLabel? expander = null;
        Button? copy = null;

        if (hasDetails)
        {
            // Disposed with the form. A Font is not owned by the control it is assigned to, so
            // one created inline here went with neither the box nor the dialog, once per error.
            var mono = new Font(FontFamily.GenericMonospace, 8.25f);
            form.Disposed += (_, _) => mono.Dispose();

            detailBox = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = mono,
                Text = details,
                Visible = false,
                BackColor = colors.Surface,
                ForeColor = colors.Text,
            };
            form.Controls.Add(detailBox);

            expander = new LinkLabel
            {
                Text = "Show details",
                LinkColor = colors.Accent,
            };
            form.Controls.Add(expander);

            copy = new Button
            {
                Text = "Copy",
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
            Text = yesNo ? "Yes" : "OK",
            DialogResult = yesNo ? DialogResult.Yes : DialogResult.OK,
            FlatStyle = FlatStyle.System,
        };
        form.Controls.Add(affirmative);
        form.AcceptButton = affirmative;

        if (affirmativeNeedsAdmin)
        {
            AddShield(affirmative);
        }

        Button? negative = null;

        if (yesNo)
        {
            negative = new Button
            {
                Text = "No",
                DialogResult = DialogResult.No,
                FlatStyle = FlatStyle.System,
            };
            form.Controls.Add(negative);
            form.CancelButton = negative;
        }
        else
        {
            form.CancelButton = affirmative;
        }

        void Arrange(bool showing)
        {
            var layout = DialogLayout.Compute(hasDetails, showing, yesNo);

            form.ClientSize = new Size(layout.ClientWidth, layout.ClientHeight);
            label.Bounds = layout.Message.ToRectangle();

            if (expander is not null && layout.Expander is { } e)
            {
                expander.Bounds = e.ToRectangle();
                expander.Text = showing ? "Hide details" : "Show details";
            }

            if (copy is not null && layout.Copy is { } c)
            {
                copy.Bounds = c.ToRectangle();
            }

            if (detailBox is not null)
            {
                detailBox.Visible = layout.Details is not null;
                detailBox.Bounds = (layout.Details ?? new Box(layout.Message.X, layout.Message.Bottom, layout.Message.Width, 0)).ToRectangle();
            }

            affirmative.Bounds = layout.Affirmative.ToRectangle();

            if (negative is not null && layout.Negative is { } n)
            {
                negative.Bounds = n.ToRectangle();
            }
        }

        Arrange(showing: false);

        if (expander is not null && detailBox is not null)
        {
            expander.LinkClicked += (_, _) => Arrange(showing: !detailBox.Visible);
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
    /// <para>
    /// And nothing is drawn when this process is already elevated. The shield means "pressing
    /// this raises a UAC prompt", and an elevated process raises none - a shield that promises a
    /// prompt which never comes is how people learn to ignore shields. The question is about
    /// <i>this window's</i> token, so it is asked of this process rather than read off a child's
    /// answer: it has to be answerable before any child exists, and the first page is built
    /// before the start-up probe returns.
    /// </para>
    /// </remarks>
    public static void AddShield(Button button)
    {
        if (Privilege.IsElevated())
        {
            return;
        }

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
