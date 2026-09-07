using System.CommandLine;
using WinLogRotate.Cli.Output;
using WinLogRotate.Core;
using WinLogRotate.Core.Engine;

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
        };

        GlobalOptions.AddTo(root);

        // Replace the built-in version option rather than adding beside it. Its action runs
        // first and prints bare text, so `--version --json` would emit no envelope at all -
        // and that envelope is exactly what the GUI reads on start to check it is talking to
        // a CLI whose contract schema it understands.
        if (root.Options.FirstOrDefault(o => o is VersionOption) is { } builtIn)
        {
            root.Options.Remove(builtIn);
        }

        var version = new Option<bool>("--version", "-V")
        {
            Description = "Print the version and exit.",
        };
        root.Options.Add(version);

        root.SetAction(parse => parse.GetValue(version)
            ? VersionCommand.Run(CommandContext.From(parse))
            : ShowBanner(CommandContext.From(parse)));

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

    private static readonly Option<int> LockHeldExit =
        new("--lock-held-exit") { Description = "Exit code to use when another run holds the lock. The registered scheduled task passes 0, because Task Scheduler renders 3 as 0x3 and an admin reads that as a failure.", DefaultValueFactory = _ => ExitCode.LockHeld };

    private static Command BuildRun()
    {
        var run = new Command("run", "Rotate everything that is due.")
        {
            DryRun, Force, Catchup, JobFilter, StateFile,
            SkipStateLock, WaitForStateLock, LockHeldExit,
        };
        GlobalOptions.AddTo(run);
        run.SetAction(parse => RunCommand.Run(
            CommandContext.From(parse),
            new RunOptions
            {
                DryRun = parse.GetValue(DryRun),
                Force = parse.GetValue(Force),
                Catchup = parse.GetValue(Catchup),
                OnlyJob = parse.GetValue(JobFilter),
            },
            parse.GetValue(GlobalOptions.ConfigDir)?.FullName,
            parse.GetValue(StateFile)?.FullName));
        return run;
    }

    // ---- inspection -------------------------------------------------------------------

    private static Command BuildProbe()
    {
        var path = new Argument<string>("path") { Description = "The log file to probe." };
        var probe = new Command("probe", "Report which locked-file strategies a path actually supports.") { path };
        GlobalOptions.AddTo(probe);
        probe.SetAction(parse => NotYet.Run(CommandContext.From(parse), "probe", milestone: 5));
        return probe;
    }

    private static Command BuildGlob()
    {
        var pattern = new Argument<string>("pattern") { Description = "The glob pattern to resolve." };
        var glob = new Command("glob",
            "Resolve a pattern and show what it matches, or why it was refused. Use this before trusting a pattern in a job.")
        { pattern };
        GlobalOptions.AddTo(glob);
        glob.SetAction(parse => GlobCommand.Run(CommandContext.From(parse), parse.GetRequiredValue(pattern)));
        return glob;
    }

    private static Command BuildConfig()
    {
        var check = new Command("check", "Validate the configuration and report problems with file, line and column.");
        GlobalOptions.AddTo(check);
        check.SetAction(parse => ConfigCommand.Check(CommandContext.From(parse), parse.GetValue(GlobalOptions.ConfigDir)?.FullName));

        var show = new Command("show", "Print the effective configuration, with every default resolved.");
        GlobalOptions.AddTo(show);
        show.SetAction(parse => ConfigCommand.Show(CommandContext.From(parse), parse.GetValue(GlobalOptions.ConfigDir)?.FullName));

        return new Command("config", "Inspect and validate the configuration.") { check, show };
    }

    private static Command BuildJournal()
    {
        var since = new Option<string?>("--since") { Description = "Only entries at or after this time." };
        var job = new Option<string?>("--job") { Description = "Only this job." };
        var journal = new Command("journal", "Read the record of everything that was compressed, moved or deleted.") { since, job };
        GlobalOptions.AddTo(journal);
        journal.SetAction(parse => JournalCommand.Run(
            CommandContext.From(parse),
            parse.GetValue(since),
            parse.GetValue(job),
            parse.GetValue(GlobalOptions.ConfigDir)?.FullName));
        return journal;
    }

    private static Command BuildDoctor()
    {
        var doctor = new Command("doctor",
            "Report every path, the conf.d ACL verdict, elevation state, long-path support and which run host is registered.");
        GlobalOptions.AddTo(doctor);
        doctor.SetAction(parse => DoctorCommand.Run(CommandContext.From(parse), parse.GetValue(GlobalOptions.ConfigDir)?.FullName));
        return doctor;
    }

    // ---- scheduling -------------------------------------------------------------------

    private static Command BuildHost()
    {
        var kind = new Argument<string>("kind") { Description = "task, service, or none." };
        var use = new Command("use", "Choose what runs rotations, switching freely from whatever is registered now.") { kind };
        GlobalOptions.AddTo(use);
        use.SetAction(parse => NotYet.Run(CommandContext.From(parse), "host use", milestone: 13));

        var status = new Command("status", "Report the configured run host, what is actually registered, and any drift between them.");
        GlobalOptions.AddTo(status);
        status.SetAction(parse => NotYet.Run(CommandContext.From(parse), "host status", milestone: 13));

        var acl = new Option<bool>("--acl") { Description = "Re-apply the hardened ACL to the configuration directory." };
        var repair = new Command("repair", "Put the run host and directory permissions back the way the installer left them.") { acl };
        GlobalOptions.AddTo(repair);
        repair.SetAction(parse => NotYet.Run(CommandContext.From(parse), "host repair", milestone: 10));

        var forDuration = new Option<string?>("--for") { Description = "How long to pause, e.g. 01:00:00." };
        var pause = new Command("pause", "Suspend rotations without unregistering the run host.") { forDuration };
        GlobalOptions.AddTo(pause);
        pause.SetAction(parse => NotYet.Run(CommandContext.From(parse), "host pause", milestone: 13));

        var exportTask = new Command("export-task", "Print the Scheduled Task XML, for deployment by GPO or DSC.");
        GlobalOptions.AddTo(exportTask);
        exportTask.SetAction(parse => NotYet.Run(CommandContext.From(parse), "host export-task", milestone: 13));

        return new Command("host", "Manage what runs rotations: a Scheduled Task, a Windows Service, or nothing.")
        {
            use, status, repair, pause, exportTask,
        };
    }

    // ---- onboarding -------------------------------------------------------------------

    private static Command BuildImport()
    {
        var source = new Argument<string>("path") { Description = "A Linux logrotate.conf or logrotate.d file." };
        var outDir = new Option<DirectoryInfo?>("--out") { Description = "Where to write the converted job files." };
        var probe = new Option<bool>("--probe") { Description = "Probe each path and pick a supported locked-file strategy, instead of leaving imported jobs disabled." };
        var import = new Command("import", "Convert a Linux logrotate configuration. One way: what cannot be translated is commented, not guessed.") { source, outDir, probe };
        GlobalOptions.AddTo(import);
        import.SetAction(parse => NotYet.Run(CommandContext.From(parse), "import", milestone: 20));
        return import;
    }

    private static Command BuildScan()
    {
        var deep = new Option<bool>("--deep") { Description = "Also search for log directories no producer claims." };
        var scan = new Command("scan", "Find log producers on this machine and say which ones rotate but never delete.") { deep };
        GlobalOptions.AddTo(scan);
        scan.SetAction(parse => NotYet.Run(CommandContext.From(parse), "scan", milestone: 20));
        return scan;
    }

    private static Command BuildUpdate()
    {
        var check = new Command("check", "Report whether a newer release exists. Makes no changes.");
        GlobalOptions.AddTo(check);
        check.SetAction(parse => NotYet.Run(CommandContext.From(parse), "update check", milestone: 21));

        var yes = new Option<bool>("--yes") { Description = "Install without asking." };
        var apply = new Command("apply", "Install the newest release.") { yes };
        GlobalOptions.AddTo(apply);
        apply.SetAction(parse => NotYet.Run(CommandContext.From(parse), "update apply", milestone: 21));

        return new Command("update", "Check for and install newer releases.") { check, apply };
    }
}
