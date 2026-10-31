using System.Net;
using System.Net.Sockets;

using Xunit;

namespace WinLogRotate.Gui.Tests.E2E;

/// <summary>
/// The verbs end to end: the built winlogrotate.exe as a process, its exit codes, its --json
/// envelopes and the files it leaves, on an elevated Windows box against a sandbox of its own.
/// </summary>
/// <remarks>
/// The verbs' logic is unit-tested in-process (Core.Tests); the installed product as SYSTEM is
/// installer-smoke's (ci.yml). This is the layer between: the real executable, real arguments,
/// real files.
/// </remarks>
[Trait("Category", "E2E")]
[Collection(ConsoleCollection.Name)]
public sealed class CliJourneys
{
    private static void Ok(CliRun run) => Assert.True(run.ExitCode == 0, run.ToString());

    [Fact]
    public void TheVersionHelpAndAParseErrorAnswerAsDocumented()
    {
        Sandbox.Require();
        var version = Sandbox.Run("--version");
        Ok(version);
        Assert.Matches(@"\d+\.\d+\.\d+", version.StdOut);

        Ok(Sandbox.Run("--help"));

        var wrong = Sandbox.Run("rotate-everything", "--json");
        Assert.Equal(2, wrong.ExitCode);
        Assert.Contains("LR1007", wrong.Codes);
    }

    [Fact]
    public void AJobIsAddedChangedSwitchedOffShownAndRemovedByTheVerbs()
    {
        Sandbox.Require();
        using var box = new Sandbox("cli-job");
        box.Log();

        var dry = box.Cli("job", "add", "App", "--paths", box.Glob(), "--set", "daily=true", "--set", "rotate=3", "--dry-run", "--json");
        Ok(dry);
        Assert.Empty(Directory.GetFiles(Path.Combine(box.Config, "conf.d")));

        Ok(box.Cli("job", "add", "App", "--paths", box.Glob(), "--set", "daily=true", "--set", "rotate=3", "--json"));
        var file = Assert.Single(Directory.GetFiles(Path.Combine(box.Config, "conf.d")));

        var duplicate = box.Cli("job", "add", "App", "--paths", box.Glob(), "--json");
        Assert.NotEqual(0, duplicate.ExitCode);

        var before = File.ReadAllText(file);
        Ok(box.Cli("job", "set", "App", "--set", "rotate=5", "--json"));
        Assert.Equal(before.Replace("rotate = 3", "rotate = 5", StringComparison.Ordinal), File.ReadAllText(file));

        Ok(box.Cli("job", "disable", "App", "--json"));
        Assert.Contains("enabled = false", File.ReadAllText(file), StringComparison.Ordinal);
        Ok(box.Cli("job", "enable", "App", "--json"));
        Assert.DoesNotContain("enabled = false", File.ReadAllText(file), StringComparison.Ordinal);

        var show = box.Cli("job", "show", "App", "--json");
        Ok(show);
        Assert.Contains("\"App\"", show.StdOut, StringComparison.Ordinal);

        var config = box.Cli("config", "show", "--json");
        Ok(config);
        Assert.Contains("\"App\"", config.StdOut, StringComparison.Ordinal);
        Ok(box.Cli("config", "check", "--json"));

        Ok(box.Cli("job", "remove", "App", "--json"));
        Assert.False(File.Exists(file));
        Assert.NotEqual(0, box.Cli("job", "show", "App", "--json").ExitCode);
    }

    [Fact]
    public void ConfigCheckExitCodesTellABrokenJobFromABrokenConfiguration()
    {
        Sandbox.Require();
        using var box = new Sandbox("cli-check");
        box.Job("app.toml", "App");
        box.Job("broken.toml", "Broken", "daily = true\nmaxage = -1\ncompresss = true\n");

        var check = box.Cli("config", "check", "--json");
        Assert.NotEqual(0, check.ExitCode);
        Assert.Contains(check.Codes, c => c.StartsWith("LR", StringComparison.Ordinal));

        File.WriteAllText(box.ConfigFile, "schema = 1\n[defaults\n");
        var unreadable = box.Cli("config", "check", "--json");
        Assert.NotEqual(0, unreadable.ExitCode);
        Assert.NotEqual(check.ExitCode, unreadable.ExitCode);
    }

    [Fact]
    public void ADryRunChangesNothingARunRotatesAndTheJournalRecordsIt()
    {
        Sandbox.Require();
        using var box = new Sandbox("cli-run");
        box.Log();
        box.Job("app.toml", "App");

        var dry = box.Cli("run", "--dry-run", "--force", "--json", "--no-notify");
        Ok(dry);
        Assert.True(dry.Result.GetProperty("dryRun").GetBoolean());
        Assert.Equal(["app.log"], box.Files());

        var run = box.Cli("run", "--force", "--json", "--no-notify");
        Ok(run);
        Assert.Equal(1, run.Result.GetProperty("jobsRun").GetInt32());
        Assert.Contains(box.Files(), f => f.StartsWith("app.log.1", StringComparison.Ordinal));

        var journal = box.Cli("journal", "--job", "App", "--json");
        Ok(journal);
        Assert.True(journal.Result.GetProperty("count").GetInt32() > 0, journal.ToString());

        // Due daily and rotated a moment ago: an unforced run leaves it alone.
        var again = box.Cli("run", "--json", "--no-notify");
        Ok(again);
        Assert.Equal(0, again.Result.GetProperty("completed").GetInt32());

        // Paused, a run rotates nothing even when forced; the same verb resumes.
        var pause = box.Cli("host", "pause", "--for", "01:00:00", "--json");
        Ok(pause);
        Assert.Equal("paused", pause.Result.GetProperty("action").GetString());
        var paused = box.Cli("run", "--force", "--json", "--no-notify");
        Assert.Contains("paused", paused.StdOut, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("resumed", box.Cli("host", "pause", "--json").Result.GetProperty("action").GetString());
    }

    [Fact]
    public void GlobAndProbeAnswerForRealFiles()
    {
        Sandbox.Require();
        using var box = new Sandbox("cli-glob");
        var log = box.Log();
        box.Log("other.log");

        var glob = Sandbox.Run("glob", box.Glob(), "--json");
        Ok(glob);
        Assert.Contains("other.log", glob.StdOut, StringComparison.Ordinal);

        Ok(Sandbox.Run("probe", log, "--json"));
    }

    [Fact]
    public async Task NotifyVerbsShowTestAndReset()
    {
        Sandbox.Require();
        var cancel = TestContext.Current.CancellationToken;
        using var sink = new TcpListener(IPAddress.Loopback, 0);
        sink.Start();
        var port = ((IPEndPoint)sink.LocalEndpoint).Port;
        var accepted = Task.Run(async () =>
        {
            using var client = await sink.AcceptTcpClientAsync(cancel).ConfigureAwait(false);
            var stream = client.GetStream();
            await stream.ReadAsync(new byte[8192], cancel).ConfigureAwait(false);
            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), cancel).ConfigureAwait(false);
        }, cancel);

        using var box = new Sandbox("cli-notify", $"schema = 1\n\n[notify]\nto = [\"webhook.sink\"]\n\n[notify.webhook.sink]\nurl = \"http://127.0.0.1:{port}/hook\"\n");

        var show = box.Cli("notify", "show", "--json");
        Ok(show);
        Assert.Contains("webhook.sink", show.StdOut, StringComparison.Ordinal);

        var test = box.Cli("notify", "test", "--json");
        Ok(test);
        Assert.Equal(1, test.Result.GetProperty("sent").GetInt32());
        await accepted.WaitAsync(TimeSpan.FromSeconds(10), cancel);

        Ok(box.Cli("notify", "status", "--json"));
        Ok(box.Cli("notify", "reset", "--json"));
    }

    [Fact]
    public void SecretsAreSetListedTestedImportedAndRemoved()
    {
        Sandbox.Require();
        using var box = new Sandbox("cli-secret");
        var value = Path.Combine(box.Root, "value.txt");
        File.WriteAllText(value, "not-a-real-secret");
        var lines = Path.Combine(box.Root, "secrets.txt");
        File.WriteAllText(lines, "e2e-one=not-a-real-secret\ne2e-two=not-a-real-either\n");

        Ok(box.Cli("secret", "set", "e2e-file", "--from-file", value, "--json"));
        Ok(box.Cli("secret", "import", "--from-file", lines, "--json"));

        var list = box.Cli("secret", "list", "--json");
        Ok(list);
        foreach (var name in new[] { "e2e-file", "e2e-one", "e2e-two" })
        {
            Assert.Contains(name, list.StdOut, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("not-a-real", list.StdOut, StringComparison.Ordinal);
        Ok(box.Cli("secret", "test", "e2e-file", "--json"));

        Ok(box.Cli("secret", "remove", "e2e-file", "--json"));
        Assert.NotEqual(0, box.Cli("secret", "test", "e2e-file", "--json").ExitCode);
    }

    [Fact]
    public void ImportConvertsALogrotateConfigurationIntoJobFiles()
    {
        Sandbox.Require();
        using var box = new Sandbox("cli-import");
        var source = Path.Combine(box.Root, "logrotate.conf");
        File.WriteAllText(source, "/var/log/app/*.log {\n  daily\n  rotate 5\n  compress\n  missingok\n}\n");
        var output = Path.Combine(box.Root, "imported");

        Ok(box.Cli("import", source, "--out", output, "--json"));
        var job = Assert.Single(Directory.GetFiles(output, "*.toml"));
        Assert.Contains("rotate = 5", File.ReadAllText(job), StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorHostStatusAndTheTaskXmlDescribeTheMachine()
    {
        Sandbox.Require();
        using var box = new Sandbox("cli-doctor");
        box.Job("app.toml", "App");

        var doctor = box.Cli("doctor", "--json");
        Ok(doctor);
        Assert.True(doctor.Result.GetProperty("configExists").GetBoolean());

        Ok(box.Cli("host", "status", "--json"));

        var xml = box.Cli("host", "export-task");
        Ok(xml);
        Assert.Contains("<Task", xml.StdOut, StringComparison.Ordinal);

        // Read-only: it reports the producers it finds, and its exit code says whether any worried it.
        Assert.Equal("scan", Sandbox.Run("scan", "--json").Envelope.GetProperty("verb").GetString());
    }
}
