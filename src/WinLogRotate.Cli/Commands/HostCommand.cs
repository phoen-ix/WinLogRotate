using System.Runtime.Versioning;
using WinLogRotate.Cli.Output;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Hosting;
using WinLogRotate.Hosting.Hosts;
using WinLogRotate.Hosting.Security;

namespace WinLogRotate.Cli.Commands;

/// <summary>
/// Manages what runs rotations.
/// </summary>
/// <remarks>
/// The installer and the GUI both call these verbs rather than duplicating the logic, which is
/// what keeps the install-time path exercised every time anyone changes their mind about
/// scheduling - instead of only once, during a setup nobody watches.
/// </remarks>
internal static class HostCommand
{
    public static int Use(CommandContext ctx, string kind, string? configDir)
    {
        if (!OperatingSystem.IsWindows())
        {
            return NotOnWindows(ctx, "host use");
        }

        if (!Enum.TryParse<RunHostKind>(kind, ignoreCase: true, out var wanted))
        {
            ctx.Output.Line($"winlogrotate: '{kind}' is not a run model.");
            ctx.Output.Line("Use task, service, or none.");
            return ctx.Output.Complete<HostResult>("host use", ExitCode.ConfigInvalid, null);
        }

        if (!Privilege.IsElevated())
        {
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Error,
                Code = DiagnosticCode.NeedsAdministrator,
                Message = "Registering or removing a run host needs administrator rights.",
                Remedy = "Run this from an elevated prompt, or use the Scheduling page in the GUI.",
            });
            return ctx.Output.Complete<HostResult>("host use", ExitCode.Errors, null);
        }

        var paths = InstallPaths.Resolve(configDir);
        var task = new TaskRunHost();

        // Remove whatever is registered first, whichever way we are switching. Doing it
        // unconditionally is what makes this idempotent and makes task -> service -> task
        // land in a known state rather than an accumulated one.
        ctx.Output.Line("Removing any existing run host...");
        task.Uninstall();

        if (wanted == RunHostKind.None)
        {
            ctx.Output.Line("Nothing will run rotations now. Trigger them with 'winlogrotate run'.");
            return ctx.Output.Complete("host use", ExitCode.Ok, Describe(RunHostKind.None, paths));
        }

        if (wanted == RunHostKind.Service)
        {
            // Honest rather than silently falling back to a task, which would leave the
            // operator believing they had chosen something they had not.
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Error,
                Code = DiagnosticCode.HostRegistrationFailed,
                Message = "The service host is not implemented in this build.",
                Remedy = "Use 'winlogrotate host use task'. A scheduled task is the better fit for a log rotator anyway: nothing stays resident, and a run missed while the machine was off is caught up afterwards.",
            });
            return ctx.Output.Complete<HostResult>("host use", ExitCode.Errors, null);
        }

        ctx.Output.Line("Registering the scheduled task...");
        task.Install(new HostInstallOptions
        {
            ExecutablePath = Environment.ProcessPath ?? "winlogrotate.exe",
            ConfigDirectory = paths.Root,
        });

        var status = task.Query();
        if (!status.Registered)
        {
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Error,
                Code = DiagnosticCode.HostRegistrationFailed,
                Message = "The task was created but could not be read back.",
            });
            return ctx.Output.Complete<HostResult>("host use", ExitCode.Errors, null);
        }

        ctx.Output.Line($"Done. Rotations will run daily as SYSTEM. Check with 'winlogrotate host status'.");
        return ctx.Output.Complete("host use", ExitCode.Ok, Describe(RunHostKind.Task, paths));
    }

    public static int Status(CommandContext ctx, string? configDir)
    {
        if (!OperatingSystem.IsWindows())
        {
            return NotOnWindows(ctx, "host status");
        }

        var paths = InstallPaths.Resolve(configDir);
        var status = new TaskRunHost().Query();

        ctx.Output.Line($"run host      {(status.Registered ? "scheduled task" : "none")}");
        ctx.Output.Line($"config        {paths.Root}");

        if (!status.Registered)
        {
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Warning,
                Code = DiagnosticCode.NoRunHost,
                Message = "Nothing is registered to run rotations.",
                Remedy = "winlogrotate host use task",
            });
        }

        return ctx.Output.Complete("host status", ExitCode.Ok,
            Describe(status.Registered ? RunHostKind.Task : RunHostKind.None, paths));
    }

    /// <summary>
    /// Puts the configuration directory's permissions back the way the installer left them.
    /// </summary>
    /// <remarks>
    /// The installer shells this rather than applying the ACL itself, so there is exactly one
    /// implementation of the rule and the GUI's repair button cannot drift away from it.
    /// </remarks>
    public static int Repair(CommandContext ctx, bool acl, string? configDir)
    {
        if (!OperatingSystem.IsWindows())
        {
            return NotOnWindows(ctx, "host repair");
        }

        var paths = InstallPaths.Resolve(configDir);

        if (!acl)
        {
            return Use(ctx, "task", configDir);
        }

        if (!Privilege.IsElevated())
        {
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Error,
                Code = DiagnosticCode.NeedsAdministrator,
                Message = "Changing directory permissions needs administrator rights.",
            });
            return ctx.Output.Complete<HostResult>("host repair", ExitCode.Errors, null);
        }

        try
        {
            ApplyAcl(paths);
            ctx.Output.Line($"Secured {paths.Root}: SYSTEM and Administrators only, inheritance severed.");
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Critical,
                Code = DiagnosticCode.ConfigDirectoryInsecure,
                Message = $"Could not secure {paths.Root}: {e.Message}",
                Remedy = "Until this succeeds, hooks are refused - a job file dropped there by a "
                       + "non-administrator would otherwise be executed by the run host.",
            });
            return ctx.Output.Complete<HostResult>("host repair", ExitCode.Errors, null);
        }

        var finding = ConfDirGuard.Verify(paths.ConfigDirectory);
        if (finding.Verdict != AclVerdict.Hardened)
        {
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Critical,
                Code = DiagnosticCode.ConfigDirectoryInsecure,
                Message = finding.Explanation ?? "The directory is still not secure.",
                Remedy = finding.FixCommand,
            });
            return ctx.Output.Complete<HostResult>("host repair", ExitCode.Errors, null);
        }

        return ctx.Output.Complete("host repair", ExitCode.Ok, Describe(RunHostKind.None, paths));
    }

    /// <summary>
    /// Adds or removes the install directory on PATH.
    /// </summary>
    /// <remarks>
    /// Done here rather than in the installer for a specific reason: stock makensis is built
    /// with NSIS_MAX_STRLEN=1024, and its ReadRegStr silently truncates a longer PATH. Writing
    /// that truncated value back destroys the machine PATH for every program on the system.
    /// </remarks>
    public static int Path(CommandContext ctx, bool add, bool machine)
    {
        if (!OperatingSystem.IsWindows())
        {
            return NotOnWindows(ctx, "host path");
        }

        var directory = System.IO.Path.GetDirectoryName(Environment.ProcessPath);
        if (directory is null)
        {
            return ctx.Output.Complete<HostResult>("host path", ExitCode.Errors, null);
        }

        // Scope follows the INSTALL, not the token. Deciding from elevation alone means a
        // per-user install performed by an administrator - which is most of them, and every one
        // on a CI runner - silently edits the machine PATH for everybody. The installer knows
        // which kind of install it is doing and says so; elevation only gates whether the
        // machine PATH can be written at all.
        var target = machine && Privilege.IsElevated()
            ? EnvironmentVariableTarget.Machine
            : EnvironmentVariableTarget.User;

        var current = Environment.GetEnvironmentVariable("PATH", target) ?? string.Empty;
        var parts = current.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();

        var already = parts.Any(p =>
            string.Equals(p.TrimEnd('\\'), directory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));

        if (add && !already)
        {
            parts.Add(directory);
        }
        else if (!add)
        {
            // Remove every occurrence: a repeated install could otherwise leave duplicates that
            // an uninstall only half-cleans.
            parts.RemoveAll(p =>
                string.Equals(p.TrimEnd('\\'), directory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            ctx.Output.Line("Already on PATH.");
            return ctx.Output.Complete<HostResult>("host path", ExitCode.Ok, null);
        }

        Environment.SetEnvironmentVariable("PATH", string.Join(';', parts), target);
        ctx.Output.Line(add
            ? $"Added {directory} to the {target} PATH."
            : $"Removed {directory} from the {target} PATH.");
        return ctx.Output.Complete<HostResult>("host path", ExitCode.Ok, null);
    }

    /// <summary>
    /// Applies the hardened descriptor to the data root and to every directory under it that
    /// we create.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each directory is set explicitly rather than relying on the root's inheritable ACEs
    /// propagating downward. Propagation is not enough here, and the reason is ProgramData's
    /// <c>CREATOR OWNER:(OI)(CI)(IO)(F)</c> entry: when a subdirectory is created beneath it,
    /// that entry materialises as a Full Control ACE for whoever created it - the elevated
    /// account running the installer. That account's own SID is not
    /// <c>BUILTIN\Administrators</c>, so <see cref="ConfDirGuard"/> correctly reads it as a
    /// non-administrator write grant and refuses every hook.
    /// </para>
    /// <para>
    /// It cost a shipped release to learn this. The installer hardened the root, the smoke test
    /// asserted the root, and conf.d - the only directory whose permissions actually matter,
    /// because it is the one holding files the run host executes - was never checked by either.
    /// </para>
    /// </remarks>
    [SupportedOSPlatform("windows")]
    private static void ApplyAcl(InstallPaths paths)
    {
        foreach (var directory in new[] { paths.Root, paths.ConfigDirectory, paths.JournalDirectory })
        {
            Directory.CreateDirectory(directory);

            var security = new System.Security.AccessControl.DirectorySecurity();
            security.SetSecurityDescriptorSddlForm(Sddl.ConfigDirectory);
            new DirectoryInfo(directory).SetAccessControl(security);
        }
    }

    private static HostResult Describe(RunHostKind kind, InstallPaths paths) => new()
    {
        Host = kind.ToString(),
        ConfigRoot = paths.Root,
        Scope = paths.Scope.ToString(),
    };

    private static int NotOnWindows(CommandContext ctx, string verb)
    {
        ctx.Output.Line($"winlogrotate: '{verb}' needs Windows.");
        return ctx.Output.Complete<HostResult>(verb, ExitCode.Errors, null);
    }
}
