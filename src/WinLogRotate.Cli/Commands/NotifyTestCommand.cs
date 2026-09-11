using System.Diagnostics;
using WinLogRotate.Cli.Output;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Notify;
using WinLogRotate.Core.Notify.Delivery;
using WinLogRotate.Core.Safety;
using WinLogRotate.Core.Secrets;
using WinLogRotate.Core.State;

namespace WinLogRotate.Cli.Commands;

/// <summary>
/// Sends a real message to every configured channel, and records nothing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Inert by design.</b> It writes no <c>notify.json</c>, moves no breaker counter and advances
/// no outcome, so running it can never change what tonight's run decides. That is the whole point:
/// a diagnostic that mutates the thing it is diagnosing is not a diagnostic.
/// </para>
/// <para>
/// It also <b>ignores an open breaker</b> and says so, because the command an operator reaches for
/// to check whether a channel is fixed must not be the one that refuses to check. The output says
/// which channels a real run would currently skip, so its answer is never mistaken for the run's.
/// </para>
/// </remarks>
internal static class NotifyTestCommand
{
    public static int Run(CommandContext ctx, string? target, string? configDir)
    {
        var paths = InstallPaths.Resolve(configDir);
        var config = ConfigLoader.Load(paths, new PathGuard(new GuardOptions()), new UnknownSecretLookup(), quarantineBadFiles: false);
        var settings = config.Notify;

        if (settings.To.Count == 0)
        {
            ctx.Output.Line("No notification targets are configured, so there is nothing to test.");
            ctx.Output.Line("Add one to [notify] in " + paths.ConfigFile + ", e.g. to = [\"eventlog:\"].");
            return ctx.Output.Complete("notify test", ExitCode.Ok, Empty());
        }

        using var senders = Senders.Build(settings);

        var resolved = ChannelResolver.Resolve(
            settings, config.NotifyProviders,
            new SecretResolver(Senders.Platform(), paths.SecretsFile),
            senders.Table);

        foreach (var diagnostic in resolved.Diagnostics)
        {
            ctx.Output.Diagnostic(diagnostic);
        }

        var channels = target is null
            ? resolved.Channels
            : [.. resolved.Channels.Where(c =>
                c.Display.Contains(target, StringComparison.OrdinalIgnoreCase)
                || c.Key.Contains(target, StringComparison.OrdinalIgnoreCase))];

        if (channels.Count == 0)
        {
            ctx.Output.Line(target is null
                ? "No target could be used. The diagnostics above say why."
                : $"No configured target matches '{target}'.");

            return ctx.Output.Complete("notify test", ExitCode.Ok, Empty());
        }

        // Loaded read-only, purely so the output can say which channels a real run would skip.
        // Never saved.
        var state = NotifyStateStore.Load(paths.NotifyStateFile);

        var run = new RunSummary
        {
            RunId = "notify-test",
            Machine = Environment.MachineName,
            JobsRun = 0,
            Completed = 0,
            BytesFreed = 0,
            ObservedJobs = [],
        };

        var message = Sample(run);
        var now = TimeProvider.System.GetUtcNow();
        var results = new List<NotifyTestChannelDto>();
        var failures = 0;

        ctx.Output.Line($"Sending a test message to {channels.Count} channel(s) as {run.Machine}.");
        ctx.Output.Line(string.Empty);

        foreach (var channel in channels)
        {
            var composed = new NotifyMessage
            {
                Subject = Redaction.MaskText(message.Subject, settings.Redact),
                Body = MessageComposer.Render(message, run, channel.Limit, now, settings.Redact),
                Plan = message,
                Run = run,
            };

            var suppressed = BreakerPolicy.Verdict(state.ChannelOrDefault(channel.Key), settings)
                          == BreakerVerdict.Open;

            var clock = Stopwatch.StartNew();
            var result = senders.Table.For(channel).Send(channel, composed, settings.Budget);
            clock.Stop();

            if (!result.Ok)
            {
                failures++;
            }

            var note = suppressed ? "   (a real run would skip this one - breaker open)" : string.Empty;

            ctx.Output.Line(result.Ok
                ? $"  ok      {channel.Display,-32} {clock.ElapsedMilliseconds} ms{note}"
                : $"  FAILED  {channel.Display,-32} {result.Error}{note}");

            if (result.Note is { } warning)
            {
                ctx.Output.Diagnostic(new CliDiagnostic
                {
                    Severity = Severity.Warning,
                    Code = DiagnosticCode.NotifyMisconfigured,
                    Message = warning,
                });
            }

            results.Add(new NotifyTestChannelDto
            {
                Channel = channel.Key,
                Display = channel.Display,
                Ok = result.Ok,
                Milliseconds = clock.ElapsedMilliseconds,
                Status = result.Status == 0 ? null : result.Status,
                Error = result.Error,
                WouldBeSkipped = suppressed,
            });
        }

        ctx.Output.Line(string.Empty);
        ctx.Output.Line("Nothing was recorded: no history, no breaker counters, no state.");

        // Exit 0 even when a channel failed, matching every other notification path: the rotation
        // is what this product does, and its exit code must not depend on a webhook. The per-line
        // FAILED and the JSON payload are how a script learns the outcome.
        return ctx.Output.Complete("notify test", ExitCode.Ok, new NotifyTestResult
        {
            Sent = results.Count - failures,
            Failed = failures,
            Channels = results,
        });
    }

    private static NotifyTestResult Empty() => new() { Sent = 0, Failed = 0, Channels = [] };

    /// <summary>
    /// A message shaped exactly like a real one, so what arrives is what an incident looks like.
    /// </summary>
    /// <remarks>
    /// Deliberately not "hello world". The point of a test send is to see the format, the subject
    /// line and the truncation an alert will actually have, in the client that will receive it.
    /// </remarks>
    private static PlannedNotification Sample(RunSummary run)
    {
        DigestLine[] lines =
        [
            new()
            {
                Severity = Severity.Error,
                Code = DiagnosticCode.FileLocked,
                Job = "example",
                Text = "This is a test. No log file was touched and no job is failing.",
                Where = @"C:\inetpub\logs\LogFiles\W3SVC1",
                DirectoryKey = @"c:\inetpub\logs\logfiles\w3svc1",
                Count = 1,
            },
        ];

        return new PlannedNotification
        {
            Reason = NotifyReason.NewFailure,
            Job = "example",
            Severity = Severity.Error,
            Subject = MessageComposer.Subject(NotifyReason.NewFailure, "example", lines, run),
            Lines = lines,
            Context = lines,
            Fingerprint = "test",
            NextState = new JobNotifyState(),
        };
    }
}
