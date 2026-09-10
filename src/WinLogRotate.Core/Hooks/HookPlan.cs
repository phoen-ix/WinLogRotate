using WinLogRotate.Contracts;
using WinLogRotate.Core.Notify;

namespace WinLogRotate.Core.Hooks;

/// <summary>What a job's hooks come to, once every rule has been applied.</summary>
public sealed record PlannedHooks
{
    public required IReadOnlyList<PlannedHook> Hooks { get; init; }

    /// <summary>Every hook that will not be run, and why. Always reported, never dropped.</summary>
    public required IReadOnlyList<CliDiagnostic> Refusals { get; init; }
}

/// <summary>
/// Turns a job's configured hook strings into things that will run, and refusals for the rest.
/// </summary>
/// <remarks>
/// <para>
/// Pure - no file system beyond an injected existence predicate, no process, no ACL - so every
/// refusal in this milestone is provable on the Linux test leg. That matters more here than
/// anywhere else in the product: these rules decide what executes as SYSTEM.
/// </para>
/// <para>
/// Every refusal is <see cref="DiagnosticCode.HookRefused"/>, whatever the cause. The distinction
/// the 9xxx band's comment is protecting is not "which rule said no" - the message says that - but
/// "did it run". A hook that ran and failed is <see cref="DiagnosticCode.HookFailed"/>, so an
/// alert rule can tell "somebody's configuration directory is loose" or "somebody typed
/// service:restart" from "the reload script returned 1".
/// </para>
/// </remarks>
public static class HookPlan
{
    /// <summary>The only service control code this sends. See <see cref="Verbs"/>.</summary>
    public const string ParamChange = "paramchange";

    /// <summary>
    /// Verbs a person might reasonably write, with the reason each of the others is refused.
    /// </summary>
    /// <remarks>
    /// <c>paramchange</c> is the only one the README promises, the only one that maps to what
    /// <c>postrotate</c> is for - logrotate's <c>kill -HUP</c>, "re-read your configuration" - and
    /// the only one that cannot cause an outage. A nightly rotation that stops IIS should have to
    /// say so in full, as a command line, rather than reading like a reload.
    /// </remarks>
    private static readonly string[] Verbs = ["stop", "start", "restart", "pause", "continue"];

    /// <param name="exists">
    /// Whether a path names a file, for splitting a command line. Injected so this runs anywhere.
    /// </param>
    public static PlannedHooks For(
        string jobName, HookStage stage, IReadOnlyList<string> raw, HookGate gate,
        Func<string, bool>? exists = null, string? sourceFile = null)
    {
        var hooks = new List<PlannedHook>();
        var refusals = new List<CliDiagnostic>();
        var probe = exists ?? File.Exists;
        var stageName = stage == HookStage.PreRotate ? "prerotate" : "postrotate";

        foreach (var entry in raw)
        {
            var parsed = HookParser.Parse(entry, sourceFile);

            if (!parsed.IsOk)
            {
                refusals.Add(Refuse(jobName, stageName,
                    parsed.Error == HookParseError.Empty
                        ? $"an empty {stageName} entry."
                        : $"'{parsed.Scheme}:' is not a hook scheme, so '{entry}' would never run.",
                    "Known hook schemes: command, service, event. A hook with no scheme is run as "
                    + "a command line."));
                continue;
            }

            var action = parsed.Action!;

            // Reporting schemes are refused here rather than quietly delivered. [notify] already
            // sends to http:, smtp:, pushover: and eventlog:, with a budget, a circuit breaker and
            // change detection; a copy of that reachable from postrotate would have none of them,
            // and the first anyone would learn of the difference is a run that hung on a webhook.
            if (!HookSchemes.NeedsHardenedConfDir(action.Scheme))
            {
                refusals.Add(Refuse(jobName, stageName,
                    $"{HookSchemes.Name(action.Scheme)}: reports, and a hook runs something.",
                    $"Put '{action.Display}' in [notify].to, where it is delivered with a time "
                    + "budget and a circuit breaker. Hooks are command:, service: and event:."));
                continue;
            }

            if (!gate.Allowed)
            {
                refusals.Add(Refuse(jobName, stageName,
                    $"'{action.Display}' runs code, and {gate.Reason}.", gate.Remedy));
                continue;
            }

            var planned = Check(jobName, stage, stageName, action, probe, refusals);
            if (planned is not null)
            {
                hooks.Add(planned);
            }
        }

        return new PlannedHooks { Hooks = hooks, Refusals = refusals };
    }

    /// <summary>Applies the per-scheme rules, or records why the hook will not run.</summary>
    private static PlannedHook? Check(
        string jobName, HookStage stage, string stageName, HookAction action,
        Func<string, bool> exists, List<CliDiagnostic> refusals)
    {
        switch (action.Scheme)
        {
            case HookScheme.Service:
                if (action.Target.Trim().Length == 0)
                {
                    refusals.Add(Refuse(jobName, stageName,
                        $"'{action.Display}' names no service.",
                        "Write service:paramchange:NAME, using the service's short name."));
                    return null;
                }

                // service:NAME with no verb means the default, and the default is the only one.
                if (action.Verb is { } verb && !verb.Equals(ParamChange, StringComparison.Ordinal))
                {
                    refusals.Add(Refuse(jobName, stageName,
                        Verbs.Contains(verb, StringComparer.Ordinal)
                            ? $"service:{verb}: is not sent by this tool - a rotation that stops or "
                              + "starts a service should say so in full."
                            : $"'{verb}' is not a service verb.",
                        $"Use service:paramchange:{action.Target}, which is the Windows equivalent "
                        + "of kill -HUP. To do anything else, write it as a command: hook, e.g. "
                        + $"command:C:\\Windows\\System32\\net.exe stop {action.Target}"));
                    return null;
                }

                return new PlannedHook { Action = action, Stage = stage, JobName = jobName };

            case HookScheme.Event:
                if (action.Target.Trim().Length == 0)
                {
                    refusals.Add(Refuse(jobName, stageName,
                        $"'{action.Display}' names no event.",
                        @"Write event:Global\YourEventName, matching the name the waiting program "
                        + "created."));
                    return null;
                }

                return new PlannedHook { Action = action, Stage = stage, JobName = jobName };

            case HookScheme.Command:
                if (!CommandLine.TrySplit(
                        action.Target, exists, out var program, out var arguments,
                        out var error, out var detail))
                {
                    refusals.Add(Refuse(jobName, stageName, detail!,
                        CommandLine.Remedy(error, action.Target)));
                    return null;
                }

                return new PlannedHook
                {
                    Action = action,
                    Stage = stage,
                    JobName = jobName,
                    Program = program,
                    Arguments = arguments,
                };

            default:
                // Unreachable: NeedsHardenedConfDir has already excluded every other scheme, and
                // a test pins the two lists together so adding a scheme cannot open a hole here.
                refusals.Add(Refuse(jobName, stageName,
                    $"{HookSchemes.Name(action.Scheme)}: is not a hook scheme.",
                    "Hooks are command:, service: and event:."));
                return null;
        }
    }

    private static CliDiagnostic Refuse(
        string jobName, string stage, string why, string? remedy) => new()
        {
            Severity = Severity.Error,
            Code = DiagnosticCode.HookRefused,
            Message = $"[{jobName}] the {stage} hook was refused: {why}",
            Job = jobName,
            Remedy = remedy,
        };
}
