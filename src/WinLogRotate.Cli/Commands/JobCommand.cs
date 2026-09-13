using WinLogRotate.Cli.Output;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Safety;
using WinLogRotate.Hosting.Security;

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
                Remedy = "Run this from an elevated prompt, or use the Jobs page in the GUI.",
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
