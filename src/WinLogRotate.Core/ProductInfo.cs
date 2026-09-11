using System.Reflection;

namespace WinLogRotate.Core;

/// <summary>Identity of the running build. The version is stamped by CI via -p:Version.</summary>
public static class ProductInfo
{
    /// <summary>Product name, as it appears in paths, the registry and the Event Log.</summary>
    public const string Name = "WinLogRotate";

    /// <summary>
    /// Wire-format version of the --json envelope. Bump on a breaking change.
    /// <para>
    /// The GUI runs <c>winlogrotate --version --json</c> once on start and compares the
    /// envelope's schema against this constant. A mismatch is <b>reported in a banner, and
    /// nothing is refused</b>: it can only make what that window displays wrong, because every
    /// action shells out and the child validates its own arguments - and if the check is itself
    /// wrong, an unnecessary banner is a far better failure than a window that will not open.
    /// <c>WinLogRotate.Gui.Cli.CliIdentity</c> is the only thing that reads it.
    /// </para></summary>
    public const int ContractSchema = 1;

    /// <summary>
    /// Three-part version of the running assembly ("1.4.0"), or "0.0.0" for a local build.
    /// </summary>
    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetName().Version is { } v
            ? Normalize(v).ToString(3)
            : "0.0.0";

    /// <summary>
    /// Reads absent version components as zero.
    /// <para>
    /// The SDK stamps a FOUR-part version ("1.4.0.0") into the assembly, while the git tag,
    /// the update feed and every string we print are three-part ("1.4.0") - and
    /// <see cref="System.Version"/> ranks an absent component (-1) BELOW 0, so "1.4.0" and
    /// "1.4.0.0" do not compare equal. The reference project shipped that bug for four
    /// releases: its updater concluded the exe it had just unpacked was not the release it
    /// claimed to be, and refused every portable self-update. Do not "simplify" this away -
    /// <c>NormalizeTreatsMissingComponentsAsZero</c> in the test suite exists to stop you.
    /// </para>
    /// </summary>
    public static Version Normalize(Version v) =>
        new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

    /// <summary>True when <paramref name="candidate"/> is a strictly newer release than
    /// <paramref name="current"/>, comparing three-part and four-part strings correctly.</summary>
    public static bool IsNewer(Version current, Version candidate) =>
        Normalize(candidate) > Normalize(current);

    /// <summary>True when both versions name the same release, however many parts each carries.</summary>
    public static bool SameVersion(Version a, Version b) =>
        Normalize(a) == Normalize(b);
}
