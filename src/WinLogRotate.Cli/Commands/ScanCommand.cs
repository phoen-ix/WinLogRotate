using WinLogRotate.Cli.Output;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Hosting.Discovery;

namespace WinLogRotate.Cli.Commands;

internal static class ScanCommand
{
    public static int Run(CommandContext ctx)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Refusals.NeedsWindows<ScanResult>(
                ctx, "scan", "it reads the share modes other processes hold your logs with.");
        }

        var findings = ProducerScanner.Scan();

        if (findings.Count == 0)
        {
            ctx.Output.Line("No known log producers found on this machine.");
            return ctx.Output.Complete("scan", ExitCode.Ok, new ScanResult { Findings = [] });
        }

        foreach (var finding in findings)
        {
            ctx.Output.Line($"{finding.Producer}");
            ctx.Output.Line($"  {finding.Directory}");
            ctx.Output.Line($"  {finding.FileCount} file(s), {GlobCommand.Humanize(finding.TotalBytes)}");
            ctx.Output.Line($"  rotates itself: {(finding.SelfRotates ? "yes" : "no")}"
                          + $"    deletes old ones: {(finding.SelfDeletes ? "yes" : "NO")}");

            if (finding.Note is not null)
            {
                foreach (var line in Wrap(finding.Note, 72))
                {
                    ctx.Output.Line($"  {line}");
                }
            }

            ctx.Output.Line("");
            ctx.Output.Line($"  Suggested job:");
            ctx.Output.Line($"    kind  = \"{finding.SuggestedKind}\"");
            ctx.Output.Line($"    paths = [\"{finding.Pattern}\"]");
            ctx.Output.Line("");
        }

        var neglected = findings.Count(f => f is { SelfRotates: true, SelfDeletes: false });
        if (neglected > 0)
        {
            ctx.Output.Line($"{neglected} producer(s) roll their own logs and never delete them.");
            ctx.Output.Line("That combination is what fills disks, and a \"manage\" job is the fix:");
            ctx.Output.Line("it compresses and retains what they have finished with and never");
            ctx.Output.Line("touches the file they are still writing.");
        }

        return ctx.Output.Complete("scan", ExitCode.Ok, new ScanResult
        {
            Findings = findings.Select(f => new ScanFinding
            {
                Producer = f.Producer,
                Directory = f.Directory,
                Pattern = f.Pattern,
                SelfRotates = f.SelfRotates,
                SelfDeletes = f.SelfDeletes,
                SuggestedKind = f.SuggestedKind,
                Note = f.Note,
                FileCount = f.FileCount,
                TotalBytes = f.TotalBytes,
            }).ToArray(),
        });
    }

    private static IEnumerable<string> Wrap(string text, int width)
    {
        var line = new System.Text.StringBuilder();

        foreach (var word in text.Split(' '))
        {
            if (line.Length + word.Length + 1 > width)
            {
                yield return line.ToString();
                line.Clear();
            }

            if (line.Length > 0)
            {
                line.Append(' ');
            }

            line.Append(word);
        }

        if (line.Length > 0)
        {
            yield return line.ToString();
        }
    }
}
