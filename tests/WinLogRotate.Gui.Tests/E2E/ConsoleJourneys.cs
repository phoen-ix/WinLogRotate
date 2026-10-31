using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.Win32;

using WinLogRotate.Gui.Cli;
using WinLogRotate.Gui.Pages;
using WinLogRotate.Gui.Ui;

using Xunit;

using static WinLogRotate.Gui.Tests.ConsoleDriver;

namespace WinLogRotate.Gui.Tests.E2E;

/// <summary>Every page of the console, and what each one does beyond the Jobs page.</summary>
[Trait("Category", "E2E")]
[Collection(ConsoleCollection.Name)]
public sealed class ConsoleJourneys
{
    public static TheoryData<string> PageNames => ["Jobs", "Run", "History", "Scheduling", "Notifications", "Settings"];

    /// <summary>What each page shows once it has loaded, on a configuration with one job and one channel.</summary>
    private static Func<bool> Loaded(Form form, string page) => page switch
    {
        "Jobs" => () => Row(Grid(form), "App") is not null,
        "Run" => () => Button(form, "Dry run", required: false) is not null && Button(form, "Rotate now", required: false) is not null,
        "History" => () => Says(form, "Nothing has been rotated yet.") || Says(form, "operation(s)."),
        "Scheduling" => () => Says(form, "Currently:"),
        "Notifications" => () => Grid(form).Rows.Count > 0,
        _ => () => All(form).OfType<TextBox>().Any(t => t.Multiline && t.Text.Length > 0),
    };

    private static Sandbox Populated(string name, string notify = "")
    {
        var box = new Sandbox(name, "schema = 1\n" + notify);
        box.Log();
        box.Job("app.toml", "App");
        return box;
    }

    private const string Webhook = """

        [notify]
        to = ["webhook.sink"]

        [notify.webhook.sink]
        url = "http://127.0.0.1:{0}/hook"

        """;

    /// <summary>The sweep: each page opens on its own, loads, and raises no dialog and no exception.</summary>
    [Theory]
    [MemberData(nameof(PageNames))]
    public void EveryPageLoads(string page)
    {
        Sandbox.Require();
        using var box = Populated("sweep", string.Format(System.Globalization.CultureInfo.InvariantCulture, Webhook, 9));

        ConsoleDriver.Run(d =>
        {
            using var form = d.Open(box.Config);
            d.Wait(Loaded(form, "Jobs"), "the Jobs page");
            d.Navigate(form, page);
            d.Wait(Loaded(form, page), $"the {page} page");
        });
    }

    /// <summary>The sweep on a fresh install: no jobs, no channels, no journal - every page still loads.</summary>
    [Fact]
    public void EveryPageLoadsOnAnEmptyConfiguration()
    {
        Sandbox.Require();
        using var box = new Sandbox("empty");

        ConsoleDriver.Run(d =>
        {
            using var form = d.Open(box.Config);
            d.Wait(() => Button(form, JobsEmptyState.Add.Text, required: false) is not null, "the empty state");

            d.Navigate(form, "Run");
            d.Wait(Loaded(form, "Run"), "the Run page");
            d.Navigate(form, "History");
            d.Wait(() => Says(form, "Nothing has been rotated yet."), "the empty journal");
            d.Navigate(form, "Scheduling");
            d.Wait(Loaded(form, "Scheduling"), "the Scheduling page");
            d.Navigate(form, "Notifications");
            d.Wait(() => Says(form, "Nothing is configured, so a failed rotation would tell nobody."), "the Notifications page");
            d.Navigate(form, "Settings");
            d.Wait(Loaded(form, "Settings"), "the Settings page");
        });
    }

    [Fact]
    public void ADryRunChangesNothingAndRotateNowAsksThenRotatesIntoTheHistory()
    {
        Sandbox.Require();
        using var box = Populated("run");

        ConsoleDriver.Run(d =>
        {
            using var form = d.Open(box.Config);
            d.Navigate(form, "Run");
            var output = All(form).OfType<TextBox>().First(t => t.Multiline);

            d.Click(Button(form, "Dry run"));
            d.Wait(() => output.Text.Contains("dry run:", StringComparison.Ordinal) && Button(form, "Dry run").Enabled, "the dry run's summary");
            Assert.Equal(["app.log"], box.Files());

            d.Dialog(() => Button(form, "Rotate now").PerformClick(), confirm =>
            {
                Assert.Equal("Rotate now", confirm.Text);
                Button(confirm, "No").PerformClick();
            });
            Assert.Equal(["app.log"], box.Files());

            d.Dialog(() => Button(form, "Rotate now").PerformClick(), confirm => Button(confirm, "Yes").PerformClick());
            d.Wait(() => Button(form, "Rotate now").Enabled && box.Files().Length > 1, "the rotation", seconds: 60);
            Assert.Contains(box.Files(), f => f.StartsWith("app.log.1", StringComparison.Ordinal));

            d.Navigate(form, "History");
            var history = Grid(form);
            d.Wait(() => history.Rows.Count > 0, "the journal");
            Assert.Contains(history.Rows.Cast<DataGridViewRow>(), r => r.Cells.Cast<DataGridViewCell>().Any(c => c.Value as string == "App"));

            var before = history.Rows.Count;
            history.Rows.Clear();
            d.Click(Button(form, "Refresh"));
            d.Wait(() => Grid(form).Rows.Count == before, "the refreshed journal");
        });
    }

    [Fact]
    public void SendTestDeliversToTheWebhookAndNamesAChannelThatFailed()
    {
        Sandbox.Require();
        using var sink = new TcpListener(IPAddress.Loopback, 0);
        sink.Start();
        var port = ((IPEndPoint)sink.LocalEndpoint).Port;
        var received = Receive(sink);

        using var box = Populated("notify", string.Format(System.Globalization.CultureInfo.InvariantCulture, Webhook, port));

        ConsoleDriver.Run(d =>
        {
            using var form = d.Open(box.Config);
            d.Navigate(form, "Notifications");
            d.Wait(() => Grid(form).Rows.Count > 0, "the channels");

            d.Dialog(() => Button(form, "Send test").PerformClick(), result =>
            {
                Assert.Equal("Send test", result.Text);
                Assert.StartsWith("1 channel(s) received the test.", Message(result), StringComparison.Ordinal);
            });

            Assert.True(received.Wait(TimeSpan.FromSeconds(10)), "The webhook never received the test.");
            Assert.StartsWith("POST /hook ", received.Result, StringComparison.Ordinal);

            // Nobody listening any more: the same button names the failure instead.
            sink.Stop();
            d.Dialog(() => Button(form, "Send test").PerformClick(), result =>
                Assert.Equal("1 channel(s) failed. Nothing arrived.", Message(result)));

            d.Dialog(() => Button(form, "Clear suppressed").PerformClick(), confirm =>
            {
                Assert.Equal("Clear suppressed", confirm.Text);
                Button(confirm, "Yes").PerformClick();
            });
            d.Wait(() => Says(form, "Suppressed channels cleared."), "the reset");

            Grid(form).Rows.Clear();
            d.Click(Button(form, "Refresh"));
            d.Wait(() => Grid(form).Rows.Count > 0, "the refreshed channels");
        });
    }

    /// <summary>Accepts one request, answers 200, and returns its request line and headers.</summary>
    private static Task<string> Receive(TcpListener sink) => Task.Run(async () =>
    {
        using var client = await sink.AcceptTcpClientAsync().ConfigureAwait(false);
        var stream = client.GetStream();
        var buffer = new byte[8192];
        var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
        await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray()).ConfigureAwait(false);
        return Encoding.ASCII.GetString(buffer, 0, read);
    });

    [Fact]
    public void ACredentialIsStoredThroughThePromptAndAMismatchIsRefused()
    {
        Sandbox.Require();
        using var box = Populated("secret", """

            [notify]
            to = ["email.ops"]

            [notify.email.ops]
            host = "smtp.contoso.example"
            port = 587
            tls = "required"
            auth = "login"
            username = "winlogrotate"
            password = "@secret:e2e-mail"
            from = "winlogrotate@contoso.example"
            to = ["ops@contoso.example"]

            """);

        ConsoleDriver.Run(d =>
        {
            using var form = d.Open(box.Config);
            d.Navigate(form, "Notifications");
            var providers = Grid(form);
            d.Wait(() => providers.Rows.Count > 0, "the providers");

            var provider = providers.Rows.Cast<DataGridViewRow>().First(r => r.Tag is string);
            providers.CurrentCell = provider.Cells[0];
            provider.Selected = true;

            // Cancel stores nothing and says nothing.
            d.Dialog(() => Button(form, "Set credential...").PerformClick(), prompt =>
            {
                Assert.Equal("Credential for email.ops", prompt.Text);
                Button(prompt, "Cancel").PerformClick();
            });

            // Nothing typed: refused once the prompt closes.
            d.Dialog(() => Button(form, "Set credential...").PerformClick(),
                prompt => Button(prompt, "Store").PerformClick(),
                refusal =>
                {
                    Assert.Equal("Credential", refusal.Text);
                    Assert.Equal("An empty value is not stored.", Message(refusal));
                });

            // Two different entries: refused once the prompt closes, and nothing is stored.
            d.Dialog(() => Button(form, "Set credential...").PerformClick(),
                prompt =>
                {
                    Assert.Equal("Credential for email.ops", prompt.Text);
                    var entries = All(prompt).OfType<TextBox>().ToArray();
                    Assert.Equal(2, entries.Length);
                    entries[0].Text = "not-a-real-secret";
                    entries[1].Text = "not-the-same-one";
                    Button(prompt, "Store").PerformClick();
                },
                refusal =>
                {
                    Assert.Equal("Credential", refusal.Text);
                    Assert.Equal("The two entries did not match.", Message(refusal));
                });
            Assert.Contains("password = \"@secret:e2e-mail\"", File.ReadAllText(box.ConfigFile), StringComparison.Ordinal);

            d.Dialog(() => Button(form, "Set credential...").PerformClick(),
                prompt =>
                {
                    foreach (var entry in All(prompt).OfType<TextBox>())
                    {
                        entry.Text = "not-a-real-secret";
                    }

                    Button(prompt, "Store").PerformClick();
                },
                stored =>
                {
                    Assert.Equal("Set credential", stored.Text);
                    Assert.Equal("Stored, and email.ops now refers to it rather than holding it.", Message(stored));
                });
        });

        // Through the elevated helper and its pipe, into this configuration's store.
        var name = Regex.Match(File.ReadAllText(box.ConfigFile), "password = \"@secret:([^\"]+)\"").Groups[1].Value;
        var test = box.Cli("secret", "test", name, "--json");
        Assert.True(test.ExitCode == 0, test.ToString());
    }

    [Fact]
    public void SetCredentialWithoutAProviderSaysToPickOne()
    {
        Sandbox.Require();
        using var box = Populated("nocred");

        ConsoleDriver.Run(d =>
        {
            using var form = d.Open(box.Config);
            d.Navigate(form, "Notifications");
            d.Settle();

            d.Dialog(() => Button(form, "Set credential...").PerformClick(), info =>
            {
                Assert.Equal("Set credential", info.Text);
                Assert.Equal("Select a provider row first.", Message(info));
            });
        });
    }

    [Fact]
    public void RepairPermissionsSecuresTheConfigurationDirectory()
    {
        Sandbox.Require();
        using var box = Populated("repair");

        ConsoleDriver.Run(d =>
        {
            using var form = d.Open(box.Config);
            d.Navigate(form, "Settings");
            d.Wait(Loaded(form, "Settings"), "the doctor report");

            d.Dialog(() => Button(form, "Repair permissions").PerformClick(), result =>
            {
                Assert.Equal("Permissions", result.Text);
                Assert.Equal("The configuration directory has been secured.", Message(result));
            });

            var report = All(form).OfType<TextBox>().First(t => t.Multiline);
            report.Clear();
            d.Click(Button(form, "Refresh"));
            d.Wait(() => report.Text.Length > 0, "the refreshed report");
        });

        var doctor = box.Cli("doctor", "--json");
        Assert.True(doctor.Result.GetProperty("hooksAllowed").GetBoolean(), doctor.ToString());
    }

    /// <summary>Switches the user's app theme to dark, so only where CI says the machine is the test's.</summary>
    [Fact]
    public void TheWindowOpensDarkWhenWindowsAppsAreDark()
    {
        Sandbox.Require();
        Assert.SkipUnless(Sandbox.MachineMayChange, "Switches the user's app theme: set WINLOGROTATE_E2E_MACHINE=1 on a throwaway machine.");
        using var box = Populated("dark");

        using var personalize = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        var before = personalize.GetValue("AppsUseLightTheme");
        personalize.SetValue("AppsUseLightTheme", 0, RegistryValueKind.DWord);

        try
        {
            ConsoleDriver.Run(d =>
            {
                using var form = d.Open(box.Config);
                d.Wait(Loaded(form, "Jobs"), "the Jobs page");
                Assert.True(Theme.IsDark, "The window opened light under a dark app theme.");
                Assert.Equal(ThemeColors.Dark.Window, form.BackColor);
            });
        }
        finally
        {
            if (before is null)
            {
                personalize.DeleteValue("AppsUseLightTheme", throwOnMissingValue: false);
            }
            else
            {
                personalize.SetValue("AppsUseLightTheme", before, RegistryValueKind.DWord);
            }
        }
    }

    /// <summary>Changes what runs rotations on this machine, so only where CI says the machine is the test's.</summary>
    [Fact]
    public void SchedulingRegistersTheTaskAndSwitchesBackToNothing()
    {
        Sandbox.Require();
        Assert.SkipUnless(Sandbox.MachineMayChange, "Registers a scheduled task: set WINLOGROTATE_E2E_MACHINE=1 on a throwaway machine.");
        using var box = Populated("schedule");

        ConsoleDriver.Run(d =>
        {
            using var form = d.Open(box.Config);
            d.Navigate(form, "Scheduling");
            d.Wait(Loaded(form, "Scheduling"), "the run host");

            var radios = All(form).OfType<RadioButton>().ToArray();
            var log = All(form).OfType<TextBox>().First(t => t.Multiline);

            radios.Single(r => r.Text.StartsWith("Scheduled task", StringComparison.Ordinal)).Checked = true;
            All(form).OfType<DateTimePicker>().Single().Value = DateTime.Today.AddHours(4).AddMinutes(15);
            d.Click(Button(form, "Apply"));
            d.Wait(() => Button(form, "Apply").Enabled && log.Lines.Length > 2, "the task to be registered", seconds: 60);
            Assert.Contains("Switching to: task, daily at 04:15", log.Text, StringComparison.Ordinal);

            var status = box.Cli("host", "status", "--json");
            Assert.Contains("04:15", status.StdOut, StringComparison.Ordinal);

            radios.Single(r => r.Text.StartsWith("Neither", StringComparison.Ordinal)).Checked = true;
            d.Click(Button(form, "Apply"));
            d.Wait(() => Button(form, "Apply").Enabled && log.Text.StartsWith("Switching to: none", StringComparison.Ordinal) && log.Lines.Length > 2, "the switch to none", seconds: 60);
            d.Wait(() => Says(form, "Currently:"), "the refreshed run host");
        });
    }
}
