using System.CommandLine;
using WinLogRotate.Cli.Output;
using WinLogRotate.Core;
using WinLogRotate.Core.Engine;
using WinLogRotate.Core.Secrets;
using WinLogRotate.Hosting.Security;

namespace WinLogRotate.Cli.Commands;

/// <summary>
/// The whole verb tree. Kept in one file on purpose: the shape of the CLI is the product's
/// public contract, and it should be readable in one screenful rather than assembled from a
/// dozen registration calls.
/// </summary>
internal static class CommandTree
{
    public static RootCommand Build()
    {
        var root = new RootCommand($"{ProductInfo.Name} - log rotation for Windows.")
        {
            BuildRun(),
            BuildProbe(),
            BuildGlob(),
            BuildConfig(),
            BuildHost(),
            BuildImport(),
            BuildScan(),
            BuildJournal(),
            BuildDoctor(),
            BuildUpdate(),
            BuildSecret(),
            BuildNotify(),
        };

        GlobalOptions.AddTo(root, configDir: false);  // the root verb prints a banner or a version

        // Replace the built-in version option rather than adding beside it. Its action runs
        // first and prints bare text, so `--version --json` would emit no envelope at all -
        // and that envelope is exactly what the GUI reads on start to check it is talking to
        // a CLI whose contract schema it understands - CliIdentity, wired into
        // MainForm.CheckEnvironmentAsync.
        if (root.Options.FirstOrDefault(o => o is VersionOption) is { } builtIn)
        {
            root.Options.Remove(builtIn);
        }

        var version = new Option<bool>("--version", "-V")
        {
            Description = "Print the version and exit.",
        };
        root.Options.Add(version);

        root.SetAction(parse => CommandContext.Guarded(parse, ctx => parse.GetValue(version)
            ? VersionCommand.Run(ctx)
            : ShowBanner(ctx)));

        return root;
    }

    // Bare `winlogrotate` with no verb. Deliberately not a wall of help: an admin who typed
    // the name wants to know it is installed and what to type next, and --help is one word away.
    private static int ShowBanner(CommandContext ctx)
    {
        ctx.Output.Line($"{ProductInfo.Name} {ProductInfo.Version} - log rotation for Windows.");
        ctx.Output.Line("");
        ctx.Output.Line("Run 'winlogrotate --help' for the full command list.");
        return ctx.Output.Complete<EmptyResult>("", ExitCode.Ok, null);
    }

    /// <summary>
    /// Where a credential arrives from when there is no console to type it into.
    /// </summary>
    /// <remarks>
    /// The GUI's only way to hand a password to an elevated child: <c>Verb = "runas"</c> requires
    /// <c>UseShellExecute = true</c>, which forbids redirecting standard input. The pipe's NAME is
    /// what appears here, and it is not a credential - what protects the value is the server's
    /// ACL and its check that the process connecting is the one it launched. There is deliberately
    /// no <c>--value</c>, and there never will be.
    /// </remarks>
    private static readonly Option<string?> FromPipe =
        new("--from-pipe") { Description = "Read the value from this named pipe instead of standard input. Used by the GUI, which cannot redirect an elevated child's stdin." };

    /// <summary>Picks the pipe when one was named, and the console otherwise.</summary>
    /// <remarks>
    /// Internal so a test can assert the option is wired to something. Every other test of the
    /// pipe drives PipeInputSource directly, so all of them would pass with --from-pipe reduced
    /// to a flag nothing reads - and the GUI, whose only channel this is, would fall back to a
    /// console that is not there.
    /// </remarks>
    internal static IInputSource Input(System.CommandLine.ParseResult parse, IInputSource console) =>
        parse.GetValue(FromPipe) is { Length: > 0 } pipe
            ? new PipeInputSource(pipe, PipeInputSource.DefaultTimeout)
            : console;

    /// <summary>
    /// Reads a duration written as <c>01:00:00</c>, <c>45m</c> or <c>2h</c>.
    /// </summary>
    /// <remarks>
    /// The same grammar the configuration file uses, through the same parser, because an operator
    /// who has written <c>remind_after = "7d"</c> will write <c>--run-deadline 45m</c>. An
    /// unparseable value yields null, which means "no deadline" - the safe direction: a mistyped
    /// flag must not silently shorten the phase to nothing, and the run verb has no business
    /// failing over a notification hint.
    /// </remarks>
    private static TimeSpan? Duration(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && Core.Configuration.ConfigBinder.TryParseDuration(text, out var value)
        && value > TimeSpan.Zero
            ? value
            : null;

    // ---- rotation ---------------------------------------------------------------------

    private static readonly Option<bool> DryRun =
        new("--dry-run", "-d") { Description = "Show exactly what would happen and change nothing. State is not updated." };

    private static readonly Option<bool> Force =
        new("--force", "-f") { Description = "Rotate even if the schedule says it is not due. Does NOT override notifempty, minsize or minage - matching logrotate." };

    private static readonly Option<bool> Catchup =
        new("--catchup") { Description = "Rotate a log the first time it is seen, instead of recording a baseline and waiting one interval. Not logrotate behaviour; opt-in." };

    private static readonly Option<string?> JobFilter =
        new("--job") { Description = "Only this job." };

    private static readonly Option<FileInfo?> StateFile =
        new("--state") { Description = "Use an alternate state file." };

    private static readonly Option<bool> SkipStateLock =
        new("--skip-state-lock") { Description = "Do not take the run lock. For environments where locking is unavailable." };

    private static readonly Option<bool> WaitForStateLock =
        new("--wait-for-state-lock") { Description = "Block until the run lock is free instead of exiting 3." };

    /// <summary>
    /// On the run verb only, not global.
    /// </summary>
    /// <remarks>
    /// The GUI's "run now" button should not page whoever is on call. Global would put it in the
    /// help text of fourteen verbs that never notify, where it would be a lie.
    /// </remarks>
    private static readonly Option<bool> NoNotify =
        new("--no-notify") { Description = "Do not report this run's outcome to any notification target." };

    /// <summary>
    /// How long this whole invocation has, so the notification phase can stop short of the kill.
    /// </summary>
    /// <remarks>
    /// Passed by the registered task, generated from the same value that writes its
    /// <c>ExecutionTimeLimit</c>. Absent - which is every hand-run rotation - there is no clamp at
    /// all, because nothing is going to kill an interactive run and truncating it would make the
    /// interactive case behave differently from the scheduled one for no benefit.
    /// </remarks>
    private static readonly Option<string?> RunDeadline =
        new("--run-deadline") { Description = "How long this run has before its host kills it, e.g. 01:00:00 or 45m. Only the notification phase uses it, to stop short rather than be terminated mid-send." };

    private static readonly Option<int> LockHeldExit =
        new("--lock-held-exit") { Description = "Exit code to use when another run holds the lock. The registered scheduled task passes 0, because Task Scheduler renders 3 as 0x3 and an admin reads that as a failure.", DefaultValueFactory = _ => ExitCode.LockHeld };

    private static Command BuildRun()
    {
        var run = new Command("run", "Rotate everything that is due.")
        {
            DryRun, Force, Catchup, JobFilter, StateFile,
            SkipStateLock, WaitForStateLock, LockHeldExit, NoNotify, RunDeadline,
        };
        GlobalOptions.AddTo(run);
        run.SetAction(parse => CommandContext.Guarded(parse, ctx => RunCommand.Run(
            ctx,
            new RunOptions
            {
                DryRun = parse.GetValue(DryRun),
                Force = parse.GetValue(Force),
                Catchup = parse.GetValue(Catchup),
                OnlyJob = parse.GetValue(JobFilter),
                Notify = !parse.GetValue(NoNotify),
                RunDeadline = Duration(parse.GetValue(RunDeadline)),
            },
            parse.GetValue(GlobalOptions.ConfigDir)?.FullName,
            parse.GetValue(StateFile)?.FullName,
            new RunLockOptions
            {
                Skip = parse.GetValue(SkipStateLock),
                Wait = parse.GetValue(WaitForStateLock),
                HeldExitCode = parse.GetValue(LockHeldExit),
            })));
        return run;
    }

    // ---- inspection -------------------------------------------------------------------

    private static Command BuildProbe()
    {
        var path = new Argument<string>("path") { Description = "The log file to probe." };
        var probe = new Command("probe", "Report which locked-file strategies a path actually supports.") { path };
        GlobalOptions.AddTo(probe, configDir: false);  // probe takes the path it probes as an argument
        probe.SetAction(parse => CommandContext.Guarded(parse, ctx => ProbeCommand.Run(ctx, parse.GetRequiredValue(path))));
        return probe;
    }

    private static Command BuildGlob()
    {
        var pattern = new Argument<string>("pattern") { Description = "The glob pattern to resolve." };
        var glob = new Command("glob",
            "Resolve a pattern and show what it matches, or why it was refused. Use this before trusting a pattern in a job.")
        { pattern };
        GlobalOptions.AddTo(glob, configDir: false);  // glob tests one pattern and builds its own guard
        glob.SetAction(parse => CommandContext.Guarded(parse, ctx => GlobCommand.Run(ctx, parse.GetRequiredValue(pattern))));
        return glob;
    }

    private static Command BuildConfig()
    {
        var check = new Command("check", "Validate the configuration and report problems with file, line and column.");
        GlobalOptions.AddTo(check);
        check.SetAction(parse => CommandContext.Guarded(parse, ctx => ConfigCommand.Check(ctx, parse.GetValue(GlobalOptions.ConfigDir)?.FullName)));

        var show = new Command("show", "Print the effective configuration, with every default resolved.");
        GlobalOptions.AddTo(show);
        show.SetAction(parse => CommandContext.Guarded(parse, ctx => ConfigCommand.Show(ctx, parse.GetValue(GlobalOptions.ConfigDir)?.FullName)));

        return new Command("config", "Inspect and validate the configuration.") { check, show };
    }

    private static Command BuildJournal()
    {
        var since = new Option<string?>("--since") { Description = "Only entries at or after this time." };
        var job = new Option<string?>("--job") { Description = "Only this job." };
        var all = new Option<bool>("--all")
        {
            Description = "Show the journal as it was written - both halves of every operation.",
        };
        var journal = new Command("journal", "Read the record of everything that was compressed, moved or deleted.") { since, job, all };
        GlobalOptions.AddTo(journal);
        journal.SetAction(parse => CommandContext.Guarded(parse, ctx => JournalCommand.Run(
            ctx,
            parse.GetValue(since),
            parse.GetValue(job),
            parse.GetValue(GlobalOptions.ConfigDir)?.FullName,
            parse.GetValue(all))));
        return journal;
    }

    private static Command BuildDoctor()
    {
        var doctor = new Command("doctor",
            "Report every path, the conf.d ACL verdict, elevation state, long-path support and which run host is registered.");
        GlobalOptions.AddTo(doctor);
        doctor.SetAction(parse => CommandContext.Guarded(parse, ctx => DoctorCommand.Run(ctx, parse.GetValue(GlobalOptions.ConfigDir)?.FullName)));
        return doctor;
    }

    // ---- notifications ------------------------------------------------------------------

    /// <summary>
    /// Inspecting notification configuration and history.
    /// </summary>
    /// <remarks>
    /// Read-only apart from reset. Without these, "why did I not get an email?" has no answer
    /// short of reading the source - and a notification feature nobody can interrogate is one
    /// nobody trusts.
    /// </remarks>
    private static Command BuildNotify()
    {
        var show = new Command("show", "Print the notification configuration: targets, providers, proxy and certificate pinning.");
        GlobalOptions.AddTo(show);
        show.SetAction(parse => CommandContext.Guarded(parse, ctx => NotifyCommand.Show(
            ctx, parse.GetValue(GlobalOptions.ConfigDir)?.FullName)));

        var status = new Command("status", "Report what was last said about each job, and which channels are suppressed.");
        GlobalOptions.AddTo(status);
        status.SetAction(parse => CommandContext.Guarded(parse, ctx => NotifyCommand.Status(
            ctx, parse.GetValue(GlobalOptions.ConfigDir)?.FullName)));

        var channel = new Argument<string?>("channel")
        {
            Description = "The channel to close, as notify status names it. Omit for all of them.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var reset = new Command("reset", "Close a suppressed channel, for when the thing it could not reach is fixed.") { channel };
        GlobalOptions.AddTo(reset);
        reset.SetAction(parse => CommandContext.Guarded(parse, ctx => NotifyCommand.Reset(
            ctx, parse.GetValue(channel),
            parse.GetValue(GlobalOptions.ConfigDir)?.FullName)));

        var only = new Argument<string?>("target")
        {
            Description = "Only channels whose name contains this. Omit for all of them.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var test = new Command("test", "Send a real test message to every configured channel. Records nothing.") { only };
        GlobalOptions.AddTo(test);
        test.SetAction(parse => CommandContext.Guarded(parse, ctx => NotifyTestCommand.Run(
            ctx, parse.GetValue(only),
            parse.GetValue(GlobalOptions.ConfigDir)?.FullName)));

        var setSecretProvider = new Argument<string>("provider")
        {
            Description = "The provider to give a credential to, as notify show names it: email.relay.",
        };
        var setSecretField = new Argument<string>("field")
        {
            Description = "Which credential: password, token, user_key or url.",
        };

        var setSecret = new Command("set-secret",
            "Store a provider's credential and point the configuration at it, in one step.")
        {
            setSecretProvider, setSecretField, FromPipe,
        };
        GlobalOptions.AddTo(setSecret);
        setSecret.SetAction(parse => CommandContext.Guarded(parse, ctx => SecretCommand.SetForProvider(
            ctx, Input(parse, new ConsoleInputSource()),
            SecretPlatform.ForThisMachine(),
            parse.GetRequiredValue(setSecretProvider), parse.GetRequiredValue(setSecretField),
            parse.GetValue(GlobalOptions.ConfigDir)?.FullName)));

        return new Command("notify", "Inspect how failures are reported, and to whom.")
        {
            show, status, test, setSecret, reset,
        };
    }

    // ---- secrets ----------------------------------------------------------------------

    /// <summary>
    /// The stored-credential verbs.
    /// </summary>
    /// <remarks>
    /// Not one of them takes the value as an option, and that is the point. A command line is
    /// readable by every local administrator through Win32_Process, is written verbatim into
    /// 4688 audit events wherever command-line auditing is on, and is captured by essentially
    /// every EDR agent. Values arrive on standard input, which none of those observe.
    /// </remarks>
    private static Command BuildSecret()
    {
        var platform = SecretPlatform.ForThisMachine();
        IInputSource input = new ConsoleInputSource();

        var name = new Argument<string>("name") { Description = "The name a config file refers to as @secret:NAME." };
        var fromFile = new Option<FileInfo?>("--from-file")
        {
            Description = "Read the value from this file instead of standard input. For a value "
                        + "your shell cannot pipe cleanly - a non-ASCII password, most often.",
        };

        var set = new Command("set", "Store a secret, reading its value from standard input.") { name, fromFile, FromPipe };
        GlobalOptions.AddTo(set);
        set.SetAction(parse => CommandContext.Guarded(parse, ctx => SecretCommand.Set(
            ctx, Input(parse, input), platform,
            parse.GetRequiredValue(name), parse.GetValue(fromFile),
            parse.GetValue(GlobalOptions.ConfigDir)?.FullName)));

        var list = new Command("list", "List stored secrets: names, when they were set and by whom. Never values.");
        GlobalOptions.AddTo(list);
        list.SetAction(parse => CommandContext.Guarded(parse, ctx => SecretCommand.List(
            ctx, platform, parse.GetValue(GlobalOptions.ConfigDir)?.FullName)));

        var removeName = new Argument<string>("name") { Description = "The secret to remove." };
        var remove = new Command("remove", "Delete a stored secret.") { removeName };
        GlobalOptions.AddTo(remove);
        remove.SetAction(parse => CommandContext.Guarded(parse, ctx => SecretCommand.Remove(
            ctx, platform, parse.GetRequiredValue(removeName),
            parse.GetValue(GlobalOptions.ConfigDir)?.FullName)));

        var testName = new Argument<string>("name") { Description = "The secret to decrypt." };
        var test = new Command("test", "Confirm a secret decrypts on this machine, reporting its length and nothing else.") { testName };
        GlobalOptions.AddTo(test);
        test.SetAction(parse => CommandContext.Guarded(parse, ctx => SecretCommand.Test(
            ctx, platform, parse.GetRequiredValue(testName),
            parse.GetValue(GlobalOptions.ConfigDir)?.FullName)));

        var importFile = new Option<FileInfo?>("--from-file")
        {
            Description = "Read name=value lines from this file instead of standard input.",
        };
        var import = new Command("import", "Store several secrets from name=value lines, for unattended rollout.") { importFile };
        GlobalOptions.AddTo(import);
        import.SetAction(parse => CommandContext.Guarded(parse, ctx => SecretCommand.Import(
            ctx, input, platform, parse.GetValue(importFile),
            parse.GetValue(GlobalOptions.ConfigDir)?.FullName)));

        return new Command("secret", "Store the credentials that notification providers authenticate with.")
        {
            set, list, remove, test, import,
        };
    }

    // ---- scheduling -------------------------------------------------------------------

    private static Command BuildHost()
    {
        var kind = new Argument<string>("kind") { Description = "task, service, or none." };
        var use = new Command("use", "Choose what runs rotations, switching freely from whatever is registered now.") { kind };
        GlobalOptions.AddTo(use);
        use.SetAction(parse => CommandContext.Guarded(parse, ctx => HostCommand.Use(ctx, parse.GetRequiredValue(kind), parse.GetValue(GlobalOptions.ConfigDir)?.FullName)));

        var status = new Command("status", "Report the configured run host, what is actually registered, and any drift between them.");
        GlobalOptions.AddTo(status);
        status.SetAction(parse => CommandContext.Guarded(parse, ctx => HostCommand.Status(ctx, parse.GetValue(GlobalOptions.ConfigDir)?.FullName)));

        var acl = new Option<bool>("--acl") { Description = "Re-apply the hardened ACL to the configuration directory." };
        var repair = new Command("repair", "Put the run host and directory permissions back the way the installer left them.") { acl };
        GlobalOptions.AddTo(repair);
        repair.SetAction(parse => CommandContext.Guarded(parse, ctx => HostCommand.Repair(ctx, parse.GetValue(acl), parse.GetValue(GlobalOptions.ConfigDir)?.FullName)));

        var forDuration = new Option<string?>("--for") { Description = "How long to pause, e.g. 01:00:00." };
        var pause = new Command("pause", "Suspend rotations without unregistering the run host.") { forDuration };
        GlobalOptions.AddTo(pause);
        pause.SetAction(parse => CommandContext.Guarded(parse, ctx => PauseCommand.Run(ctx, parse.GetValue(forDuration), parse.GetValue(GlobalOptions.ConfigDir)?.FullName)));

        var exportTask = new Command("export-task", "Print the Scheduled Task XML, for deployment by GPO or DSC.");
        GlobalOptions.AddTo(exportTask);
        exportTask.SetAction(parse => CommandContext.Guarded(parse, ctx => ExportTaskCommand.Run(ctx, parse.GetValue(GlobalOptions.ConfigDir)?.FullName)));

        // Called by the installer rather than having NSIS edit PATH itself: stock makensis is
        // built with NSIS_MAX_STRLEN=1024 and silently truncates a longer PATH, and writing
        // that back destroys it for every program on the machine.
        // The installer passes --machine only for an all-users install. Without it the user's
        // own PATH is edited, which is what a per-user install must do even when the person
        // running it happens to be an administrator.
        var machineScope = new Option<bool>("--machine")
        {
            Description = "Edit the machine PATH rather than this user's. Needs administrator.",
        };

        var pathAdd = new Command("path-add", "Add the install directory to PATH.") { machineScope };
        GlobalOptions.AddTo(pathAdd, configDir: false);  // path-add edits PATH, not configuration
        pathAdd.SetAction(parse => CommandContext.Guarded(parse, ctx => HostCommand.Path(
            ctx, add: true, machine: parse.GetValue(machineScope))));

        var pathRemove = new Command("path-remove", "Remove the install directory from PATH.") { machineScope };
        GlobalOptions.AddTo(pathRemove, configDir: false);  // path-remove edits PATH, not configuration
        pathRemove.SetAction(parse => CommandContext.Guarded(parse, ctx => HostCommand.Path(
            ctx, add: false, machine: parse.GetValue(machineScope))));

        return new Command("host", "Manage what runs rotations: a Scheduled Task, a Windows Service, or nothing.")
        {
            use, status, repair, pause, exportTask, pathAdd, pathRemove,
        };
    }

    // ---- onboarding -------------------------------------------------------------------

    private static Command BuildImport()
    {
        var source = new Argument<string>("path") { Description = "A Linux logrotate.conf or logrotate.d file." };
        var outDir = new Option<DirectoryInfo?>("--out") { Description = "Where to write the converted job files." };
        var import = new Command("import", "Convert a Linux logrotate configuration. One way: what cannot be translated is commented, not guessed.") { source, outDir };
        GlobalOptions.AddTo(import);
        import.SetAction(parse => CommandContext.Guarded(parse, ctx => ImportCommand.Run(ctx, parse.GetRequiredValue(source), parse.GetValue(outDir)?.FullName, parse.GetValue(GlobalOptions.ConfigDir)?.FullName)));
        return import;
    }

    private static Command BuildScan()
    {
        var scan = new Command("scan", "Find log producers on this machine and say which ones rotate but never delete.");
        GlobalOptions.AddTo(scan, configDir: false);  // scan looks at the machine, not at a configuration
        scan.SetAction(parse => CommandContext.Guarded(parse, ctx => ScanCommand.Run(ctx)));
        return scan;
    }

    private static Command BuildUpdate()
    {
        var check = new Command("check", "Report whether a newer release exists. Makes no changes.");
        GlobalOptions.AddTo(check, configDir: false);  // update check asks GitHub for a version
        check.SetAction(parse => CommandContext.Guarded(parse, ctx => UpdateCommand.CheckAsync(ctx).GetAwaiter().GetResult()));

        // No --yes, and the description says what the verb does rather than what it is named
        // after. Apply deliberately does not self-replace - see UpdateCommand.Apply - so a switch
        // offering to "install without asking" was attached to a verb that installs nothing.
        var apply = new Command("apply", "Explain how to install the newest release.");
        GlobalOptions.AddTo(apply, configDir: false);  // update apply prints where to download
        apply.SetAction(parse => CommandContext.Guarded(parse, ctx => UpdateCommand.Apply(ctx)));

        return new Command("update", "Check for newer releases, and say how to install one.") { check, apply };
    }
}
