using WinLogRotate.Core;

namespace WinLogRotate.Cli;

/// <summary>
/// Entry point. Milestone 0 deliberately has no package dependencies at all: proving the
/// NativeAOT toolchain, the size gate and the manifest work on a bare exe is worth more
/// than doing it later with System.CommandLine in the way. The real verb tree lands in M1.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] is "--version" or "-V")
        {
            Console.WriteLine(ProductInfo.Version);
            return ExitCode.Ok;
        }

        if (args.Length == 0 || args[0] is "--help" or "-h" or "-?")
        {
            Console.WriteLine($"{ProductInfo.Name} {ProductInfo.Version} - log rotation for Windows");
            Console.WriteLine();
            Console.WriteLine("  winlogrotate --version");
            Console.WriteLine("  winlogrotate --help");
            Console.WriteLine();
            Console.WriteLine("The verb tree (run, probe, host, import, scan, doctor) lands in M1.");
            return ExitCode.Ok;
        }

        Console.Error.WriteLine($"winlogrotate: unknown option '{args[0]}'");
        return ExitCode.ConfigInvalid;
    }
}
