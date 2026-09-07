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
            NativeAot = IsNativeImage(),
        };

        // Bare `--version` prints the number and nothing else, so it can be captured
        // directly in a script. Everything else is behind --json or --verbose.
        ctx.Output.Line(ctx.Output.Verbose
            ? $"{result.Product} {result.Version} ({result.Runtime}, {result.Architecture}{(result.NativeAot ? ", native" : "")})"
            : result.Version);

        return ctx.Output.Complete("version", ExitCode.Ok, result);
    }

    /// <summary>
    /// True only in an actually-native image.
    /// <para>
    /// RuntimeFeature.IsDynamicCodeSupported alone is NOT enough, and getting this wrong is
    /// easy: setting PublishAot in the csproj stamps that feature switch into
    /// runtimeconfig.json for every build, so an ordinary JIT `dotnet run` reports itself as
    /// native. The discriminator is the entry assembly's Location, which the runtime reports
    /// as empty for a native image and as a real path under the JIT. Requiring both keeps a
    /// bug report honest about which binary the reporter was actually running.
    /// </para>
    /// </summary>
    private static bool IsNativeImage()
    {
        if (System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
        {
            return false;
        }

        // IL3000 fires here because Assembly.Location returns "" in a single-file or native
        // app. That emptiness is precisely the signal we want, so the analyzer is right about
        // the fact and wrong about the intent. Suppressed narrowly rather than project-wide:
        // anywhere else in this codebase, IL3000 is a real bug and must stay an error.
#pragma warning disable IL3000
        return string.IsNullOrEmpty(System.Reflection.Assembly.GetEntryAssembly()?.Location);
#pragma warning restore IL3000
    }
}
