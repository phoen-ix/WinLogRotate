using WinLogRotate.Cli.Output;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Core.Configuration;
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
    /// <summary>
    /// Why this build cannot register the requested host, or null when it can.
    /// </summary>
    /// <remarks>
    /// Honest rather than silently falling back to a task, which would leave the operator
    /// believing they had chosen something they had not. Pure, and called before any side
    /// effect, so "refuse without touching what is already registered" is a property a test can
    /// hold rather than a comment somebody has to keep obeying.
    /// </remarks>
    internal static CliDiagnostic? Unsupported(RunHostKind wanted) =>
        wanted != RunHostKind.Service
            ? null
            : new CliDiagnostic
            {
                Severity = Severity.Error,
                Code = DiagnosticCode.HostRegistrationFailed,
                Message = "The service host is not implemented in this build.",
                Remedy = "Use 'winlogrotate host use task'. A scheduled task is the better fit for a log rotator anyway: nothing stays resident, and a run missed while the machine was off is caught up afterwards.",
            };

    public static int Use(CommandContext ctx, string kind, string? configDir, string? at) =>
        Use(ctx, kind, configDir, host: null, elevated: null, verb: "host use", at);

    /// <summary>
    /// The same, with the registrar, the elevation question and the reporting verb supplied.
    /// </summary>
    /// <param name="verb">
    /// <c>host repair</c> reaches this too, and used to be reported as <c>host use</c> - a verb
    /// the caller had not typed and a script matching on the envelope would not find.
    /// </param>
    /// <param name="at">
    /// <c>--at</c>: the time of day the task fires, written to <c>[host]</c> in config.toml
    /// before the task is registered. Null means the configured time, which is how
    /// <c>host repair</c> keeps the time it was given.
    /// </param>
    internal static int Use(
        CommandContext ctx, string kind, string? configDir, IRunHost? host, Func<bool>? elevated, string verb,
        string? at = null)
    {
        if (!ConfigBinder.TryParseName<RunHostKind>(kind, out var wanted))
        {
            return Refusals.CannotUse<HostResult>(
                ctx, verb, kind, "a run model", "Use task or none.");
        }

        // Judged here, above the platform guard, like Unsupported below: a fact about the
        // arguments is the same answer everywhere, and refusing it before anything is touched is
        // a property the tests can hold where the tests run.
        TimeSpan? requested = null;

        if (at is not null)
        {
            if (wanted != RunHostKind.Task)
            {
                return Refusals.CannotUse<HostResult>(
                    ctx, verb, "--at", $"an option of 'host use {kind}'",
                    "It says when the scheduled task fires. Leave it out, or use 'host use task --at HH:mm'.");
            }

            if (!HostSettings.TryParseTime(at, out var parsed))
            {
                return Refusals.CannotUse<HostResult>(
                    ctx, verb, at, "a time of day",
                    "Write it as HH:mm on a 24-hour clock, e.g. --at 03:00 or --at 22:30.");
            }

            requested = parsed;
        }

        // Before the platform guard, before the elevation check, and - the part that matters -
        // before anything is uninstalled. Asking for a host this build does not have used to
        // remove the working scheduled task first and refuse afterwards, so `host use service`
        // left the machine with nothing running rotations at all. The GUI's Scheduling page
        // offers it as a radio button, so it was one click away.
        //
        // Above the Windows guard deliberately: "not implemented in this build" is a fact about
        // the build rather than the operating system, it is the more specific answer, and it is
        // the same answer everywhere.
        if (Unsupported(wanted) is { } unsupported)
        {
            ctx.Output.Diagnostic(unsupported);
            return ctx.Output.Complete<HostResult>(verb, ExitCode.Errors, null);
        }

        if (!OperatingSystem.IsWindows())
        {
            return NotOnWindows(ctx, verb);
        }

        if (!(elevated ?? Elevated)())
        {
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Error,
                Code = DiagnosticCode.NeedsAdministrator,
                Message = "Registering or removing a run host needs administrator rights.",
                Remedy = "Run this from an elevated prompt, or use the Scheduling page in the GUI.",
            });
            return ctx.Output.Complete<HostResult>(verb, ExitCode.Errors, null);
        }

        var paths = InstallPaths.Resolve(configDir);
        var task = host ?? new TaskRunHost(TimeProvider.System);

        // The configuration first, then the task. host repair and host export-task read the time
        // back from config.toml, so a task registered at a time the file does not hold is exactly
        // the drift the [host] table exists to end: the next repair would put 03:00 back without
        // a word. If the registration below then fails, the file says 22:30 and the remedy is to
        // run this again. Read back after it is written, so what is registered is what the file
        // will say tomorrow and not merely what was typed today.
        HostSettings? settings = null;

        if (wanted == RunHostKind.Task)
        {
            if (requested is { } chosen && Remember(paths, chosen) is { } notWritten)
            {
                ctx.Output.Diagnostic(notWritten);
                return ctx.Output.Complete<HostResult>(verb, ExitCode.ConfigInvalid, null);
            }

            var configured = ConfiguredTime(paths);

            foreach (var d in configured.Diagnostics)
            {
                ctx.Output.Diagnostic(d);
            }

            settings = configured.Settings;

            if (settings is null)
            {
                ctx.Output.Line("winlogrotate: the time the task fires could not be read from the configuration; nothing was registered.");
                return ctx.Output.Complete<HostResult>(verb, ExitCode.ConfigInvalid, null);
            }
        }

        try
        {
            if (wanted == RunHostKind.None)
            {
                ctx.Output.Line("Removing any existing run host...");
                task.Uninstall();
                task.Record(RunHostKind.None);

                ctx.Output.Line("Nothing will run rotations now. Trigger them with 'winlogrotate run'.");
                return ctx.Output.Complete(verb, ExitCode.Ok, Describe(RunHostKind.None, paths));
            }

            // Not removed first. `schtasks /create /f` replaces the registered task as a whole, so
            // the one that works stays in place until its replacement is accepted. This used to
            // delete it and then register, and a registration schtasks refused - a policy, a
            // task folder with changed permissions - left the machine with nothing running
            // rotations, reported as a defect.
            ctx.Output.Line($"Registering the scheduled task, daily at {settings!.TimeText}...");
            task.Install(new HostInstallOptions
            {
                ExecutablePath = Environment.ProcessPath ?? "winlogrotate.exe",
                ConfigDirectory = paths.Root,
                TimeOfDay = settings.Time,
            });
        }
        catch (HostRegistrationException e)
        {
            ctx.Output.Diagnostic(RegistrationFailed(e));
            return ctx.Output.Complete<HostResult>(verb, ExitCode.Errors, null);
        }

        var status = task.Query();
        if (!status.Registered)
        {
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Error,
                Code = DiagnosticCode.HostRegistrationFailed,
                Message = "The task was created but could not be read back.",
            });
            return ctx.Output.Complete<HostResult>(verb, ExitCode.Errors, null);
        }

        task.Record(RunHostKind.Task);

        ctx.Output.Line($"Done. Rotations will run daily at {settings.TimeText} as SYSTEM. Check with 'winlogrotate host status'.");
        return ctx.Output.Complete(verb, ExitCode.Ok, Describe(RunHostKind.Task, paths, settings.TimeText));
    }

    /// <summary>What the configuration says about the task's time, or why it could not say.</summary>
    /// <remarks>
    /// <see cref="Settings"/> is null exactly when something in <see cref="Diagnostics"/> is an
    /// error; the warnings - an unknown key - ride along with a usable answer.
    /// </remarks>
    internal sealed record ConfiguredHost(HostSettings? Settings, IReadOnlyList<CliDiagnostic> Diagnostics);

    /// <summary>
    /// The time the configuration says the task fires.
    /// </summary>
    /// <remarks>
    /// One reader for every verb that needs the answer - use, repair, status, export-task and
    /// doctor - so that none of them can disagree about it. No config.toml means the default; one
    /// that cannot be read, does not parse, or holds a time the registrar could not use is a
    /// refusal rather than a quiet 03:00, because a task registered at a time the file does not
    /// hold is the drift the table exists to end.
    /// </remarks>
    internal static ConfiguredHost ConfiguredTime(InstallPaths paths)
    {
        if (!File.Exists(paths.ConfigFile))
        {
            return new ConfiguredHost(HostSettings.Default, []);
        }

        TomlFile file;

        try
        {
            file = TomlFile.Load(paths.ConfigFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new ConfiguredHost(null,
            [
                new CliDiagnostic
                {
                    Severity = Severity.Error,
                    Code = DiagnosticCode.ConfigUnreadable,
                    Message = $"{paths.ConfigFile} could not be read: {e.Message}",
                    Path = paths.ConfigFile,
                    Remedy = "The time the scheduled task fires is read from it. Fix the file, then run this again.",
                },
            ]);
        }

        if (file.HasErrors)
        {
            return new ConfiguredHost(null, [DoesNotParse(paths)]);
        }

        var bag = new DiagnosticBag();
        var settings = ConfigBinder.BindHost(file, bag);
        var carried = bag.Items.Select(Carry).ToArray();

        return new ConfiguredHost(bag.Items.Any(d => d.Severity >= Severity.Error) ? null : settings, carried);
    }

    /// <summary>
    /// Writes the time into <c>[host]</c> in config.toml, or says why it could not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The table is appended when the file has none, which is every installation older than
    /// the table. <see cref="TomlEditor"/> deliberately never creates one, and that is right for
    /// a job or a provider - but a top-level table at the very end of a file is the one placement
    /// that cannot change the meaning of anything above it. A file that is not there at all - a
    /// portable copy nobody has configured - is created with the table and nothing else.
    /// </para>
    /// <para>
    /// Nothing is written over a file that does not parse, for the reason set-secret gives: a
    /// rewrite driven by a partial parse is how a typo becomes data loss.
    /// </para>
    /// </remarks>
    internal static CliDiagnostic? Remember(InstallPaths paths, TimeSpan time)
    {
        var text = new HostSettings { Time = time }.TimeText;
        TomlFile file;

        // In the file's own line ending, never the machine's - TomlEditor's rule. The installer
        // seeds config.toml with CRLF, and a table appended with "\n" inside and
        // Environment.NewLine around it left one file with both.
        string Table(string eol) => $"[host]{eol}time = \"{text}\"{eol}";

        if (!File.Exists(paths.ConfigFile))
        {
            file = TomlFile.Parse($"schema = 1\n\n{Table("\n")}", paths.ConfigFile);
        }
        else
        {
            try
            {
                file = TomlFile.Load(paths.ConfigFile);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new CliDiagnostic
                {
                    Severity = Severity.Error,
                    Code = DiagnosticCode.ConfigUnreadable,
                    Message = $"{paths.ConfigFile} could not be read: {e.Message}",
                    Path = paths.ConfigFile,
                    Remedy = "The time is written there before the task is registered. Fix the file, then run this again.",
                };
            }

            if (file.HasErrors)
            {
                return DoesNotParse(paths);
            }

            if (!TomlEditor.TrySet(file, ["host"], "time", text, out var error, out var detail))
            {
                if (error != TomlEditError.NoSuchTable)
                {
                    return new CliDiagnostic
                    {
                        Severity = Severity.Error,
                        Code = DiagnosticCode.ConfigUnwritable,
                        Message = $"'time' could not be written to [host] in {paths.ConfigFile}: {detail}",
                        Path = paths.ConfigFile,
                        Remedy = "Set time = \"HH:mm\" under [host] by hand, then run 'winlogrotate host use task'.",
                    };
                }

                var written = file.ToString();
                var eol = written.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

                file = TomlFile.Parse(
                    written.TrimEnd('\r', '\n') + eol + eol + Table(eol), paths.ConfigFile);
            }
        }

        try
        {
            ConfigWrites.Config(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new CliDiagnostic
            {
                Severity = Severity.Error,
                Code = DiagnosticCode.ConfigUnwritable,
                Message = $"{paths.ConfigFile} could not be written: {e.Message}",
                Path = paths.ConfigFile,
                Remedy = "Nothing was registered. Check the directory exists and that this account may write to it.",
            };
        }

        return null;
    }

    private static CliDiagnostic DoesNotParse(InstallPaths paths) => new()
    {
        Severity = Severity.Error,
        Code = DiagnosticCode.ConfigInvalid,
        Message = $"{paths.ConfigFile} does not parse, so the time the scheduled task fires cannot be read from it.",
        Path = paths.ConfigFile,
        Remedy = "Run 'winlogrotate config check' and fix it first.",
    };

    /// <summary>A binder's finding, as the envelope carries one - the mapping RunCommand makes inline.</summary>
    private static CliDiagnostic Carry(ConfigDiagnostic d) => new()
    {
        Severity = d.Severity,
        Code = d.Code,
        Message = d.Message,
        Path = d.File,
        Line = d.Line == 0 ? null : d.Line,
        Column = d.Column == 0 ? null : d.Column,
        Remedy = d.Remedy,
    };

    /// <summary>
    /// What the registrar said, under the code that means it, with the one fact the operator
    /// needs before anything else: nothing was taken away.
    /// </summary>
    internal static CliDiagnostic RegistrationFailed(HostRegistrationException e) => new()
    {
        Severity = Severity.Error,
        Code = DiagnosticCode.HostRegistrationFailed,
        Message = $"The run host could not be changed: {e.Message}",
        Remedy = "Whatever was registered before is still registered. The text above is what "
               + "schtasks.exe said; a policy restricting who may create tasks, or changed "
               + "permissions on the task folder, are the usual causes.",
    };

    public static int Status(CommandContext ctx, string? configDir)
    {
        if (!OperatingSystem.IsWindows())
        {
            return NotOnWindows(ctx, "host status");
        }

        var paths = InstallPaths.Resolve(configDir);
        var status = new TaskRunHost(TimeProvider.System).Query();

        ctx.Output.Line($"run host      {(status.Registered ? "scheduled task" : "none")}");
        ctx.Output.Line($"config        {paths.Root}");

        // Reported, never failed on: status says what is, and a config.toml that will not parse
        // is one of the things that is. Exit 0 still means "here is the status".
        var configured = ConfiguredTime(paths);

        foreach (var d in configured.Diagnostics)
        {
            ctx.Output.Diagnostic(d with { Severity = Severity.Warning });
        }

        if (configured.Settings is { } time)
        {
            ctx.Output.Line($"runs at       {time.TimeText} (config.toml)");
        }

        // Drift first, because it is the more specific answer to the same observation. "Nothing
        // is registered" is a fact about now; "this install was set up with a task and the task is
        // gone" says somebody removed it, which is a different problem with a different cause.
        if (status.Drifted && status.Configured != RunHostKind.None)
        {
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Warning,
                Code = DiagnosticCode.HostDrift,
                Message = $"This install was set up to run rotations as a "
                        + $"{status.Configured.ToString().ToLowerInvariant()}, but that is not "
                        + "registered any more.",
                Remedy = "Something removed it - a Group Policy sweep, a cleanup script, or a "
                       + "hand edit. Re-register with 'winlogrotate host use task'.",
            });
        }
        else if (!status.Registered)
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
            Describe(status.Registered ? RunHostKind.Task : RunHostKind.None, paths,
                status.Registered ? configured.Settings?.TimeText : null));
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
            return Use(ctx, "task", configDir, host: null, elevated: null, verb: "host repair", at: null);
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

        // Refused rather than obeyed. The hardened descriptor grants SYSTEM and Administrators
        // full control and everyone else read - so applying it to a per-user installation, whose
        // configuration lives in that user's own profile, would take away their write access to
        // their own jobs. They would then be unable to edit or repair anything, which is a
        // considerably worse outcome than the hooks they were already not getting.
        if (paths.Scope == InstallScope.PerUser)
        {
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Warning,
                Code = DiagnosticCode.ConfigDirectoryInsecure,
                Message = $"'{paths.Root}' belongs to a per-user installation and was left alone.",
                Path = paths.Root,
                Remedy = "A per-user configuration directory is writable by its owner by definition, "
                       + "so hooks are refused there and no permission change would alter that. "
                       + "Install for all users if you need hooks.",
            });

            ctx.Output.Line($"{paths.Root} is a per-user directory; its permissions were left as they are.");
            return ctx.Output.Complete("host repair", ExitCode.Ok, Describe(RunHostKind.None, paths));
        }

        try
        {
            ConfDirGuard.Apply(paths);
            ctx.Output.Line($"Secured {paths.Root}: SYSTEM and Administrators only, inheritance severed.");
        }
        // InvalidOperationException joins the two that were here: giving a file away is a new
        // way for this to fail, and it is the one an attacker holding a file open produces.
        catch (Exception e) when (e is UnauthorizedAccessException
                                      or IOException
                                      or InvalidOperationException)
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

        var finding = ConfDirGuard.Verify(paths);
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

    /// <summary>What decides whether this process is elevated. A seam for the tests.</summary>
    private static readonly Func<bool> Elevated = Privilege.IsElevated;

    /// <summary>
    /// Adds or removes the install directory on PATH.
    /// </summary>
    /// <remarks>
    /// Done here rather than in the installer for a specific reason: stock makensis is built
    /// with NSIS_MAX_STRLEN=1024, and its ReadRegStr silently truncates a longer PATH. Writing
    /// that truncated value back destroys the machine PATH for every program on the system.
    /// </remarks>
    public static int Path(CommandContext ctx, bool add, bool machine) =>
        Path(ctx, add, machine, elevated: null);

    /// <summary>The same, with the elevation question answerable by a test.</summary>
    internal static int Path(CommandContext ctx, bool add, bool machine, Func<bool>? elevated)
    {
        // Spelled as the command tree spells them. Both used to report "host path", which is not
        // a verb: a script matching on the envelope, or an operator following a remedy that
        // named it, found nothing by that name.
        var verb = add ? "host path-add" : "host path-remove";

        if (!OperatingSystem.IsWindows())
        {
            return NotOnWindows(ctx, verb);
        }

        var directory = System.IO.Path.GetDirectoryName(Environment.ProcessPath);
        if (directory is null)
        {
            // Said, rather than returned in silence on every channel at once - no diagnostic, no
            // line, not even a comment. An executable with no directory is not something an
            // operator can have caused or can fix, which is exactly what LR1006 is for.
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Error,
                Code = DiagnosticCode.InternalError,
                Message = $"This executable reports its own location as '{Environment.ProcessPath}', "
                        + "which has no directory to add to PATH.",
                Remedy = "This is a defect. Please report it, with where winlogrotate.exe is installed.",
            });

            return ctx.Output.Complete<PathResult>(verb, ExitCode.Errors, null);
        }

        // Scope follows the INSTALL, not the token. Deciding from elevation alone means a
        // per-user install performed by an administrator - which is most of them, and every one
        // on a CI runner - silently edits the machine PATH for everybody. The installer knows
        // which kind of install it is doing and says so; elevation only gates whether the
        // machine PATH can be written at all. An unelevated --machine is refused, not quietly
        // redirected at this user's PATH with exit 0, which is what it used to do: the caller
        // was told the machine PATH had been edited when it had not been touched.
        if (machine && !(elevated ?? Elevated)())
        {
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Error,
                Code = DiagnosticCode.NeedsAdministrator,
                Message = "Editing the machine PATH needs administrator rights.",
                Remedy = "Run this from an elevated prompt, or leave out --machine to edit this user's PATH.",
            });

            return ctx.Output.Complete<PathResult>(verb, ExitCode.Errors, null);
        }

        var target = machine ? EnvironmentVariableTarget.Machine : EnvironmentVariableTarget.User;

        // Through the registry, not Environment.GetEnvironmentVariable: that pair expands every
        // %VAR% on the way out and writes REG_SZ on the way back, so the stock REG_EXPAND_SZ
        // machine PATH left every default install as hard-coded text. PathEnvironment says why.
        RegistryText current;
        try
        {
            current = PathEnvironment.Read(target);
        }
        catch (Exception e) when (IsRefusal(e))
        {
            return Untouched(ctx, verb, target, $"could not be read: {e.GetType().Name}: {e.Message}");
        }

        if (!current.IsText)
        {
            return Untouched(ctx, verb, target,
                $"is stored as {current.Kind} rather than as text, which something else must have done");
        }

        var (edited, changed) = PathEdit.Apply(current.Value, directory, add);

        if (!changed)
        {
            ctx.Output.Line(add ? "Already on PATH." : "Not on PATH.");

            // Three outcomes, one null payload. A caller could not tell "added" from "removed"
            // from "it was already there", and the lines that say so are a no-op under --json.
            return ctx.Output.Complete(verb, ExitCode.Ok, new PathResult
            {
                Directory = directory,
                Scope = target.ToString(),
                Action = "unchanged",
            });
        }

        try
        {
            PathEnvironment.Write(target, current with { Value = edited });
        }
        catch (Exception e) when (IsRefusal(e))
        {
            return Untouched(ctx, verb, target, $"could not be written: {e.GetType().Name}: {e.Message}");
        }

        ctx.Output.Line(add
            ? $"Added {directory} to the {target} PATH."
            : $"Removed {directory} from the {target} PATH.");

        return ctx.Output.Complete(verb, ExitCode.Ok, new PathResult
        {
            Directory = directory,
            Scope = target.ToString(),
            Action = add ? "added" : "removed",
        });
    }

    /// <summary>The ways a registry value refuses an account, none of them a defect.</summary>
    private static bool IsRefusal(Exception e) =>
        e is IOException or UnauthorizedAccessException or System.Security.SecurityException;

    /// <summary>The PATH was not edited, and the value is exactly as it was found.</summary>
    private static int Untouched(CommandContext ctx, string verb, EnvironmentVariableTarget target, string because)
    {
        ctx.Output.Diagnostic(new CliDiagnostic
        {
            Severity = Severity.Error,
            Code = DiagnosticCode.PathUnwritable,
            Message = $"The {target} PATH {because}; it was left as it was.",
            Remedy = "Add or remove the install directory by hand under System Properties > "
                   + "Environment Variables, or run this again once the value can be edited.",
        });

        return ctx.Output.Complete<PathResult>(verb, ExitCode.Errors, null);
    }

    private static HostResult Describe(RunHostKind kind, InstallPaths paths, string? time = null) => new()
    {
        Host = kind.ToString(),
        ConfigRoot = paths.Root,
        Scope = paths.Scope.ToString(),
        Time = time,
    };

    private static int NotOnWindows(CommandContext ctx, string verb) =>
        Refusals.NeedsWindows<HostResult>(
            ctx, verb, "run hosts are Windows scheduled tasks and services.");
}
