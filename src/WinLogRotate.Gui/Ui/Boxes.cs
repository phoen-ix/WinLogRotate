using WinLogRotate.Gui.Cli;

namespace WinLogRotate.Gui.Ui;

/// <summary>
/// Turns the model's layout boxes into the framework's rectangles.
/// </summary>
/// <remarks>
/// The model computes every position in logical pixels and knows nothing of
/// <c>System.Drawing</c>, so that its arithmetic can be asserted on the leg that cannot open a
/// window. This is the one line that bridges the two, kept out of the forms so that each of them
/// does not grow its own.
/// </remarks>
internal static class Boxes
{
    public static Rectangle ToRectangle(this Box box) => new(box.X, box.Y, box.Width, box.Height);
}
