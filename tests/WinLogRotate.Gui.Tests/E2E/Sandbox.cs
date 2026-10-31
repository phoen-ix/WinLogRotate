using System.Diagnostics;
using System.Text;
using System.Text.Json;

using WinLogRotate.Hosting.Security;

using Xunit;

namespace WinLogRotate.Gui.Tests.E2E;

/// <summary>
/// One journey's own machine: a configuration directory and the logs its jobs rotate, under
/// <see cref="Base"/> with a unique name, deleted again when the journey is done.
/// </summary>
/// <remarks>
/// Deleted only when it carries <see cref="Marker"/>, the same rule as the docs tour's demo root:
/// a directory the journeys did not make is never theirs to remove.
/// </remarks>
internal sealed class Sandbox : IDisposable
{
    public const string Base = @"C:\WinLogRotate-e2e";
    private const string Marker = ".winlogrotate-e2e";

    public Sandbox(string name, string config = "schema = 1\n")
    {
        Root = Path.Combine(Base, $"{name}-{Guid.NewGuid():N}"[..(name.Length + 9)]);
        Directory.CreateDirectory(Path.Combine(Config, "conf.d"));
        Directory.CreateDirectory(Logs);
        File.WriteAllText(Path.Combine(Root, Marker), "Made by WinLogRotate.Gui.Tests E2E; deleted when its journey ends.\n");
        File.WriteAllText(ConfigFile, config);
    }

    public string Root { get; }

    public string Config => Path.Combine(Root, "config");

    public string ConfigFile => Path.Combine(Config, "config.toml");

    public string Logs => Path.Combine(Root, "logs");

    /// <summary>
    /// Skips the journey unless scripts/e2e.sh asked for it: they open windows and, where the
    /// shell is not elevated, ask for elevation, which no plain <c>dotnet test</c> - or the docs
    /// tour, which runs this same project - should do by surprise.
    /// </summary>
    public static void Require()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The console is WinForms and the verbs write Windows paths: Windows only.");
        Assert.SkipUnless(Environment.GetEnvironmentVariable("WINLOGROTATE_E2E") == "1", "Set WINLOGROTATE_E2E=1 (scripts/e2e.sh does) to run the end-to-end journeys.");
    }

    /// <summary>Opt-in for the journeys that change the machine, not just their sandbox: CI sets it.</summary>
    public static bool MachineMayChange => Environment.GetEnvironmentVariable("WINLOGROTATE_E2E_MACHINE") == "1";

    /// <summary>
    /// Leaves the configuration as the installer leaves a per-machine one: its files owned by
    /// Administrators, the directory locked to SYSTEM and Administrators. Without it the Settings
    /// page rightly warns that hooks are off, which is a journey of its own.
    /// </summary>
    public void Harden()
    {
        foreach (var file in Directory.EnumerateFiles(Config, "*.toml", SearchOption.AllDirectories))
        {
            ConfigFileOwner.Claim(file);
        }

        var repair = Cli("host", "repair", "--acl", "--json");
        Assert.True(repair.ExitCode == 0, repair.ToString());
    }

    /// <summary>The glob a job uses for the logs in <paramref name="dir"/>, in the forward-slash form the docs use.</summary>
    public string Glob(string dir = "app", string pattern = "*.log") => Path.Combine(Logs, dir, pattern).Replace('\\', '/');

    /// <summary>Writes a job file into conf.d and returns its path.</summary>
    public string Job(string file, string name, string extra = "daily = true\nrotate = 3\n", string dir = "app")
    {
        var path = Path.Combine(Config, "conf.d", file);
        File.WriteAllText(path, $"schema = 1\n\n# kept by every edit\n[job]\nname = \"{name}\"\npaths = [\"{Glob(dir)}\"]\n{extra}");
        return path;
    }

    /// <summary>Writes a log of <paramref name="lines"/> lines and returns its path.</summary>
    public string Log(string name = "app.log", int lines = 200, string dir = "app")
    {
        var folder = Path.Combine(Logs, dir);
        Directory.CreateDirectory(folder);

        var text = new StringBuilder();
        for (var i = 0; i < lines; i++)
        {
            text.Append("2026-06-15 02:30:00 INFO e2e line ").Append(i).Append("\r\n");
        }

        var path = Path.Combine(folder, name);
        File.WriteAllText(path, text.ToString());
        return path;
    }

    public string[] Files(string dir = "app") =>
        Directory.GetFiles(Path.Combine(Logs, dir)).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;

    /// <summary>Runs winlogrotate.exe with this sandbox's configuration directory appended.</summary>
    public CliRun Cli(params string[] arguments) => Run([.. arguments, "--config-dir", Config]);

    /// <summary>Runs winlogrotate.exe, the one beside the tests, as the console does.</summary>
    public static CliRun Run(params string[] arguments)
    {
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "winlogrotate.exe"))
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return new CliRun(process.ExitCode, output.Result, error);
    }

    public void Dispose()
    {
        if (!File.Exists(Path.Combine(Root, Marker)))
        {
            return;
        }

        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left for the next run's inspection; the name is unique, so it collides with nothing.
        }
    }
}

/// <summary>What one winlogrotate.exe run said.</summary>
internal sealed record CliRun(int ExitCode, string StdOut, string StdErr)
{
    /// <summary>The <c>--json</c> envelope.</summary>
    public JsonElement Envelope => JsonDocument.Parse(StdOut).RootElement;

    public JsonElement Result => Envelope.GetProperty("result");

    public IEnumerable<string> Codes =>
        Envelope.TryGetProperty("diagnostics", out var list)
            ? list.EnumerateArray().Select(d => d.GetProperty("code").GetString()!)
            : [];

    public override string ToString() => $"exit {ExitCode}: {StdOut}{StdErr}";
}
