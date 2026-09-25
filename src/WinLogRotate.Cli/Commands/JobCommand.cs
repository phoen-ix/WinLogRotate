using WinLogRotate.Cli.Output;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Safety;

namespace WinLogRotate.Cli.Commands;

/// <summary>
/// Creating and editing jobs, which until now could only be done in Notepad.
/// </summary>
/// <remarks>
/// <para>
/// <c>ConfigLoader</c> has told operators to "use the GUI to add one" since the first milestone,
/// and there was no such capability - nor a command-line one. These verbs are the capability, and
/// the GUI's job editor is a form over them rather than a second implementation.
/// </para>
/// <para>
/// Every verb here goes through <see cref="JobDocument.Apply"/>, which edits the file in place
/// key by key. <c>--dry-run</c> is the same call that stops before <c>Save</c>, so what a form
/// validates and what it saves cannot come apart.
/// </para>
/// </remarks>
internal static class JobCommand
{
    /// <summary>
    /// What decides whether this process may write. A field so it can be asserted, and injected
    /// at the call sites only by tests - the same shape, and for the same reason, as
    /// <see cref="ConfigWrites.Owner"/>.
    /// </summary>
    internal static readonly Func<bool> Elevated = Privilege.IsElevated;

    public static int Add(
        CommandContext ctx, string name, string? configDir, IReadOnlyList<JobEdit> edits,
        bool dryRun, Func<bool>? elevated = null)
    {
        if (Refuse(ctx, "job add", name, edits, dryRun, elevated) is { } refused)
        {
            return refused;
        }

        var (paths, guard, index) = Open(configDir);

        if (index.FileFor(name) is { } taken)
        {
            return Refusals.CannotUse<JobEditResult>(
                ctx, "job add", name, "a name that is free",
                $"A job called '{name}' is already defined in {Path.GetFileName(taken)}. "
                + "Edit that one with 'winlogrotate job set', or choose another name.");
        }

        var path = Path.Combine(paths.ConfigDirectory, JobFiles.NameFor(name));

        if (File.Exists(path))
        {
            // The name is free but the filename is taken, which happens when two names sanitise
            // to one file - "IIS: W3SVC1" and "iis w3svc1". Refused rather than resolved with a
            // suffix: a file whose name does not match the job in it is how a conf.d becomes
            // unreadable to the person maintaining it.
            return Refusals.CannotUse<JobEditResult>(
                ctx, "job add", name, "a name that does not collide",
                $"{Path.GetFileName(path)} already exists. Choose a name that differs by more "
                + "than punctuation, or edit that file directly.");
        }

        // The name is written like any other key, so there is one apply path and not two.
        var proposal = JobDocument.Apply(
            JobDocument.Skeleton(path),
            path,
            [new JobEdit("name", name), .. edits],
            index.Defaults,
            guard,
            index.NamesInUse(excludingFile: null));

        return Finish(ctx, "job add", name, path, proposal, dryRun, isNew: true);
    }

    /// <summary>
    /// Changes keys on a job that already exists, in the file it already lives in.
    /// </summary>
    /// <remarks>
    /// The same apply path as <see cref="Add"/> over a file somebody else wrote, which is the
    /// case that matters: a file this product created will always round-trip, and the hazard is
    /// the hand-written one with a comment in it, an unknown key, and an <c>allowdangerous</c>
    /// entry somebody argued for.
    /// </remarks>
    public static int Set(
        CommandContext ctx, string name, string? configDir, IReadOnlyList<JobEdit> edits,
        bool dryRun, Func<bool>? elevated = null)
    {
        if (Refuse(ctx, "job set", name, edits, dryRun, elevated) is { } refused)
        {
            return refused;
        }

        var (_, guard, index) = Open(configDir);

        if (Find(ctx, "job set", name, index) is not { } path)
        {
            return ExitCode.ConfigInvalid;
        }

        if (edits.Count == 0)
        {
            return Refusals.CannotUse<JobEditResult>(
                ctx, "job set", name, "a job with anything to change",
                "Name at least one key: --set rotate=14, or --unset rotate to inherit it again.");
        }

        TomlFile file;

        try
        {
            file = TomlFile.Load(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Error,
                Code = DiagnosticCode.ConfigUnreadable,
                Message = $"{path} could not be read: {e.Message}",
                Path = path,
                Remedy = "Check the permissions on the conf.d directory.",
            });

            return ctx.Output.Complete<JobEditResult>("job set", ExitCode.Errors, null);
        }

        // Its own file excluded, or every edit would be refused for colliding with the job
        // being edited.
        var proposal = JobDocument.Apply(
            file, path, edits, index.Defaults, guard, index.NamesInUse(excludingFile: path));

        return Finish(ctx, "job set", name, path, proposal, dryRun, isNew: false);
    }

    /// <summary>
    /// Switches a job off, or back on, without touching anything else in its file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>enable</c> removes the key rather than writing <c>enabled = true</c>, and that is what
    /// makes it a byte-exact undo of <c>disable</c>: <c>enabled</c> is a per-job key with no
    /// <c>[defaults]</c> layer beneath it, so absent and true are the same state. Writing the
    /// second spelling of a state the file already had would leave a line behind after every
    /// off-and-on, and a job's file would slowly accumulate the history of somebody's afternoon.
    /// </para>
    /// <para>
    /// This is also the verb <c>remove</c>'s refusal points at. Deleting a job destroys its
    /// configuration; disabling it is the reversible one, and it has to actually be reversible
    /// for that to be honest advice.
    /// </para>
    /// </remarks>
    public static int Switch(
        CommandContext ctx, string name, string? configDir, bool on, bool dryRun,
        Func<bool>? elevated = null)
    {
        var verb = on ? "job enable" : "job disable";

        if (Refuse(ctx, verb, name, [], dryRun, elevated) is { } refused)
        {
            return refused;
        }

        var (_, guard, index) = Open(configDir);

        if (Find(ctx, verb, name, index) is not { } path)
        {
            return ExitCode.ConfigInvalid;
        }

        var proposal = JobDocument.Apply(
            TomlFile.Load(path),
            path,
            [new JobEdit("enabled", on ? null : "false")],
            index.Defaults,
            guard,
            index.NamesInUse(excludingFile: path));

        return Finish(ctx, verb, name, path, proposal, dryRun, isNew: false);
    }

    /// <summary>
    /// Deletes a job's file, having said what it held.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It deletes rather than renaming aside. A kept-aside <c>iis.toml.removed</c> renamed back
    /// beside a live job of the same name is a duplicate name, and a duplicate name is not
    /// file-scoped: it stops every rotation on the machine. Keeping a copy would trade a loss the
    /// operator chose for an outage they did not.
    /// </para>
    /// <para>
    /// So the output says what was in the file before it goes, and names <c>job disable</c> -
    /// which is the reversible one, and is reversible byte for byte.
    /// </para>
    /// </remarks>
    public static int Remove(
        CommandContext ctx, string name, string? configDir, bool dryRun, Func<bool>? elevated = null)
    {
        if (Refuse(ctx, "job remove", name, [], dryRun, elevated) is { } refused)
        {
            return refused;
        }

        var (_, _, index) = Open(configDir);

        if (Find(ctx, "job remove", name, index) is not { } path)
        {
            return ExitCode.ConfigInvalid;
        }

        // Read before deleting, so the record of what was lost is the file's own and not a
        // reconstruction. Every key is reported as unset, because that is what removal does to
        // all of them at once.
        var held = Held(path);

        if (dryRun)
        {
            ctx.Output.Line($"{name} would be removed, and {Path.GetFileName(path)} deleted.");
            Recite(ctx, held);
            ctx.Output.Line("Nothing was removed. Run without --dry-run to do it, or 'job disable' to keep the file.");

            return Complete(ctx, "job remove", name, path, held, written: false, ExitCode.Ok);
        }

        try
        {
            ConfigWrites.RemoveJob(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Error,
                Code = DiagnosticCode.ConfigUnwritable,
                Message = $"{path} could not be deleted: {e.Message}",
                Path = path,
                Remedy = "Check the permissions on the conf.d directory, and that nothing has the file open.",
            });

            return Complete(ctx, "job remove", name, path, held, written: false, ExitCode.Errors);
        }

        ctx.Output.Line($"{name} removed. {Path.GetFileName(path)} is gone.");
        Recite(ctx, held);
        ctx.Output.Line("'winlogrotate job disable' keeps the file next time.");

        return Complete(ctx, "job remove", name, path, held, written: true, ExitCode.Ok);
    }

    /// <summary>What a job file said, as the changes deleting it amounts to.</summary>
    private static JobProposal Held(string path)
    {
        var file = TomlFile.Load(path);

        return new JobProposal
        {
            File = file,
            Problems = [],
            Changes = [.. JobSchema.Keys
                .Where(k => TomlEditor.TryRead(file, JobDocument.Table, k.Key, out _, out _))
                .Select(k =>
                {
                    TomlEditor.TryRead(file, JobDocument.Table, k.Key, out var was, out _);
                    return new JobChange { Key = k.Key, Kind = JobChangeKind.Unset, Before = was };
                })],
        };
    }

    private static void Recite(CommandContext ctx, JobProposal held)
    {
        foreach (var change in held.Changes)
        {
            ctx.Output.Line($"    {change.Key} = {change.Before}");
        }
    }

    /// <summary>
    /// The file a named job lives in, or a refusal that says which names exist.
    /// </summary>
    /// <remarks>
    /// Listing them is not decoration. The name in the file is the job's name and the filename is
    /// only where it lives, so somebody who guessed from <c>dir conf.d</c> has guessed wrong and
    /// has no other way to find out.
    /// </remarks>
    private static string? Find(CommandContext ctx, string verb, string name, JobIndex index)
    {
        if (index.FileFor(name) is { } path)
        {
            return path;
        }

        var known = index.Entries.Select(e => e.Name).Order(StringComparer.OrdinalIgnoreCase).ToArray();

        Refusals.CannotUse<JobEditResult>(
            ctx, verb, name, "a job in this configuration",
            known.Length == 0
                ? "There are no jobs yet. Create one with 'winlogrotate job add'."
                : $"The jobs here are: {string.Join(", ", known)}.");

        return null;
    }

    /// <summary>
    /// Prints a job as its file writes it, and never as a run would resolve it.
    /// </summary>
    /// <remarks>
    /// The one verb here that needs no elevation, because it writes nothing. That matters for the
    /// GUI: opening a job to look at it must not raise a UAC prompt.
    /// </remarks>
    public static int Show(CommandContext ctx, string name, string? configDir)
    {
        var (_, guard, index) = Open(configDir);

        if (Find(ctx, "job show", name, index) is not { } path)
        {
            return ExitCode.ConfigInvalid;
        }

        TomlFile file;

        try
        {
            file = TomlFile.Load(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Error,
                Code = DiagnosticCode.ConfigUnreadable,
                Message = $"{path} could not be read: {e.Message}",
                Path = path,
                Remedy = "Check the permissions on the conf.d directory.",
            });

            return ctx.Output.Complete<JobShowResult>("job show", ExitCode.Errors, null);
        }

        var keys = TomlEditor.Keys(file, JobDocument.Table)
            .Select(k => new JobKeyLineDto
            {
                Key = k.Key,
                Source = k.Source,
                Line = k.Line,
                Known = JobSchema.Find(k.Key) is not null,
            })
            .ToArray();

        // Judged as well as read, because the job an editor opens is often the one that will not
        // load - and a verdict is the reason they opened it.
        var verdict = ConfigLoader.Judge(
            file, path, index.Defaults, guard, index.NamesInUse(excludingFile: path));

        ctx.Output.Line($"{name}  {path}");

        foreach (var key in keys)
        {
            ctx.Output.Line(key.Known
                ? $"    {key.Key} = {key.Source}"
                : $"    {key.Key} = {key.Source}   # not a setting this product reads");
        }

        foreach (var d in verdict.Diagnostics)
        {
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = d.Severity,
                Code = d.Code,
                Message = d.Message,
                Path = d.File,
                Line = d.Line == 0 ? null : d.Line,
                Column = d.Column == 0 ? null : d.Column,
                Remedy = d.Remedy,
            });
        }

        return ctx.Output.Complete("job show", verdict.HasErrors ? ExitCode.ConfigInvalid : ExitCode.Ok,
            new JobShowResult
            {
                Job = name,
                Path = path,
                Keys = keys,
                Diagnostics = verdict.Diagnostics.Select(ConfigCommand.Map).ToArray(),
            });
    }

    /// <summary>
    /// Everything a job verb needs before it can judge anything.
    /// </summary>
    /// <remarks>
    /// The guard carries <see cref="OverrideSupport.ForThisMachine"/>. A bare
    /// <c>new PathGuard(new GuardOptions())</c> honours no <c>allowdangerous</c> entry, so it
    /// would refuse a path the operator has already unlocked - and an editor that refuses what
    /// the engine accepts is worse than one that refuses nothing.
    /// </remarks>
    private static (InstallPaths Paths, PathGuard Guard, JobIndex Index) Open(string? configDir)
    {
        var paths = InstallPaths.Resolve(configDir);
        var guard = new PathGuard(new GuardOptions { Overrides = OverrideSupport.ForThisMachine(paths) });
        return (paths, guard, JobIndex.Of(paths));
    }

    /// <summary>
    /// The two things that stop a job verb before it has read anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Elevation first, and explicitly, in the shape <c>host use</c> uses. <c>import</c> does not
    /// check, and an unelevated write there escapes as an <c>UnauthorizedAccessException</c> that
    /// <c>CommandContext.Guarded</c> reports as <c>LR1006</c>, exit 4 - "This is a defect. Nothing
    /// about what was or was not done can be relied on" - about a machine that is working
    /// correctly and refusing correctly. It is also the one code the GUI turns into a modal.
    /// </para>
    /// <para>
    /// But not under <c>--dry-run</c>, which writes nothing and is the GUI's validate call. A
    /// form that had to raise a UAC prompt to tell you a field was wrong would ask on every blur,
    /// and people who are asked that often stop reading what they are agreeing to.
    /// </para>
    /// <para>
    /// And <c>name</c> is refused from <c>--set</c>. Renaming a job is not a key edit: the file is
    /// named after the job, the journal is keyed by the name, and half a rename is a job that
    /// silently stops being the job whose history anybody has.
    /// </para>
    /// </remarks>
    private static int? Refuse(
        CommandContext ctx, string verb, string name, IReadOnlyList<JobEdit> edits,
        bool dryRun, Func<bool>? elevated)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Refusals.CannotUse<JobEditResult>(
                ctx, verb, name, "a job name", "Give the job a name: winlogrotate job add iis --paths ...");
        }

        if (edits.FirstOrDefault(e => string.Equals(e.Key, "name", StringComparison.OrdinalIgnoreCase))
            is { Key: not null })
        {
            return Refusals.CannotUse<JobEditResult>(
                ctx, verb, "name", "a key this verb will change",
                "A job's name is its identity: the file is named after it and the journal is keyed "
                + "by it. Add a job under the new name and remove the old one.");
        }

        if (!dryRun && !(elevated ?? Elevated)())
        {
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Error,
                Code = DiagnosticCode.NeedsAdministrator,
                Message = "Changing the configuration needs administrator rights.",
                // Deliberately not "or use the GUI": the Jobs page cannot create or change a
                // job yet, and pointing somebody at a capability that is not there is the whole
                // defect this milestone exists to fix. It will say so when it is true.
                Remedy = "Run this from an elevated prompt. Add --dry-run to check the change "
                    + "without writing it, which needs no rights at all.",
            });

            return ctx.Output.Complete<JobEditResult>(verb, ExitCode.Errors, null);
        }

        return null;
    }

    /// <summary>Reports a judged proposal, and writes it if it is allowed to be written.</summary>
    private static int Finish(
        CommandContext ctx, string verb, string name, string path,
        JobProposal proposal, bool dryRun, bool isNew)
    {
        foreach (var problem in proposal.Problems)
        {
            Refusals.Unusable(ctx, problem.Message, problem.Remedy);
        }

        foreach (var d in proposal.Verdict?.Diagnostics ?? [])
        {
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = d.Severity,
                Code = d.Code,
                Message = d.Message,
                Path = d.File,
                Line = d.Line == 0 ? null : d.Line,
                Column = d.Column == 0 ? null : d.Column,
                Remedy = d.Remedy,
            });
        }

        if (!proposal.Writable)
        {
            ctx.Output.Line($"{name} was not written. Nothing has changed.");
            return Complete(ctx, verb, name, path, proposal, written: false, ExitCode.ConfigInvalid);
        }

        if (dryRun)
        {
            ctx.Output.Line(proposal.Changed
                ? $"{name} would be valid. {Count(proposal)} to write; nothing was."
                : $"{name} is already what you asked for. Nothing to change.");

            return Complete(ctx, verb, name, path, proposal, written: false, ExitCode.Ok);
        }

        // An existing file that already says all of it is left alone entirely, rather than
        // rewritten to identical bytes. Rewriting would change the file's timestamp and its
        // descriptor for no reason, and a backup tool would report a change that is not one.
        if (!proposal.Changed && !isNew)
        {
            ctx.Output.Line($"{name} is already what you asked for. Nothing to change.");
            return Complete(ctx, verb, name, path, proposal, written: false, ExitCode.Ok);
        }

        try
        {
            ConfigWrites.Job(path, proposal.File.ToString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Error,
                Code = DiagnosticCode.ConfigUnwritable,
                Message = $"{path} could not be written: {e.Message}",
                Path = path,
                Remedy = "Check free space and the permissions on the conf.d directory.",
            });

            // Exit 1 rather than 2: the proposal was valid and the write failed, which is a
            // different thing for a caller to handle than a configuration it has to fix.
            return Complete(ctx, verb, name, path, proposal, written: false, ExitCode.Errors);
        }

        ctx.Output.Line($"{name}: {Count(proposal)} in {Path.GetFileName(path)}.");

        foreach (var change in proposal.Changes.Where(c => c.Kind != JobChangeKind.Unchanged))
        {
            ctx.Output.Line(change.Kind == JobChangeKind.Unset
                ? $"    unset     {change.Key} (was {change.Before}); it inherits again"
                : $"    {change.Key} = {change.After}");
        }

        return Complete(ctx, verb, name, path, proposal, written: true, ExitCode.Ok);
    }

    private static string Count(JobProposal proposal)
    {
        var n = proposal.Changes.Count(c => c.Kind != JobChangeKind.Unchanged);
        return n == 1 ? "1 change" : $"{n} changes";
    }

    private static int Complete(
        CommandContext ctx, string verb, string name, string path,
        JobProposal proposal, bool written, int exit) =>
        ctx.Output.Complete(verb, exit, new JobEditResult
        {
            Verb = verb["job ".Length..],
            Job = name,
            Path = path,
            Written = written,
            Changes = proposal.Changes.Select(c => new JobChangeDto
            {
                Key = c.Key,
                Kind = c.Kind.ToString(),
                Before = c.Before,
                After = c.After,
            }).ToArray(),
            Diagnostics = (proposal.Verdict?.Diagnostics ?? []).Select(ConfigCommand.Map).ToArray(),
        });
}
