namespace WinLogRotate.Core;

/// <summary>Where a copy of WinLogRotate keeps its data.</summary>
public enum InstallScope
{
    /// <summary>No installer ran - a portable copy beside its own exe.</summary>
    Portable,

    /// <summary>Installed for one user. Rotates only what that user can reach.</summary>
    PerUser,

    /// <summary>Installed for the machine. Config in ProgramData, run host as SYSTEM.</summary>
    PerMachine,
}

/// <summary>
/// Resolves every path the product uses.
/// </summary>
/// <remarks>
/// The resolution order matters and mirrors the reference project's reasoning: an existing
/// configuration wins over a writability probe, so an elevated child process and its
/// unelevated parent always agree on which directory they are talking about. Probing first
/// would let the two disagree the moment one of them can write somewhere the other cannot.
/// </remarks>
public sealed record InstallPaths
{
    public required InstallScope Scope { get; init; }

    /// <summary>Directory holding config.toml, conf.d, state and the journal.</summary>
    public required string Root { get; init; }

    public string ConfigFile => Path.Combine(Root, "config.toml");
    public string ConfigDirectory => Path.Combine(Root, "conf.d");
    public string StateFile => Path.Combine(Root, "state.json");
    public string JournalDirectory => Path.Combine(Root, "journal");
    public string RunDirectory => Path.Combine(Root, "run");

    // There is deliberately no LogFile. Diagnostics go to stdout for whoever ran the command
    // and to the Windows Event Log at Warning and above; the record of what was actually done
    // to files is the journal, which is queryable and bounded. A third half-used sink would be
    // one more thing to rotate and one more place to look.

    /// <summary>Resolves the paths for this process, honouring an explicit override.</summary>
    public static InstallPaths Resolve(string? overrideRoot = null)
    {
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            return new InstallPaths { Scope = InstallScope.Portable, Root = Path.GetFullPath(overrideRoot) };
        }

        var machine = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            ProductInfo.Name);

        var user = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ProductInfo.Name);

        // An existing configuration decides, before any writability probe.
        if (File.Exists(Path.Combine(machine, "config.toml")) || Directory.Exists(Path.Combine(machine, "conf.d")))
        {
            return new InstallPaths { Scope = InstallScope.PerMachine, Root = machine };
        }

        if (File.Exists(Path.Combine(user, "config.toml")) || Directory.Exists(Path.Combine(user, "conf.d")))
        {
            return new InstallPaths { Scope = InstallScope.PerUser, Root = user };
        }

        // Nothing installed yet: per-machine is the default, because this is a system tool and
        // its natural job is rotating logs no single user owns.
        return new InstallPaths { Scope = InstallScope.PerMachine, Root = machine };
    }
}
