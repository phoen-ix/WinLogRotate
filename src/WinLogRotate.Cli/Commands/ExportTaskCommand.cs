using WinLogRotate.Cli.Output;
using WinLogRotate.Core;
using WinLogRotate.Hosting.Hosts;

namespace WinLogRotate.Cli.Commands;

/// <summary>
/// Prints the Scheduled Task XML instead of registering it.
/// </summary>
/// <remarks>
/// For deployment by Group Policy or DSC, where the task is created by the configuration
/// management system rather than by us - and for anyone who wants to read exactly what
/// <c>host use task</c> would register before letting it.
/// </remarks>
internal static class ExportTaskCommand
{
    public static int Run(CommandContext ctx, string? configDir)
    {
        var paths = InstallPaths.Resolve(configDir);

        // The time the registrar would use, from the reader the registrar uses. An export at a
        // time host use task would not register is the drift this verb exists to let people
        // read before it happens.
        var configured = HostCommand.ConfiguredTime(paths);

        foreach (var d in configured.Diagnostics)
        {
            ctx.Output.Diagnostic(d);
        }

        if (configured.Settings is not { } settings)
        {
            ctx.Output.Line("winlogrotate: the time the task fires could not be read from the configuration; nothing was exported.");
            return ctx.Output.Complete<ExportTaskResult>("host export-task", ExitCode.ConfigInvalid, null);
        }

        // The same mapping the registrar uses, so an exported task is the task that would be
        // registered. This used to build its own TaskDefinition and reach Arguments through a
        // throwaway HostInstallOptions with an empty executable path, which meant the two could
        // drift in exactly the way nobody would think to check.
        var xml = TaskXmlBuilder.Build(new HostInstallOptions
        {
            ExecutablePath = Environment.ProcessPath ?? @"C:\Program Files\WinLogRotate\winlogrotate.exe",
            ConfigDirectory = paths.Root,
            Account = RunAccount.System,
            TimeOfDay = settings.Time,
        }.ToTaskDefinition(), TimeProvider.System);

        ctx.Output.Line(xml);
        return ctx.Output.Complete("host export-task", ExitCode.Ok, new ExportTaskResult { Xml = xml });
    }
}
