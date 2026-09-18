using System.Globalization;
using WinLogRotate.Core;

namespace WinLogRotate.Gui.Cli;

/// <summary>
/// Every sentence the Updates panel and the start-up banner can say.
/// </summary>
/// <remarks>
/// Here rather than on the page so a test can read them, and so they say the same thing in
/// the two places they appear. They are for the person at the window: no code, no verb, no
/// word they would have to look up.
/// </remarks>
public static class UpdateText
{
    public const string Title = "Updates";

    public const string OnlyWhenAsked = "Only when I ask";
    public const string OnceADay = "Check once a day when this window opens";

    public const string NeverChecked = "Never checked.";
    public const string Checking = "Checking…";
    public const string Downloading = "Downloading…";

    public const string DisabledByPolicy = "Update checks are disabled by policy on this machine.";
    public const string CouldNotCheck = "The release feed could not be reached. Nothing was changed.";

    public const string Installing =
        "Installing… This window closes while the files are replaced, and opens again at the new version.";

    public const string DidNotInstall =
        "The update did not install. A rotation may have been running; try again in a minute.";

    public static string LastChecked(DateTimeOffset local) =>
        $"Last checked {local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}.";

    public static string Available(string latest, string current) =>
        $"{ProductInfo.Name} {latest} is available (you have {current}).";

    public static string Newest(string current) =>
        $"You have the newest release, {current}.";

    /// <summary>Across the top of the window, when a scheduled check found one.</summary>
    public static string Banner(string latest) =>
        $"{ProductInfo.Name} {latest} is available. Install it from Settings.";

    /// <summary>After a hand-over that did not end with this window closing, once the files
    /// turn out to have been replaced after all.</summary>
    public static string InstalledButStillOpen(string version) =>
        $"{ProductInfo.Name} {version} is installed. Close this window and open it again.";
}
