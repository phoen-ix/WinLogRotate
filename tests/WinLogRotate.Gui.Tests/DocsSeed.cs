using System.Diagnostics;
using System.Text;

using Microsoft.Extensions.Time.Testing;

using WinLogRotate.Contracts;
using WinLogRotate.Core.Journaling;
using WinLogRotate.Hosting.Security;

namespace WinLogRotate.Gui.Tests;

/// <summary>
/// The fictional machine the docs screenshots show: a web server with a shop API, an IIS site and
/// one job switched off, plus a second configuration with mistakes in it and a third with none.
/// </summary>
/// <remarks>
/// <para>
/// A fixed root rather than a temporary directory, because every path a page shows comes from
/// here and a random name would change every PNG on every run. It is wiped and rebuilt each time,
/// and only ever when it carries <see cref="Marker"/>, so a directory of that name that the tour
/// did not make is refused rather than deleted.
/// </para>
/// <para>
/// Files are dated relative to now, because manage mode reads modification times; what the pages
/// show of them is names and sizes, which are fixed. The journal is written through the
/// product's own <see cref="JournalWriter"/> with fixed timestamps, so the History page is the
/// same on every run.
/// </para>
/// </remarks>
internal static class DocsSeed
{
    public const string Root = @"C:\WinLogRotate-demo";
    private const string Marker = ".winlogrotate-docs-demo";

    public static string Config => Path.Combine(Root, "config");
    public static string EmptyConfig => Path.Combine(Root, "config-empty");
    public static string ProblemConfig => Path.Combine(Root, "config-problem");

    /// <summary>Builds the machine; returns what went wrong hardening it, which the tour reports after shooting.</summary>
    public static string? Build()
    {
        Clean();
        Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(Root, Marker), "Made by WinLogRotate.Gui.Tests; deleted on the next tour.\n");

        WriteConfig(Config, ConfigToml, ShopApi, Iis, Importer);
        WriteConfig(EmptyConfig, "schema = 1\n");
        WriteConfig(ProblemConfig, "schema = 1\n", ShopApi, Iis, Billing);

        Logs();
        Journal();

        // As the installer leaves a per-machine install: owned by Administrators rather than by
        // the account that wrote the files, and locked to SYSTEM and Administrators - so the
        // pages show hooks as allowed rather than the warning only a per-user install has.
        foreach (var file in Directory.EnumerateFiles(Config, "*.toml", SearchOption.AllDirectories))
        {
            ConfigFileOwner.Claim(file);
        }

        return Cli("host", "repair", "--acl", "--json", "--config-dir", Config);
    }

    private static string? Cli(params string[] arguments)
    {
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "winlogrotate.exe"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return process.ExitCode == 0
            ? null
            : $"winlogrotate {string.Join(' ', arguments)} exited {process.ExitCode}: {output.Result}{error}";
    }

    public static void Clean()
    {
        if (!Directory.Exists(Root))
        {
            return;
        }

        if (!File.Exists(Path.Combine(Root, Marker)))
        {
            throw new InvalidOperationException($"{Root} exists and was not made by the docs tour; refusing to delete it.");
        }

        Directory.Delete(Root, recursive: true);
    }

    private const string ConfigToml = """
        schema = 1

        [defaults]
        compress = true
        missingok = true

        [host]
        time = "02:30"

        [notify]
        to = ["email.ops", "webhook.teams"]

        [notify.email.ops]
        host = "smtp.contoso.example"
        port = 587
        tls = "required"
        auth = "login"
        username = "winlogrotate"
        password = "@secret:ops-mail"
        from = "winlogrotate@contoso.example"
        to = ["ops@contoso.example"]
        subject_prefix = "[WEB01]"

        [notify.webhook.teams]
        url = "@secret:teams-hook"

        """;

    private static readonly (string File, string Toml) ShopApi = ("shop-api.toml", """
        schema = 1

        [job]
        name = "Shop API"
        kind = "rotate"
        paths = ["C:/WinLogRotate-demo/logs/shop/*.log"]
        daily = true
        rotate = 7
        delaycompress = true
        notifempty = true
        lockstrategy = "auto"

        """);

    private static readonly (string File, string Toml) Iis = ("iis-w3svc1.toml", """
        schema = 1

        # IIS rolls its own logs and never deletes them: compress and prune, never touch the live one.
        [job]
        name = "IIS W3SVC1"
        kind = "manage"
        paths = ["C:/WinLogRotate-demo/logs/iis/W3SVC1/u_ex*.log"]
        rotate = 7

        """);

    private static readonly (string File, string Toml) Importer = ("legacy-importer.toml", """
        schema = 1

        [job]
        name = "Legacy importer"
        enabled = false
        paths = ["C:/WinLogRotate-demo/logs/importer/*.log"]
        weekly = true
        rotate = 4

        """);

    private static readonly (string File, string Toml) Billing = ("billing.toml", """
        schema = 1

        [job]
        name = "Billing export"
        paths = ["C:/WinLogRotate-demo/logs/billing/*.log"]
        daily = true
        rotate = 14
        maxage = -1
        compresss = true

        """);

    private static void WriteConfig(string dir, string config, params (string File, string Toml)[] jobs)
    {
        Directory.CreateDirectory(Path.Combine(dir, "conf.d"));
        File.WriteAllText(Path.Combine(dir, "config.toml"), config);

        foreach (var (file, toml) in jobs)
        {
            File.WriteAllText(Path.Combine(dir, "conf.d", file), toml);
        }
    }

    private static void Logs()
    {
        var now = DateTime.UtcNow;

        var shop = Path.Combine(Root, @"logs\shop");
        Log(shop, "shop-api.log", 2_400, now);
        Log(shop, "shop-api.log.1", 2_100, now.AddDays(-1));
        Log(shop, "shop-api.log.2.zip", 180, now.AddDays(-2));
        Log(shop, "shop-api.log.3.zip", 170, now.AddDays(-3));

        // Two weeks of a site IIS rolls daily, the newest still being written.
        var iis = Path.Combine(Root, @"logs\iis\W3SVC1");
        for (var day = 2; day <= 15; day++)
        {
            Log(iis, $"u_ex2606{day:00}.log", 900 + (day * 40), now.AddDays(day - 15));
        }

        Log(Path.Combine(Root, @"logs\importer"), "importer.log", 300, now);
    }

    /// <summary>A log of <paramref name="lines"/> fixed lines, so every size the pages print is the same each run.</summary>
    private static void Log(string dir, string name, int lines, DateTime written)
    {
        Directory.CreateDirectory(dir);

        var text = new StringBuilder();
        for (var i = 0; i < lines; i++)
        {
            text.Append("2026-06-15 02:30:00 INFO request handled in 12 ms, status 200, line ").Append(i).Append("\r\n");
        }

        var path = Path.Combine(dir, name);
        File.WriteAllText(path, text.ToString());
        File.SetLastWriteTimeUtc(path, written);
    }

    /// <summary>Three nights of the scheduled task, the middle one with a file it could not compress.</summary>
    private static void Journal()
    {
        var journal = Path.Combine(Config, "journal");
        var iis = "C:/WinLogRotate-demo/logs/iis/W3SVC1/";
        var shop = "C:/WinLogRotate-demo/logs/shop/";

        for (var night = 13; night <= 15; night++)
        {
            var at = new DateTimeOffset(2026, 6, night, 2, 30, 0, TimeSpan.Zero);
            using var writer = JournalWriter.Open(journal, new FakeTimeProvider(at), $"01JXDEMORUN0000000000000{night}");
            var second = 0;

            void Write(string operation, string? job, string? src = null, string? dst = null,
                string? reason = null, string? result = OpResult.Ok, string? error = null,
                long? before = null, long? after = null, string? strategy = null)
            {
                var ts = at.AddSeconds(second++).ToString("O", System.Globalization.CultureInfo.InvariantCulture);

                var plan = new CliEvent
                {
                    Ts = ts,
                    Run = string.Empty,
                    Operation = operation,
                    Phase = Phase.Plan,
                    Job = job,
                    Src = src,
                    Dst = dst,
                    Reason = reason,
                    Strategy = strategy,
                    BytesBefore = before,
                };

                writer.Write(plan);
                writer.Write(plan with { Phase = Phase.Apply, Result = result, Error = error, BytesAfter = after, Ms = 40 });
            }

            var log = $"u_ex2606{night - 1:00}.log";
            Write(Op.Compress, "IIS W3SVC1", iis + log, iis + log + ".zip", "compress = zip",
                result: night == 14 ? OpResult.Failed : OpResult.Ok,
                error: night == 14 ? "The process cannot access the file because it is being used by another process." : null,
                before: 48_213_760, after: night == 14 ? null : 3_145_728);
            Write(Op.Delete, "IIS W3SVC1", iis + $"u_ex2605{night + 10:00}.log.zip", reason: "rotate = 7 keeps 7 archive(s); this is number 8");
            Write(Op.Compress, "Shop API", shop + "shop-api.log.1", shop + "shop-api.log.2.zip", "delaycompress: compressed one rotation late",
                before: 2_097_152, after: 180_224);
            Write(Op.Rename, "Shop API", shop + "shop-api.log", shop + "shop-api.log.1", "due; lockstrategy = auto", strategy: "rename");
            Write(Op.Create, "Shop API", shop + "shop-api.log", reason: "recreating the log the writer expects to find");
        }
    }
}
