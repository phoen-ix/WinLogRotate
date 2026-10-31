using WinLogRotate.Gui.Cli;
using WinLogRotate.Gui.Pages;

using Xunit;

using static WinLogRotate.Gui.Tests.ConsoleDriver;

namespace WinLogRotate.Gui.Tests.E2E;

/// <summary>The Jobs page and the job editor, driven through the window down to the files they write.</summary>
[Trait("Category", "E2E")]
[Collection(ConsoleCollection.Name)]
public sealed class JobJourneys
{
    private static string[] JobFiles(Sandbox box) => Directory.GetFiles(Path.Combine(box.Config, "conf.d"), "*.toml");

    [Fact]
    public void TheFirstJobIsAddedFromTheEmptyStateAndWritesItsFile()
    {
        Sandbox.Require();
        using var box = new Sandbox("add");
        box.Log();

        ConsoleDriver.Run(d =>
        {
            using var form = d.Open(box.Config);
            d.Wait(() => Button(form, JobsEmptyState.Add.Text, required: false) is not null, "the empty state");
            Assert.True(Says(form, JobsEmptyState.Sentence[..20]));

            d.Dialog(() => Button(form, JobsEmptyState.Add.Text).PerformClick(), dialog =>
            {
                var editor = Assert.IsType<JobEditor>(dialog);

                // Save with nothing filled in is refused in the window, before any verb runs.
                d.Click(Field<Button>(editor, "_save"));
                d.Wait(() => Field<Label>(editor, "_status").Text == JobEditorText.NoName, "the missing-name refusal");

                Field<TextBox>(editor, "_name").Text = "E2E app";
                d.Click(Field<Button>(editor, "_save"));
                d.Wait(() => Field<Label>(editor, "_status").Text == JobEditorText.NoFiles, "the missing-files refusal");

                Field<TextBox>(editor, "_files").Text = box.Glob();
                d.Wait(() => Field<Label>(editor, "_preview").Text.StartsWith("Matches 1 file", StringComparison.Ordinal), "the files preview");

                d.Click(Field<Button>(editor, "_save"));
                d.Wait(() => editor.DialogResult == DialogResult.OK, "the job to be written");
            });

            var jobs = Grid(form);
            d.Wait(() => Row(jobs, "E2E app") is not null, "the new job in the list");
            Assert.True(Says(form, JobEditorText.FirstJobFollowUp), "The first job's follow-up hint is missing.");
        });

        var file = Assert.Single(JobFiles(box));
        Assert.Contains("name = \"E2E app\"", File.ReadAllText(file), StringComparison.Ordinal);
        Assert.Contains(box.Glob(), File.ReadAllText(file), StringComparison.Ordinal);
        Assert.Equal(0, box.Cli("config", "check", "--json").ExitCode);
    }

    [Fact]
    public void EditingAJobChangesOnlyTheLineThatChanged()
    {
        Sandbox.Require();
        using var box = new Sandbox("edit");
        box.Log();
        var file = box.Job("app.toml", "App");
        var before = File.ReadAllText(file);

        ConsoleDriver.Run(d =>
        {
            using var form = d.Open(box.Config);
            var jobs = Grid(form);
            d.Wait(() => Row(jobs, "App") is not null, "the job");

            // The row says what the job is, where it looks, and what it keeps.
            var row = Row(jobs, "App")!;
            Assert.Equal("on", row.Cells["state"].Value);
            Assert.Equal("rotate", row.Cells["kind"].Value);
            Assert.Equal(box.Glob(), row.Cells["paths"].Value);
            Assert.Equal("daily, keep 3", row.Cells["policy"].Value);

            // A double-click opens the row under the pointer.
            d.Dialog(() => Raise(jobs, "OnCellDoubleClick", new DataGridViewCellEventArgs(0, Row(jobs, "App")!.Index)), dialog =>
            {
                var editor = Assert.IsType<JobEditor>(dialog);
                Assert.Equal("Job: App", editor.Text);

                var menu = Field<ListBox>(editor, "_menu");
                menu.SelectedIndex = menu.Items.Cast<object>().ToList()
                    .FindIndex(i => $"{i}".StartsWith(JobEditorModel.Menu(JobPageId.Keep), StringComparison.Ordinal));
                d.Wait(() => Says(editor, JobEditorModel.Title(JobPageId.Keep)), "the Keep section");

                var rotate = Field<TextBox>(editor, "_rotate");
                Assert.Equal("3", rotate.Text);
                rotate.Text = "5";

                d.Click(Field<Button>(editor, "_save"));
                d.Wait(() => editor.DialogResult == DialogResult.OK, "the change to be written");
            });

            d.Wait(() => Row(jobs, "App") is not null && Grid(form).Rows.Count == 1, "the list to reload");
        });

        Assert.Equal(before.Replace("rotate = 3", "rotate = 5", StringComparison.Ordinal), File.ReadAllText(file));
    }

    [Fact]
    public void TheEditorChecksAndPreviewsWithoutWritingAndCancelKeepsTheFile()
    {
        Sandbox.Require();
        using var box = new Sandbox("check");
        box.Log();
        var file = box.Job("app.toml", "App");
        var before = File.ReadAllText(file);

        ConsoleDriver.Run(d =>
        {
            using var form = d.Open(box.Config);
            var jobs = Grid(form);
            d.Wait(() => Row(jobs, "App") is not null, "the job");
            d.Select(jobs, "App");

            d.Dialog(() => Button(form, JobsToolbar.Edit.Text).PerformClick(), dialog =>
            {
                var editor = Assert.IsType<JobEditor>(dialog);

                // What would happen: a dry run of the job as it is on disk, in its own dialog.
                d.Dialog(() => Field<Button>(editor, "_whatWouldHappen").PerformClick(), preview =>
                {
                    Assert.Equal(JobEditorText.PreviewTitle, preview.Text);
                    Assert.StartsWith("As if App were due tonight: 1 rename", Message(preview), StringComparison.Ordinal);
                });

                // Check: the edit judged by the verb's dry run, and nothing written.
                Field<TextBox>(editor, "_rotate").Text = "9";
                var status = Field<Label>(editor, "_status");
                d.Click(Field<Button>(editor, "_check"));
                d.Wait(() => status.Text == "App would be accepted: 1 change. Nothing has been written.", "the check's verdict");
                Assert.Equal(before, File.ReadAllText(file));

                // An impossible value is refused by the verb, with its reasons.
                Field<TextBox>(editor, "_rotate").Text = "-4";
                d.Dialog(() => Field<Button>(editor, "_check").PerformClick(), problem =>
                {
                    Assert.Equal("Check", problem.Text);
                    Assert.NotNull(All(problem).OfType<LinkLabel>().SingleOrDefault());
                });

                // Cancel, by the button a user presses.
                Button(editor, "Cancel").PerformClick();
            });
        });

        Assert.Equal(before, File.ReadAllText(file));
    }

    [Fact]
    public void AJobIsSwitchedOffAndOnAgain()
    {
        Sandbox.Require();
        using var box = new Sandbox("toggle");
        box.Log();
        var file = box.Job("app.toml", "App");

        ConsoleDriver.Run(d =>
        {
            using var form = d.Open(box.Config);
            var jobs = Grid(form);
            d.Wait(() => Row(jobs, "App") is not null, "the job");

            // Nothing selected: the page says so rather than guessing.
            jobs.ClearSelection();
            d.Click(Button(form, JobsToolbar.Toggle.Text));
            Assert.True(Says(form, "Select a job first."));

            d.Select(jobs, "App");
            d.Click(Button(form, JobsToolbar.Toggle.Text));
            d.Wait(() => Row(Grid(form), "App")?.Cells["state"].Value as string == "disabled", "the job to show as disabled");
            Assert.Contains("enabled = false", File.ReadAllText(file), StringComparison.Ordinal);

            d.Select(Grid(form), "App");
            d.Click(Button(form, JobsToolbar.Toggle.Text));
            d.Wait(() => Row(Grid(form), "App")?.Cells["state"].Value as string == "on", "the job to show as on");
        });

        Assert.DoesNotContain("enabled = false", File.ReadAllText(file), StringComparison.Ordinal);
    }

    [Fact]
    public void RemovingAJobAsksFirstThenDeletesItsFile()
    {
        Sandbox.Require();
        using var box = new Sandbox("remove");
        var file = box.Job("app.toml", "App");

        ConsoleDriver.Run(d =>
        {
            using var form = d.Open(box.Config);
            var jobs = Grid(form);
            d.Wait(() => Row(jobs, "App") is not null, "the job");
            d.Select(jobs, "App");

            d.Dialog(() => Button(form, JobsToolbar.Remove.Text).PerformClick(), confirm =>
            {
                Assert.Equal("Remove job", confirm.Text);
                Assert.Contains("'App'", Message(confirm), StringComparison.Ordinal);
                Button(confirm, "No").PerformClick();
            });
            Assert.True(File.Exists(file), "No removed the job anyway.");

            d.Select(Grid(form), "App");
            d.Dialog(() => Button(form, JobsToolbar.Remove.Text).PerformClick(), confirm => Button(confirm, "Yes").PerformClick());
            d.Wait(() => Button(form, JobsEmptyState.Add.Text, required: false) is not null, "the empty state");
        });

        Assert.False(File.Exists(file));
    }

    [Fact]
    public void CheckConfigurationSaysCleanOrNamesTheProblems()
    {
        Sandbox.Require();
        using var box = new Sandbox("checkcfg");
        box.Log();
        box.Job("app.toml", "App");

        ConsoleDriver.Run(d =>
        {
            using var form = d.Open(box.Config);
            d.Wait(() => Row(Grid(form), "App") is not null, "the job");

            d.Dialog(() => Button(form, JobsToolbar.Check.Text).PerformClick(), check =>
            {
                Assert.Equal("Configuration", check.Text);
                Assert.Equal("No problems found.", Message(check));
            });

            // Broken behind the window's back, then Refresh: the list and the check both see it.
            box.Job("broken.toml", "Broken", "daily = true\nmaxage = -1\ncompresss = true\n");
            d.Click(Button(form, JobsToolbar.Refresh.Text));
            d.Wait(() => Grid(form).Rows.Count == 2 || Says(form, "Broken"), "the refreshed list");

            d.Dialog(() => Button(form, JobsToolbar.Check.Text).PerformClick(), check =>
            {
                Assert.Equal("Configuration", check.Text);
                Assert.StartsWith("One or more jobs have problems", Message(check), StringComparison.Ordinal);

                var details = All(check).OfType<LinkLabel>().Single();
                Raise(details, "OnLinkClicked", new LinkLabelLinkClickedEventArgs(details.Links[0]));
                d.Wait(() => details.Text == "Hide details", "the details");
                Assert.Contains("compresss", All(check).OfType<TextBox>().Single().Text, StringComparison.Ordinal);
            });
        });
    }
}
