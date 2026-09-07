using System.Runtime.InteropServices;

namespace WinLogRotate.Gui.Ui;

/// <summary>The palette for one appearance.</summary>
public sealed record ThemeColors
{
    public required Color Window { get; init; }
    public required Color Surface { get; init; }
    public required Color Text { get; init; }
    public required Color Muted { get; init; }
    public required Color Accent { get; init; }
    public required Color Danger { get; init; }
    public required Color Warning { get; init; }
    public required Color Border { get; init; }

    public static ThemeColors Light { get; } = new()
    {
        Window = Color.FromArgb(0xF6, 0xF7, 0xF9),
        Surface = Color.White,
        Text = Color.FromArgb(0x1E, 0x29, 0x3B),
        Muted = Color.FromArgb(0x64, 0x74, 0x8B),
        Accent = Color.FromArgb(0xC2, 0x7A, 0x0A),
        Danger = Color.FromArgb(0xB9, 0x1C, 0x1C),
        Warning = Color.FromArgb(0x92, 0x6C, 0x05),
        Border = Color.FromArgb(0xD6, 0xDB, 0xE1),
    };

    public static ThemeColors Dark { get; } = new()
    {
        Window = Color.FromArgb(0x1E, 0x29, 0x3B),
        Surface = Color.FromArgb(0x28, 0x35, 0x4A),
        Text = Color.FromArgb(0xE8, 0xED, 0xF4),
        Muted = Color.FromArgb(0x93, 0xA3, 0xB8),
        Accent = Color.FromArgb(0xF5, 0x9E, 0x0B),
        Danger = Color.FromArgb(0xEF, 0x44, 0x44),
        Warning = Color.FromArgb(0xFB, 0xBF, 0x24),
        Border = Color.FromArgb(0x3C, 0x4A, 0x60),
    };
}

/// <summary>
/// Applies a colour scheme by walking the control tree.
/// </summary>
/// <remarks>
/// <para>
/// .NET 10's <c>Application.SetColorMode</c> is treated as a bonus rather than the mechanism,
/// because it only works on Windows 11 (build 22000+) and this tool's audience is largely
/// Server 2019 and 2022, where it does nothing at all. The manual walk below works back to
/// Windows 10.
/// </para>
/// <para>
/// Two controls need explicit help that a naive walk does not give them: a
/// <see cref="DataGridView"/> keeps light column headers unless <c>EnableHeadersVisualStyles</c>
/// is turned off, and a <see cref="ListView"/> has no themed dark header at all.
/// </para>
/// </remarks>
public static partial class Theme
{
    private const int DwmwaUseImmersiveDarkMode = 20;

    public static ThemeColors Current { get; private set; } = ThemeColors.Light;

    public static bool IsDark => Current == ThemeColors.Dark;

    public static void Apply(Form form, bool dark)
    {
        Current = dark ? ThemeColors.Dark : ThemeColors.Light;

        // SetColorMode does not touch the title bar on Windows 10, so this is done directly
        // regardless of which mechanism coloured the client area.
        ApplyDarkTitleBar(form, dark);

        Walk(form, Current);
        form.BackColor = Current.Window;
    }

    private static void ApplyDarkTitleBar(Form form, bool dark)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            return;
        }

        var value = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(form.Handle, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
    }

    private static void Walk(Control control, ThemeColors colors)
    {
        switch (control)
        {
            case DataGridView grid:
                grid.BackgroundColor = colors.Surface;
                grid.GridColor = colors.Border;
                grid.DefaultCellStyle.BackColor = colors.Surface;
                grid.DefaultCellStyle.ForeColor = colors.Text;
                grid.DefaultCellStyle.SelectionBackColor = colors.Accent;
                grid.DefaultCellStyle.SelectionForeColor = colors.Window;

                // Without this the headers stay light and the grid looks broken in dark mode.
                grid.EnableHeadersVisualStyles = false;
                grid.ColumnHeadersDefaultCellStyle.BackColor = colors.Window;
                grid.ColumnHeadersDefaultCellStyle.ForeColor = colors.Text;
                break;

            case TextBox or RichTextBox or ListBox:
                control.BackColor = colors.Surface;
                control.ForeColor = colors.Text;
                break;

            case Button button:
                button.FlatStyle = FlatStyle.System;
                break;

            case LinkLabel link:
                link.LinkColor = colors.Accent;
                link.BackColor = Color.Transparent;
                link.ForeColor = colors.Text;
                break;

            default:
                control.BackColor = control.Parent is null ? colors.Window : control.BackColor;
                control.ForeColor = colors.Text;
                break;
        }

        foreach (Control child in control.Controls)
        {
            Walk(child, colors);
        }
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
