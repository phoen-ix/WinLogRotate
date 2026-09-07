using System.Runtime.InteropServices;
using WinLogRotate.Cli.Output;
using WinLogRotate.Core;

namespace WinLogRotate.Cli.Commands;

internal static class VersionCommand
{
    public static int Run(CommandContext ctx)
    {
        var result = new VersionResult
        {
            Product = ProductInfo.Name,
            Version = ProductInfo.Version,
            Schema = ProductInfo.ContractSchema,
            Runtime = RuntimeInformation.FrameworkDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            Elevated = Privilege.IsElevated(),
        };

        // Bare `--version` prints the number and nothing else, so it can be captured
        // directly in a script. Everything else is behind --json or --verbose.
        ctx.Output.Line(ctx.Output.Verbose
            ? $"{result.Product} {result.Version} ({result.Runtime}, {result.Architecture})"
            : result.Version);

        return ctx.Output.Complete("version", ExitCode.Ok, result);
    }
}
