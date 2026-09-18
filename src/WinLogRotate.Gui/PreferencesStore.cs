using WinLogRotate.Core;
using WinLogRotate.Gui.Cli;

namespace WinLogRotate.Gui;

/// <summary>
/// Where <see cref="GuiPreferences"/> lives on disk: one small file under the user's own
/// local application data.
/// </summary>
/// <remarks>
/// Every failure reads as the defaults and every failure to write is swallowed. A console that
/// cannot remember when it last checked for updates checks again next time; that is the whole
/// cost, and it is not one worth a dialog.
/// </remarks>
internal static class PreferencesStore
{
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ProductInfo.Name,
        GuiPreferences.FileName);

    public static GuiPreferences Load()
    {
        try
        {
            return File.Exists(Path) ? GuiPreferences.Parse(File.ReadAllText(Path)) : GuiPreferences.Default;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return GuiPreferences.Default;
        }
    }

    public static void Save(GuiPreferences preferences)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

            // Written beside and moved into place, so a crash mid-write leaves the old file
            // rather than half of the new one.
            var temporary = Path + ".tmp";
            File.WriteAllText(temporary, preferences.ToJson());
            File.Move(temporary, Path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
