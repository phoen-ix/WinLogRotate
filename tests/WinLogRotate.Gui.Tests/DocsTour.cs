using System.Drawing.Imaging;

using WinLogRotate.Gui.Cli;
using WinLogRotate.Gui.Pages;

using Xunit;

using static WinLogRotate.Gui.Tests.ConsoleDriver;

namespace WinLogRotate.Gui.Tests;

/// <summary>
/// Opens the real console on <see cref="DocsSeed"/>'s machine and saves every page and dialog the
/// docs show, as docs/screenshots/*.png.
/// </summary>
/// <remarks>
/// Opt-in: it runs only when DOCS_SCREENSHOT_DIR names where the PNGs go, which
/// scripts/docs-screenshots.sh sets. It is still a test - each shot first asserts that the page
/// shows what the image is supposed to show, and any dialog nobody asked for fails it
/// (<see cref="ConsoleDriver"/>).
/// </remarks>
[Trait("Category", "Docs")]
[Collection(ConsoleCollection.Name)]
public sealed class DocsTour
{
    private static readonly string? OutDir = Environment.GetEnvironmentVariable("DOCS_SCREENSHOT_DIR");

    private ITestOutputHelper? _log;

    [Fact]
    public void Screenshots()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(OutDir), "Set DOCS_SCREENSHOT_DIR to render the docs screenshots.");
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The console is WinForms and needs Windows.");

        _log = TestContext.Current.TestOutputHelper;
        Directory.CreateDirectory(OutDir!);
        var problem = DocsSeed.Build();

        ConsoleDriver.Run(Tour, TimeSpan.FromMinutes(6));
        Assert.Null(problem);

        DocsSeed.Clean();
    }

    private void Tour(ConsoleDriver d)
    {
        using (var form = d.Open(DocsSeed.EmptyConfig))
        {
            d.Wait(() => Button(form, JobsEmptyState.Add.Text, required: false) is not null, "the empty state");
            Shoot(form, "jobs-empty");
        }

        using (var form = d.Open(DocsSeed.ProblemConfig))
        {
            var jobs = Grid(form);
            d.Wait(() => jobs.Rows.Count == 2, "the jobs that still load");

            d.Dialog(() => Button(form, JobsToolbar.Check.Text).PerformClick(), check =>
            {
                Assert.Equal("Configuration", check.Text);

                // The details are what the picture is for. A LinkLabel has no PerformClick.
                var details = All(check).OfType<LinkLabel>().Single();
                Raise(details, "OnLinkClicked", new LinkLabelLinkClickedEventArgs(details.Links[0]));
                d.Wait(() => details.Text == "Hide details", "the details");
                Shoot(check, "jobs-check-problems");
            });
        }

        using (var form = d.Open(DocsSeed.Config))
        {
            var jobs = Grid(form);
            d.Wait(() => jobs.Rows.Count == 3 && Row(jobs, "Shop API") is not null, "the three jobs");
            Shoot(form, "jobs-list");

            d.Select(jobs, "Shop API");
            d.Dialog(() => Button(form, JobsToolbar.Remove.Text).PerformClick(), confirm =>
            {
                Assert.Equal("Remove job", confirm.Text);
                Shoot(confirm, "jobs-remove");
            });

            d.Navigate(form, "Run");
            Button(form, "Dry run").PerformClick();
            var output = All(form).OfType<TextBox>().First(t => t.Multiline);
            d.Wait(() => output.Text.Contains("dry run:", StringComparison.Ordinal), "the dry run's summary");
            Shoot(form, "run-dry-run");

            d.Navigate(form, "History");
            var history = Grid(form);
            d.Wait(() => history.Rows.Count > 0, "the journal");
            Shoot(form, "history-journal");

            d.Navigate(form, "Scheduling");
            d.Wait(() => All(form).OfType<Label>().Any(l => l.Visible && l.Text.StartsWith("Currently:", StringComparison.Ordinal)), "the run host");
            Shoot(form, "scheduling-task");

            d.Navigate(form, "Notifications");
            var providers = Grid(form);
            d.Wait(() => providers.Rows.Count > 0, "the providers");
            d.Wait(() => All(form).OfType<Label>().Any(l => l.Text.Contains("reported again after 7 days", StringComparison.Ordinal)), "the summary in words");
            Shoot(form, "notifications-providers");

            // A provider row, not a target row: only those carry a credential.
            var provider = providers.Rows.Cast<DataGridViewRow>().First(r => r.Tag is string);
            providers.CurrentCell = provider.Cells[0];
            provider.Selected = true;

            d.Dialog(() => Button(form, "Set credential...").PerformClick(), prompt =>
            {
                Assert.Equal("Credential for email.ops", prompt.Text);

                // Typed so the picture shows the masking; the dialog is cancelled, so nothing is stored.
                foreach (var box in All(prompt).OfType<TextBox>())
                {
                    box.Text = "not-a-real-secret";
                }

                d.Settle();
                Shoot(prompt, "notifications-credential");
            });

            d.Navigate(form, "Settings");
            var report = All(form).OfType<TextBox>().First(t => t.Multiline);
            d.Wait(() => report.Text.Length > 0, "the doctor report");
            Shoot(form, "settings-overview");

            // Last: opened straight after the first Jobs page loaded, before the Remove dialog, the
            // tour hung after this editor's shots for reasons never pinned down. Here it closes,
            // and the dialog watchdog above would dispose it if it ever stopped closing again.
            d.Navigate(form, "Jobs");
            jobs = Grid(form);
            d.Wait(() => jobs.Rows.Count == 3, "the jobs, for the editor");
            d.Select(jobs, "Shop API");
            d.Dialog(() => Button(form, JobsToolbar.Edit.Text).PerformClick(), editor =>
            {
                d.Wait(() => editor.Text == "Job: Shop API", "the editor");
                Shoot(editor, "jobs-editor");

                var menu = All(editor).OfType<ListBox>().First();
                var keep = menu.Items.Cast<object>().ToList().FindIndex(i => $"{i}".StartsWith(JobEditorModel.Menu(JobPageId.Keep), StringComparison.Ordinal));
                Assert.True(keep >= 0, "The editor has no Keep section.");
                menu.SelectedIndex = keep;
                d.Wait(() => All(editor).OfType<Label>().Any(l => l.Visible && l.Text == JobEditorModel.Title(JobPageId.Keep)), "the Keep section");
                Shoot(editor, "jobs-editor-keep");
            });
        }
    }

    /// <summary>
    /// Saves the window's client area. The frame is left out: drawn by WM_PRINT it is the classic
    /// frame, not the one Windows shows.
    /// </summary>
    private void Shoot(Form form, string name)
    {
        using var full = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(full, new Rectangle(Point.Empty, form.Size));

        var origin = form.PointToScreen(Point.Empty);
        var client = new Rectangle(origin.X - form.Left, origin.Y - form.Top, form.ClientSize.Width, form.ClientSize.Height);

        using var shot = full.Clone(client, full.PixelFormat);
        shot.Save(Path.Combine(OutDir!, name + ".png"), ImageFormat.Png);
        _log?.WriteLine($"shot {name}");
    }
}
