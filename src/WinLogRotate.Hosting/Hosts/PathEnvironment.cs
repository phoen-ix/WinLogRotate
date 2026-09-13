using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace WinLogRotate.Hosting.Hosts;

/// <summary>
/// One directory added to, or removed from, a semicolon-separated search path. Pure.
/// </summary>
/// <remarks>
/// <para>
/// Every entry that is not ours belongs to another program, and the only safe edit to somebody
/// else's PATH entry is none: nothing is trimmed, expanded, re-quoted or de-duplicated. A list
/// that needs no edit is handed back as the very string that came in, so the caller can tell
/// that nothing needs writing - a remove of a directory that was never there used to rewrite
/// the whole value anyway. Empty entries are the one liberty taken: <c>;;</c> is what a previous
/// careless editor left, not a directory.
/// </para>
/// <para>
/// Platform-neutral on purpose. The registry half is <see cref="PathEnvironment"/>; this half is
/// where the list arithmetic lives, and it runs on the Linux leg.
/// </para>
/// </remarks>
public static class PathEdit
{
    /// <summary>The list after the edit, and whether it differs from what came in.</summary>
    public static (string Value, bool Changed) Apply(string current, string directory, bool add)
    {
        var parts = current.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
        var present = parts.Any(p => Same(p, directory));

        if (add)
        {
            if (present)
            {
                return (current, false);
            }

            parts.Add(directory);
            return (string.Join(';', parts), true);
        }

        if (!present)
        {
            return (current, false);
        }

        // Every occurrence: a repeated install could otherwise leave duplicates that an
        // uninstall only half-cleans.
        parts.RemoveAll(p => Same(p, directory));
        return (string.Join(';', parts), true);
    }

    /// <summary>
    /// The same directory whether or not it ends in a backslash, was written in quotes, or
    /// differs in case. Comparison only; the entry itself is never rewritten.
    /// </summary>
    private static bool Same(string entry, string directory) =>
        string.Equals(Normalise(entry), Normalise(directory), StringComparison.OrdinalIgnoreCase);

    private static string Normalise(string entry) => entry.Trim().Trim('"').TrimEnd('\\');
}

/// <summary>A registry value that should be text: what it holds, unexpanded, and its type.</summary>
[SupportedOSPlatform("windows")]
public readonly record struct RegistryText(string Value, RegistryValueKind Kind)
{
    /// <summary>True for the two types that hold a string. Anything else is not a PATH we can edit.</summary>
    public bool IsText => Kind is RegistryValueKind.String or RegistryValueKind.ExpandString;
}

/// <summary>
/// The machine and user <c>Path</c> values, read and written as the registry holds them.
/// </summary>
/// <remarks>
/// <para>
/// Not through <see cref="Environment"/>. <c>GetEnvironmentVariable(name, target)</c> hands the
/// registry value back with every <c>%VAR%</c> already expanded, and
/// <c>SetEnvironmentVariable(name, value, target)</c> writes whatever it is given as a plain
/// <c>REG_SZ</c>. Round-tripping through that pair - which is what <c>host path-add</c> did -
/// turned the stock <c>REG_EXPAND_SZ</c> <c>%SystemRoot%\system32;...</c> into hard-coded text
/// on every all-users install and every uninstall, and any entry whose variable happened to be
/// unset in the installer's environment into a literal <c>%VAR%</c> that never expands again. The
/// installer's smoke test compares expanded text, so it could not see the type change.
/// </para>
/// <para>
/// So: read with <see cref="RegistryValueOptions.DoNotExpandEnvironmentNames"/>, write back under
/// the type the value already had, and send the <c>WM_SETTINGCHANGE</c> broadcast that
/// <see cref="Environment"/> would have sent - without it a console opened from Explorer keeps the
/// old PATH until the next logon.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static partial class PathEnvironment
{
    public const string ValueName = "Path";

    private const string MachineKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";
    private const string UserKey = "Environment";

    /// <summary>
    /// The stored text, unexpanded, and its type. A value that does not exist reads as empty
    /// <c>REG_EXPAND_SZ</c>, the type Windows itself gives a <c>Path</c>.
    /// </summary>
    /// <param name="name">The value to read; the tests point this at a scratch name so that they
    /// never touch the real <c>Path</c>.</param>
    public static RegistryText Read(EnvironmentVariableTarget target, string name = ValueName)
    {
        using var key = Open(target, writable: false);

        if (key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not { } raw)
        {
            return new RegistryText(string.Empty, RegistryValueKind.ExpandString);
        }

        // A REG_MULTI_SZ or REG_BINARY comes back as something other than a string; the kind is
        // kept so the caller can name what it found, and IsText says it is not ours to edit.
        return new RegistryText(raw as string ?? string.Empty, key.GetValueKind(name));
    }

    /// <summary>
    /// Writes the text under the type it was read with, then tells every window that the
    /// environment changed.
    /// </summary>
    public static void Write(EnvironmentVariableTarget target, RegistryText value, string name = ValueName)
    {
        if (!value.IsText)
        {
            throw new ArgumentException($"A {value.Kind} value is not a PATH and is not written as one.", nameof(value));
        }

        using (var key = Open(target, writable: true)
                         ?? throw new IOException($"The {target} environment key does not exist."))
        {
            key.SetValue(name, value.Value, value.Kind);
        }

        Broadcast();
    }

    private static RegistryKey? Open(EnvironmentVariableTarget target, bool writable) => target switch
    {
        EnvironmentVariableTarget.Machine => Registry.LocalMachine.OpenSubKey(MachineKey, writable),
        EnvironmentVariableTarget.User => Registry.CurrentUser.OpenSubKey(UserKey, writable),
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Only the machine and user scopes have a registry value."),
    };

    /// <summary>
    /// <c>WM_SETTINGCHANGE</c> to every top-level window, the way <see cref="Environment"/>
    /// sends it. A window that is hung or does not answer is not a failure of the edit, so the
    /// result is ignored and the wait is short.
    /// </summary>
    private static void Broadcast() =>
        _ = SendMessageTimeout(HwndBroadcast, WmSettingChange, 0, "Environment", SmtoAbortIfHung, 1000, out _);

    private const nint HwndBroadcast = 0xFFFF;
    private const uint WmSettingChange = 0x001A;
    private const uint SmtoAbortIfHung = 0x0002;

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint SendMessageTimeout(
        nint hWnd, uint msg, nuint wParam, string lParam, uint flags, uint timeoutMs, out nuint result);
}
