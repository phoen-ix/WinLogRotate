using System.Runtime.InteropServices;
using WinLogRotate.Cli.Output;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Notify;
using WinLogRotate.Core.Safety;
using WinLogRotate.Core.Secrets;
using WinLogRotate.Core.State;
using WinLogRotate.Hosting;
using WinLogRotate.Hosting.Hosts;
using WinLogRotate.Hosting.Security;

namespace WinLogRotate.Cli.Commands;

/// <summary>
/// One command that answers "why is this not working".
/// </summary>
/// <remarks>
/// Every fact an operator would otherwise have to gather from four different consoles: where
/// the files are, whether the configuration directory is safe enough for hooks to run, whether
/// this process is elevated, whether long paths are enabled, and what is actually registered to
/// run rotations.
/// </remarks>
internal static class DoctorCommand
{
    public static int Run(CommandContext ctx, string? configDir)
    {
        var paths = InstallPaths.Resolve(configDir);
        var elevated = Privilege.IsElevated();

        ctx.Output.Line($"WinLogRotate {ProductInfo.Version} ({RuntimeInformation.FrameworkDescription})");
        ctx.Output.Line("");
        ctx.Output.Line("Paths");
        ctx.Output.Line($"  scope         {paths.Scope}");
        ctx.Output.Line($"  config        {paths.ConfigFile}       {Exists(File.Exists(paths.ConfigFile))}");
        ctx.Output.Line($"  jobs          {paths.ConfigDirectory}  {Exists(Directory.Exists(paths.ConfigDirectory))}");
        ctx.Output.Line($"  state         {paths.StateFile}        {Exists(File.Exists(paths.StateFile))}");
        ctx.Output.Line($"  journal       {paths.JournalDirectory} {Exists(Directory.Exists(paths.JournalDirectory))}");
        ctx.Output.Line("");

        var aclVerdict = AclVerdict.NotApplicable;
        var hooksAllowed = false;
        string? aclFix = null;

        if (OperatingSystem.IsWindows())
        {
            var finding = ConfDirGuard.Verify(paths.ConfigDirectory, scope: paths.Scope);
            aclVerdict = finding.Verdict;
            hooksAllowed = finding.HooksAllowed;
            aclFix = finding.FixCommand;

            ctx.Output.Line("Security");
            ctx.Output.Line($"  conf.d ACL    {finding.Verdict}");
            ctx.Output.Line($"  hooks         {(finding.HooksAllowed ? "permitted" : "REFUSED")}");

            // A per-user installation always lands here, and it is not broken. Reporting it as
            // a Critical security finding would be crying wolf at the one person least able to
            // judge it, and the repair it suggested would lock them out of their own config.
            if (finding.ExpectedForScope)
            {
                ctx.Output.Line("                by design for a per-user installation");
                ctx.Output.Diagnostic(new CliDiagnostic
                {
                    Severity = Severity.Info,
                    Code = DiagnosticCode.ConfigDirectoryInsecure,
                    Message = finding.Explanation ?? "Hooks are refused for a per-user installation.",
                    Path = paths.ConfigDirectory,
                    Remedy = finding.FixCommand,
                });
            }
            else if (finding.Verdict is not AclVerdict.Hardened and not AclVerdict.Unknown)
            {
                foreach (var ace in finding.OffendingAces)
                {
                    ctx.Output.Line($"                {ace}");
                }

                ctx.Output.Diagnostic(new CliDiagnostic
                {
                    Severity = Severity.Critical,
                    Code = DiagnosticCode.ConfigDirectoryInsecure,
                    Message = finding.Explanation ?? "The configuration directory is not secure.",
                    Path = paths.ConfigDirectory,
                    Remedy = finding.FixCommand,
                });
            }

            ctx.Output.Line("");
        }

        ctx.Output.Line("Process");
        ctx.Output.Line($"  elevated      {elevated}");
        ctx.Output.Line($"  long paths    {LongPathState()}");
        ctx.Output.Line("");

        var hostKind = RunHostKind.None;
        var hostDetail = "not checked (Windows only)";

        if (OperatingSystem.IsWindows())
        {
            var status = new TaskRunHost().Query();
            hostKind = status.Actual;
            hostDetail = status.Registered ? "registered" : "not registered";

            ctx.Output.Line("Run host");
            ctx.Output.Line($"  scheduled task  {hostDetail}");

            if (!status.Registered)
            {
                // A configuration that rotates nothing because nothing runs it is the most
                // common "it doesn't work" report there is.
                ctx.Output.Diagnostic(new CliDiagnostic
                {
                    Severity = Severity.Warning,
                    Code = DiagnosticCode.NoRunHost,
                    Message = "Nothing is registered to run rotations, so the configuration will never be applied.",
                    Remedy = "Run 'winlogrotate host use task' (needs administrator).",
                });
            }

            // Every section but the last ends with one, and Run host is no longer the last.
            ctx.Output.Line("");
        }

        var network = Network(ctx, paths);

        var result = new DoctorResult
        {
            Version = ProductInfo.Version,
            Scope = paths.Scope,
            Root = paths.Root,
            ConfigExists = File.Exists(paths.ConfigFile),
            JobsDirectoryExists = Directory.Exists(paths.ConfigDirectory),
            Elevated = elevated,
            AclVerdict = aclVerdict,
            HooksAllowed = hooksAllowed,
            AclFix = aclFix,
            RunHost = hostKind,
            RunHostDetail = hostDetail,
            Notify = network,
        };

        return ctx.Output.Complete("doctor", ExitCode.Ok, result);
    }

    /// <summary>
    /// What notifications are configured to do, without doing any of it.
    /// </summary>
    /// <remarks>
    /// <b>Nothing here opens a socket, resolves a name or reads the secret store.</b> The GUI runs
    /// <c>doctor --json</c> on every tab change, so a probe placed here would connect to the
    /// operator's relay several times a minute - and a diagnostic that generates the traffic it is
    /// meant to explain is worse than none. <c>notify test</c> is the live check, and this section
    /// says so.
    /// </remarks>
    private static NotifyDoctorDto Network(CommandContext ctx, InstallPaths paths)
    {
        var config = ConfigLoader.Load(
            paths, new PathGuard(new GuardOptions()),
            // doctor's own remarks forbid it: "nothing here opens a socket, resolves a name or
            // reads the secret store", because the GUI runs doctor --json on every tab change.
            new UnknownSecretLookup(), quarantineBadFiles: false);

        var settings = config.Notify;
        var state = NotifyStateStore.Load(paths.NotifyStateFile);

        var suppressed = state.Channels
            .Count(kv => BreakerPolicy.Verdict(kv.Value, settings) == BreakerVerdict.Open);

        var stored = config.NotifyProviders
            .SelectMany(p => p.Credentials())
            .Count(c => c.Reference.Source == SecretSource.Store);

        ctx.Output.Line("Notifications");
        ctx.Output.Line($"  reporting     {(settings.WouldSend ? "on" : "off")}"
            + (settings.WouldSend ? $", {settings.To.Count} target(s)" : " - nothing would be sent"));
        ctx.Output.Line($"  proxy         {(string.IsNullOrWhiteSpace(settings.Proxy) ? "machine default" : settings.Proxy)}");
        ctx.Output.Line($"  tls           {(settings.ServerCertThumbprint is { Length: > 0 } ? "pinned" : "machine certificate store")}");
        ctx.Output.Line($"  stored creds  {stored}");
        ctx.Output.Line($"  suppressed    {suppressed}");
        ctx.Output.Line("  live check    winlogrotate notify test");

        if (suppressed > 0)
        {
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Warning,
                Code = DiagnosticCode.NotifyCircuitOpen,
                Message = $"{suppressed} notification channel(s) are suppressed after repeated failures.",
                Remedy = "See 'winlogrotate notify status'. Fix the destination, then "
                       + "'winlogrotate notify test' - or 'winlogrotate notify reset' to clear the counter.",
            });
        }

        return new NotifyDoctorDto
        {
            Enabled = settings.WouldSend,
            Targets = settings.To.Count,
            Proxy = string.IsNullOrWhiteSpace(settings.Proxy) ? null : settings.Proxy,
            CertificatePinned = settings.ServerCertThumbprint is { Length: > 0 },
            StoredCredentials = stored,
            SuppressedChannels = suppressed,
        };
    }

    private static string Exists(bool present) => present ? "" : "  (missing)";

    /// <summary>
    /// Long paths need both the manifest entry and the machine-wide registry flag. The manifest
    /// half we ship; this reports the half we do not control.
    /// </summary>
    private static string LongPathState()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "n/a";
        }

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\FileSystem");
            var value = key?.GetValue("LongPathsEnabled");
            return value is int and 1 ? "enabled" : "disabled (paths over 260 characters may fail)";
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return "unknown (no access to the registry key)";
        }
    }
}
