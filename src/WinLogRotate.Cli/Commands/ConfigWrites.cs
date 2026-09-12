using WinLogRotate.Core.Configuration;
using WinLogRotate.Hosting.Security;

namespace WinLogRotate.Cli.Commands;

/// <summary>
/// The two ways this product writes into its own configuration directory.
/// </summary>
/// <remarks>
/// One place, so that "and leave it owned by Administrators" is written once rather than
/// remembered at each call site. The gate judges the owner of every file it would execute a hook
/// from; a writer that forgets turns hooks off on a healthy machine, silently, until somebody
/// runs the repair - and then again on the next edit.
/// </remarks>
internal static class ConfigWrites
{
    /// <summary>
    /// What claims ownership in the product. Injected at the two call sites only by tests, which
    /// is why the default is a field and not a lambda: it can be asserted.
    /// </summary>
    internal static readonly Func<string, bool> Owner = ConfigFileOwner.Claim;

    /// <summary>Writes a job file into conf.d.</summary>
    internal static void Job(string path, string contents, Func<string, bool>? claim = null)
    {
        File.WriteAllText(path, contents);
        (claim ?? Owner)(path);
    }

    /// <summary>Saves config.toml back over itself.</summary>
    /// <remarks>
    /// <see cref="TomlFile.Save"/> writes a temporary sibling and moves it over the target, so
    /// the file that ends up in place was created by this process and carries this process's
    /// owner - not the one the original had.
    /// </remarks>
    internal static void Config(TomlFile file, Func<string, bool>? claim = null)
    {
        file.Save();
        (claim ?? Owner)(file.Path);
    }
}
