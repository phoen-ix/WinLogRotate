using WinLogRotate.Gui.Cli;

namespace WinLogRotate.Gui.Ui;

/// <summary>
/// Turns the model's layout boxes into the framework's rectangles.
/// </summary>
/// <remarks>
/// <para>
/// The model computes every position in logical pixels and knows nothing of
/// <c>System.Drawing</c>, so that its arithmetic can be asserted on the leg that cannot open a
/// window. This is the bridge between the two, kept out of the forms so that each of them does
/// not grow its own.
/// </para>
/// <para>
/// Two conversions, because a window scales once. Every window here sets
/// <c>AutoScaleMode.Dpi</c> with dimensions of 96 and calls <c>PerformAutoScale</c> when it has
/// been built, which scales whatever bounds exist by then - so a position set while building the
/// window is a logical one and must stay so. A position set afterwards, when a dialog rearranges
/// itself, is not scaled by anything and has to be scaled by hand, or a 125 per cent dialog
/// shrinks to 100 the moment its details are expanded.
/// </para>
/// </remarks>
internal static class Boxes
{
    /// <summary>A rectangle in logical pixels, for a control that has not been scaled yet.</summary>
    public static Rectangle ToRectangle(this Box box) => new(box.X, box.Y, box.Width, box.Height);

    /// <summary>
    /// A rectangle in whichever units <paramref name="on"/> is currently laid out in.
    /// </summary>
    /// <remarks>
    /// Logical until the window has scaled, because its scale is what will turn them into the
    /// monitor's; device afterwards, because it has scaled and will not scale again.
    /// </remarks>
    public static Rectangle Placed(this Box box, ContainerControl on) =>
        HasScaled(on)
            ? new Rectangle(
                on.LogicalToDeviceUnits(box.X),
                on.LogicalToDeviceUnits(box.Y),
                on.LogicalToDeviceUnits(box.Width),
                on.LogicalToDeviceUnits(box.Height))
            : box.ToRectangle();

    /// <summary>A client size, by the same rule as <see cref="Placed(Box, ContainerControl)"/>.</summary>
    public static Size Placed(int width, int height, ContainerControl on) =>
        HasScaled(on)
            ? new Size(on.LogicalToDeviceUnits(width), on.LogicalToDeviceUnits(height))
            : new Size(width, height);

    /// <summary>
    /// Whether the window's one scale has happened.
    /// </summary>
    /// <remarks>
    /// The framework records a scale by setting the design dimensions to the current ones, so
    /// the two are equal exactly when there is nothing left for it to do - after the scale, or
    /// on a monitor at 100 per cent, where the conversion is the identity either way.
    /// </remarks>
    private static bool HasScaled(ContainerControl on) =>
        on.AutoScaleDimensions == on.CurrentAutoScaleDimensions;
}
