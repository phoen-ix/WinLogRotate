using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Shouldly;
using WinLogRotate.Contracts;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Safety;
using WinLogRotate.Core.Secrets;
using WinLogRotate.Hosting.Diagnostics;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// Rules that are easy to state, easy to break by accident, and invisible in review.
/// </summary>
public partial class ArchitectureTests
{
    [GeneratedRegex(@"\bConsole\s*\.\s*(Write|WriteLine|Error|Out|In|Read)", RegexOptions.Compiled)]
    private static partial Regex BareConsole();

    /// <summary>
    /// Nothing outside the output sinks may write to the console.
    /// <para>
    /// Every verb has to render three ways - human text, one JSON envelope, and an NDJSON
    /// stream the GUI tails - and a single stray Console.WriteLine corrupts two of them. In
    /// the JSON case it is worse than cosmetic: an unexpected line makes the whole envelope
    /// unparseable, so the GUI reports a broken CLI rather than the actual result.
    /// </para>
    /// </summary>
    [Fact]
    public void OnlyTheOutputSinksTouchTheConsole()
    {
        var cli = Path.Combine(RepoRoot.Find().FullName, "src", "WinLogRotate.Cli");

        var offenders = Directory
            .EnumerateFiles(cli, "*.cs", SearchOption.AllDirectories)
            .Where(f => !Path.GetDirectoryName(f)!.EndsWith("Output", StringComparison.Ordinal))
            .Where(f => !f.Contains(Path.Combine("obj", ""), StringComparison.Ordinal))
            .Select(f => (File: Path.GetFileName(f), Text: File.ReadAllText(f)))
            .Where(x => BareConsole().IsMatch(x.Text))
            .Select(x => x.File)
            .ToArray();

        offenders.ShouldBeEmpty(
            "everything user-facing goes through IOutputSink; see WinLogRotate.Cli/Output");
    }

    /// <summary>
    /// The GUI must never call <c>MessageBox</c>.
    /// <para>
    /// It renders light regardless of Application.SetColorMode, so a single call ruins a
    /// dark-themed window - and it has never had a way to copy the error text out, which is
    /// the first thing anyone needs when reporting a problem. LrDialog replaces it and adds
    /// both. This is exactly the kind of rule that is obvious in review and forgotten in the
    /// one hurried fix, so it is enforced.
    /// </para>
    /// </summary>
    [Fact]
    public void TheGuiNeverCallsMessageBox()
    {
        var gui = Path.Combine(RepoRoot.Find().FullName, "src", "WinLogRotate.Gui");

        var offenders = Directory
            .EnumerateFiles(gui, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.Combine("obj", ""), StringComparison.Ordinal))
            .Where(f => File.ReadAllLines(f).Any(line =>
                line.Contains("MessageBox.Show", StringComparison.Ordinal)))
            .Select(Path.GetFileName)
            .ToArray();

        offenders.ShouldBeEmpty("use LrDialog instead - see WinLogRotate.Gui/Ui/LrDialog.cs");
    }

    /// <summary>
    /// Every verb in the tree is invoked through the guard.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An exception escaping a verb used to reach System.CommandLine's default handler, which
    /// returned exit 1 - the code that says a run happened and state was written - with no
    /// envelope and the <c>--output</c> file still open. <c>CommandContext.Guarded</c> is what
    /// makes that impossible, and it only helps the actions that go through it.
    /// </para>
    /// <para>
    /// <c>CommandContext.From</c> being private means the compiler already enforces this, which
    /// is the stronger guarantee. This is here for the case the compiler cannot see: an action
    /// that builds no context at all, and so reports nothing when it throws.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryVerbInTheTreeIsInvokedThroughTheGuard()
    {
        var tree = File.ReadAllText(Path.Combine(
            RepoRoot.Find().FullName, "src", "WinLogRotate.Cli", "Commands", "CommandTree.cs"));

        var actions = tree.Split(".SetAction(", StringSplitOptions.None).Skip(1).ToArray();

        // Self-check: a rule about the tree's actions means nothing if it stopped finding any.
        actions.Length.ShouldBeGreaterThan(20);

        actions
            .Where(a => !a.StartsWith("parse => CommandContext.Guarded(", StringComparison.Ordinal))
            .ShouldBeEmpty("every action must run inside CommandContext.Guarded");
    }

    /// <summary>
    /// Nothing says <c>host status</c> reads the journal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It does not. <c>HostCommand.Status</c> asks the run host what is registered and reads no
    /// journal file at all. The claim had reached four files and one documentation page, because
    /// each was a verbatim copy of the sentence before it - fixing two and leaving three is how
    /// it got there.
    /// </para>
    /// <para>
    /// Sentence-scoped, and it needs the reading verb: "results surface in the Event Log, the
    /// journal and <c>host status</c>" is true and names both, and a rule that could not tell
    /// the two apart would push people into wording their comments around the test.
    /// </para>
    /// </remarks>
    [Fact]
    public void NothingSaysHostStatusReadsTheJournal()
    {
        var root = RepoRoot.Find().FullName;

        var files = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.Combine("obj", ""), StringComparison.Ordinal))
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "docs"), "*.md"))
            .Append(Path.Combine(root, "README.md"))
            .ToArray();

        // Self-check: several files name the verb, so the needle is live.
        files.Count(f => File.ReadAllText(f).Contains("host status", StringComparison.Ordinal))
            .ShouldBeGreaterThan(4);

        bool Claims(string path)
        {
            var prose = File.ReadAllText(path)
                .Replace("///", " ", StringComparison.Ordinal)
                .Replace("//", " ", StringComparison.Ordinal)
                .Replace("<c>", "", StringComparison.Ordinal)
                .Replace("</c>", "", StringComparison.Ordinal)
                .Replace('\n', ' ')
                .Replace("`", "", StringComparison.Ordinal);

            return prose.Split('.').Any(sentence =>
                sentence.Contains("host status", StringComparison.OrdinalIgnoreCase)
                && sentence.Contains("journal", StringComparison.OrdinalIgnoreCase)
                && sentence.Contains("read", StringComparison.OrdinalIgnoreCase));
        }

        files.Where(Claims).Select(Path.GetFileName).ShouldBeEmpty(
            "host status reports what is registered; it reads no journal");
    }

    /// <summary>
    /// The journal's contract describes the journal.
    /// </summary>
    /// <remarks>
    /// The half a sentence-scoped rule cannot reach. <c>IJournal</c> said the GUI and
    /// <c>host status</c> "never read Last Run Result" - true - and then, in the next sentence,
    /// "The journal does", which implies something that is not. Listing a type's readers is a
    /// fact about those readers, and keeping such a list in the contract is how it came to hold
    /// one that was wrong.
    /// </remarks>
    [Fact]
    public void TheJournalContractDescribesTheJournal()
    {
        var root = RepoRoot.Find().FullName;

        // Self-check: the needle is a live string somewhere else in the tree.
        File.ReadAllText(Path.Combine(root, "src", "WinLogRotate.Cli", "Commands", "HostCommand.cs"))
            .ShouldContain("host status");

        File.ReadAllText(Path.Combine(root, "src", "WinLogRotate.Core", "Journaling", "IJournal.cs"))
            .ShouldNotContain("host status", Case.Sensitive,
                "the journal's contract is about the journal, not about who reads it");
    }

    /// <summary>
    /// And the fact both documents were wrong about, made checkable.
    /// </summary>
    /// <remarks>
    /// Method-scoped, because <c>host path</c> legitimately names the journal directory a few
    /// hundred lines away - a file-level needle would be a false positive on its neighbour.
    /// </remarks>
    [Fact]
    public void HostStatusReadsNoJournal()
    {
        var file = File.ReadAllText(Path.Combine(
            RepoRoot.Find().FullName, "src", "WinLogRotate.Cli", "Commands", "HostCommand.cs"));

        var status = file
            .Split("public static int ", StringSplitOptions.None)
            .Where(chunk => chunk.StartsWith("Status(", StringComparison.Ordinal))
            .ToArray();

        var body = status.ShouldHaveSingleItem();

        body.ShouldContain("TaskRunHost", Case.Sensitive, "its whole data source");
        body.ShouldNotContain("JournalReader", Case.Sensitive);
    }

    /// <summary>
    /// The elevation question is asked in exactly two places.
    /// </summary>
    /// <remarks>
    /// One is the start-up banner, which says a read-only view is a supported way to run. The
    /// other is the UAC shield, which means "pressing this raises a prompt" and must therefore
    /// not be drawn when this process is already elevated. Both are statements about this
    /// window's own token.
    /// <para>
    /// Asserted as an equality rather than a containment, so it pins both halves: that the rule
    /// exists in <c>LrDialog</c>, and that no page re-derives it. A page deciding for itself
    /// whether to draw a shield is how six call sites came to draw one unconditionally.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheElevationQuestionIsAskedInExactlyTwoPlaces()
    {
        var gui = Path.Combine(RepoRoot.Find().FullName, "src", "WinLogRotate.Gui");

        Directory
            .EnumerateFiles(gui, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.Combine("obj", ""), StringComparison.Ordinal))
            .Where(f => File.ReadAllLines(f)
                .Where(line => !line.TrimStart().StartsWith("///", StringComparison.Ordinal))
                .Any(line => line.Contains("Privilege.IsElevated()", StringComparison.Ordinal)))
            .Select(Path.GetFileName)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ShouldBe(["LrDialog.cs", "MainForm.cs"]);
    }

    /// <summary>
    /// A payload does not claim a reader it does not have.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Five doc comments in <c>Results.cs</c> named the GUI as a reader of a field the GUI never
    /// read. <c>VersionResult</c> said "the GUI reads this on start" while no handshake existed at
    /// all; <c>Elevated</c> said it decided whether a button needed a shield while every shield
    /// was drawn unconditionally; <c>ConfigDiagnosticDto</c> offered file, line and column for a
    /// page that runs <c>config check</c> without <c>--json</c>; and <c>ProbeResultDto</c> offered
    /// a value to preselect for a verb the window never runs.
    /// </para>
    /// <para>
    /// So: naming the GUI in a payload's documentation commits that payload to being read by it.
    /// The GUI hand-walks every envelope, so "read" means the property's camelCase name appears
    /// as a literal in one of the two GUI projects.
    /// </para>
    /// <para>
    /// Record-level, and therefore weaker than it looks: a record passes if <i>any</i> of its
    /// properties is read, and the name could be one the GUI reads from a different verb's
    /// payload entirely. Stated rather than glossed - the same caveat
    /// <c>EveryDiagnosticCodeIsRaisedBySomethingUnderSrc</c> makes about itself.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryResultThatNamesTheGuiIsReadByTheGui()
    {
        var root = RepoRoot.Find().FullName;

        var gui = new[] { "WinLogRotate.Gui", "WinLogRotate.Gui.Model" }
            .SelectMany(p => Directory.EnumerateFiles(
                Path.Combine(root, "src", p), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains(Path.Combine("obj", ""), StringComparison.Ordinal))
            .Select(File.ReadAllText)
            .ToArray();

        var lines = File.ReadAllLines(
            Path.Combine(root, "src", "WinLogRotate.Cli", "Output", "Results.cs"));

        var doc = new List<string>();
        var records = new List<(string Name, string Doc, List<string> Body)>();
        (string Name, string Doc, List<string> Body)? open = null;

        foreach (var line in lines)
        {
            if (open is { } current)
            {
                if (line == "}")
                {
                    records.Add(current);
                    open = null;
                }
                else
                {
                    current.Body.Add(line);
                }

                continue;
            }

            var declared = Regex.Match(line, @"^public sealed record (\w+)");

            if (declared.Success)
            {
                open = (declared.Groups[1].Value, string.Join('\n', doc), []);
                doc.Clear();
                continue;
            }

            if (line.TrimStart().StartsWith("///", StringComparison.Ordinal))
            {
                doc.Add(line);
            }
            else if (line.Trim().Length > 0)
            {
                doc.Clear();
            }
        }

        // Self-check: the rule is about the records in this file, and there are a lot of them.
        records.Count.ShouldBeGreaterThan(20);

        var claimants = records
            .Where(r => (r.Doc + string.Join('\n', r.Body))
                .Contains("the GUI", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        claimants.Length.ShouldBeGreaterThan(1, "some payloads do name the GUI");

        foreach (var (name, _, body) in claimants)
        {
            var properties = body
                .Select(l => Regex.Match(l, @"public (?:required )?[\w<>?\[\].]+ (\w+) \{ get;"))
                .Where(m => m.Success)
                .Select(m => m.Groups[1].Value)
                .Select(n => char.ToLowerInvariant(n[0]) + n[1..])
                .ToArray();

            properties.ShouldNotBeEmpty($"{name} has no properties this rule can check");

            properties
                .Any(n => gui.Any(f => f.Contains($"\"{n}\"", StringComparison.Ordinal)))
                .ShouldBeTrue($"{name} names the GUI, and the GUI reads none of its fields");
        }
    }

    /// <summary>
    /// Every page that runs the CLI reports a defect.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Milestone 21 gave the CLI a way to say "this is a bug in me, and nothing about what was or
    /// was not done can be relied on": an LR1006 diagnostic and exit 4. Eleven of the twelve
    /// unelevated call sites then set a status label and returned, or discarded the failure
    /// outright - so the thing the CLI had gone to trouble to say arrived nowhere.
    /// </para>
    /// <para>
    /// Scoped to <c>Pages/</c>. <c>MainForm</c> runs the CLI too, but its one invocation is the
    /// identity probe, whose failures are already a banner; a dialog there would be a second
    /// report of the same thing before the window has even finished opening.
    /// </para>
    /// <para>
    /// A text scan, because a test project that references the GUI carries a
    /// Microsoft.WindowsDesktop.App framework reference and cannot run on this leg.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryPageThatRunsTheCliReportsADefect()
    {
        var pages = Path.Combine(RepoRoot.Find().FullName, "src", "WinLogRotate.Gui", "Pages");

        var runners = Directory
            .EnumerateFiles(pages, "*.cs", SearchOption.AllDirectories)
            .Where(f => File.ReadAllLines(f)
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                .Any(line =>
                    line.Contains("RunAsync(", StringComparison.Ordinal)
                    || line.Contains("RunElevatedAsync(", StringComparison.Ordinal)))
            .ToArray();

        // Self-check: a rule about the pages that run the CLI means nothing once none appear to.
        runners.Length.ShouldBeGreaterThan(4);

        runners
            .Where(f => !File.ReadAllText(f).Contains("IsDefect", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ShouldBeEmpty("a page that runs the CLI must say so when the CLI reports a defect");
    }

    /// <summary>
    /// Every claim that the GUI checks the contract schema names the code that does it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four places said the GUI compared the envelope's schema on start, and none of them was
    /// true: a grep for "Schema" across both GUI projects returned nothing. One of the four was
    /// not even a comment - <c>CommandTree</c> removes System.CommandLine's built-in version
    /// option so that <c>--version --json</c> emits an envelope, real code written for a reader
    /// that did not exist.
    /// </para>
    /// <para>
    /// A claim about a reader must name the reader. That is checkable, where "is this sentence
    /// true" is not, and it is what would have caught the original defect: four files naming a
    /// behaviour, no file implementing it.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryClaimThatTheGuiChecksTheContractSchemaNamesTheCodeThatDoes()
    {
        var root = RepoRoot.Find().FullName;

        string[] claimants =
        [
            Path.Combine(root, "src", "WinLogRotate.Core", "ProductInfo.cs"),
            Path.Combine(root, "src", "WinLogRotate.Contracts", "CliEnvelope.cs"),
            Path.Combine(root, "src", "WinLogRotate.Cli", "Output", "Results.cs"),
            Path.Combine(root, "src", "WinLogRotate.Cli", "Commands", "CommandTree.cs"),
        ];

        claimants
            .Where(f => !File.ReadAllText(f).Contains("CliIdentity", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ShouldBeEmpty("a claim that the GUI checks the schema must name CliIdentity");

        // And the window must CALL it, in code. Naming it was not enough: the first version of
        // this rule asked only that MainForm.cs contained the string, and the file mentions
        // CliIdentityTests in a comment - so the window could stop comparing altogether and the
        // prose would keep this green. Comment lines are stripped for the same reason
        // NothingMarshalsToTheUiThreadByHand strips them.
        var window = File.ReadAllLines(
                Path.Combine(root, "src", "WinLogRotate.Gui", "MainForm.cs"))
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .Where(line => !line.TrimStart().StartsWith("///", StringComparison.Ordinal));

        window.Any(line => line.Contains("CliIdentity.Inspect", StringComparison.Ordinal))
            .ShouldBeTrue("MainForm must actually run the check, not merely mention it");

        // The check reports and carries on. The sentence that said otherwise described a feature
        // nobody had written, and implementing it as written would have bricked the window on a
        // false positive.
        Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.Combine("obj", ""), StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("refuses to talk", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ShouldBeEmpty("the GUI reports a mismatch; it does not refuse to talk");
    }

    /// <summary>
    /// The rotation gate asks for exactly the rights it grants.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It did not. The descriptor granted Everyone <c>Synchronize | Modify</c> and the create
    /// asked for full control, which that grants to nobody - so the first caller made the gate
    /// and every concurrent second caller was refused by it. One constant used by both sides is
    /// what makes the two unable to disagree, and a second spelling of the rights anywhere in
    /// the file is that disagreement coming back.
    /// </para>
    /// <para>
    /// A text scan, and a proxy: the behaviour itself is asserted by
    /// <c>RotationGateTests</c>, which needs <c>MutexAcl</c> and therefore real Windows. This is
    /// the part of it that can be checked on the leg that runs everywhere.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheRotationGateAsksForExactlyTheRightsItGrants()
    {
        var gate = File.ReadAllText(Path.Combine(
            RepoRoot.Find().FullName, "src", "WinLogRotate.Hosting", "RotationGate.cs"));

        // Self-check: a scan for a spelling that is no longer used anywhere would pass for the
        // wrong reason the moment someone renamed it.
        gate.ShouldContain("MutexRights.Synchronize | MutexRights.Modify");

        gate.Split("MutexRights.Synchronize | MutexRights.Modify").Length.ShouldBe(
            2,
            "the rights belong in one constant, used by both the descriptor and the open");

        gate.ShouldContain(
            "TryOpenExisting",
            Case.Sensitive,
            "the gate must join an existing mutex rather than asking to create one it may not own");
    }

    /// <summary>
    /// Nothing marshals to the UI thread by hand.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>InvokeRequired</c> is only meaningful once a handle exists: it walks up the parent
    /// chain looking for a control with a created handle and answers <c>false</c> when it finds
    /// none. So the natural-looking guard - <c>if (InvokeRequired) BeginInvoke(...)</c> - sends a
    /// detached control's work down the "already on the UI thread" branch and runs it on
    /// whichever thread produced it. <c>MainForm.ShowPage</c> detaches a page on every
    /// navigation, and a rotation's line callbacks arrive from a thread-pool thread, so that is
    /// an ordinary sequence rather than a corner.
    /// </para>
    /// <para>
    /// A text scan, because the helper touches <c>Control</c> and so cannot leave the WinForms
    /// project, and a test project that references that project cannot run on the Linux leg. The
    /// file set is derived rather than listed, so a new hand-rolled site turns this red.
    /// </para>
    /// </remarks>
    [Fact]
    public void NothingMarshalsToTheUiThreadByHand()
    {
        var gui = Path.Combine(RepoRoot.Find().FullName, "src", "WinLogRotate.Gui");
        var helper = Path.Combine(gui, "Ui", "UiThread.cs");

        File.Exists(helper).ShouldBeTrue("the one place allowed to do this must exist");

        var offenders = Directory
            .EnumerateFiles(gui, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.Combine("obj", ""), StringComparison.Ordinal))
            .Where(f => !string.Equals(f, helper, StringComparison.Ordinal))
            .Where(f => File.ReadAllLines(f)
                // Prose about the hazard is not the hazard. Only code counts.
                .Where(line => !line.TrimStart().StartsWith("///", StringComparison.Ordinal))
                .Any(line =>
                    line.Contains("BeginInvoke", StringComparison.Ordinal)
                    || line.Contains("InvokeRequired", StringComparison.Ordinal)))
            .Select(Path.GetFileName)
            .ToArray();

        offenders.ShouldBeEmpty(
            "post through WinLogRotate.Gui/Ui/UiThread.cs - InvokeRequired alone is not a guard");
    }

    /// <summary>
    /// A page that walks an envelope by hand catches every way that walk can fail.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>GetProperty</c> throws <c>KeyNotFoundException</c> and <c>GetInt32</c> throws
    /// <c>InvalidOperationException</c>. Neither is a <c>JsonException</c>, and every page but
    /// one caught only that - so a response that parsed but was missing a field escaped from an
    /// <c>async void</c> handler, in a process that installs no
    /// <c>Application.ThreadException</c> handler at all. A CLI one version out of step is the
    /// ordinary way to produce exactly that.
    /// </para>
    /// <para>
    /// A text scan, because a test project that references the GUI carries a
    /// Microsoft.WindowsDesktop.App framework reference and cannot run on the Linux leg. The
    /// rule is deliberately narrow - it does not forbid hand-walking, which would force all five
    /// pages' projections out at once - so it asks only that a page which does it says so in its
    /// catch.
    /// </para>
    /// </remarks>
    [Fact]
    public void APageThatWalksAnEnvelopeCatchesEveryWayItCanFail()
    {
        var pages = Path.Combine(RepoRoot.Find().FullName, "src", "WinLogRotate.Gui", "Pages");

        var walkers = Directory
            .EnumerateFiles(pages, "*.cs", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains("GetProperty(", StringComparison.Ordinal))
            .ToArray();

        // Self-check: a rule about pages that hand-walk an envelope means nothing once none do.
        walkers.Length.ShouldBeGreaterThan(2);

        walkers
            .Where(f => File.ReadAllText(f).Contains("catch (JsonException", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ShouldBeEmpty(
                "a page that walks an envelope by hand must also catch KeyNotFoundException and "
                + "InvalidOperationException - see NotificationsPage for the filter");
    }

    /// <summary>
    /// Every capability must be reachable from the command line.
    /// <para>
    /// WinForms does not run on Server Core, which is where IIS is most often installed. One
    /// GUI-only feature therefore strands exactly the users who need this tool most, with no
    /// workaround at all. The GUI shelling out for everything is what keeps that true, so this
    /// asserts it structurally: the GUI reaches the engine only through CliRunner.
    /// </para>
    /// </summary>
    [Fact]
    public void TheGuiReachesTheEngineOnlyThroughTheCommandLine()
    {
        var gui = Path.Combine(RepoRoot.Find().FullName, "src", "WinLogRotate.Gui");

        var offenders = Directory
            .EnumerateFiles(gui, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.Combine("obj", ""), StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f) is var text
                        && (text.Contains("RotationRunner", StringComparison.Ordinal)
                            || text.Contains("PlanExecutor", StringComparison.Ordinal)
                            || text.Contains("ManageJobPlanner", StringComparison.Ordinal)
                            || text.Contains("RotateJobPlanner", StringComparison.Ordinal)))
            .Select(Path.GetFileName)
            .ToArray();

        offenders.ShouldBeEmpty(
            "the GUI must shell out to winlogrotate.exe rather than driving the engine directly, "
            + "so that every capability stays reachable on Server Core where WinForms cannot run");
    }

    /// <summary>
    /// Only a small, named set of files may read a secret's value.
    /// <para>
    /// <c>SecretString.Reveal</c> is deliberately ugly so that writing it feels like a
    /// decision rather than a getter, and this is what makes that more than a convention. The
    /// value has exactly one legitimate destination - the transport that authenticates with it
    /// - and every other caller is a leak waiting to be discovered in a support bundle.
    /// </para>
    /// <para>
    /// The allowlist is spelled out rather than derived from a folder, because "anything under
    /// Secrets/ may reveal" is the rule that stops meaning anything the first time somebody
    /// adds a convenience helper there.
    /// </para>
    /// </summary>
    [Fact]
    public void OnlyTheAllowlistedFilesRevealASecret()
    {
        string[] allowed =
        [
            // Where a secret is encrypted and decrypted.
            "SecretStore.cs",
            // The type itself.
            "SecretString.cs",
            // The tests that prove the redaction works, and that a stored secret round-trips.
            "SecretStringTests.cs",
            "SecretStoreTests.cs",
            "SecretsTests.cs",
            "SecretCommandTests.cs",
            "NotifyBindingTests.cs",

            // Added in milestone 11: the pipe tests assert the value that ARRIVED, not its
            // length. A length-only assertion passes for a frame misaligned by a byte, which is
            // the defect most worth catching in a channel that carries a password.
            "SecretInputTests.cs",

            // The three transports that authenticate, added deliberately in milestone 10. Each
            // reveals exactly one thing at the moment it needs it: the webhook URL (whose entropy
            // is in its path, which is why it is a credential at all), the Pushover application
            // token and user key, and the SMTP password. Nothing else in the delivery path calls
            // Reveal - the dispatcher, the composer and the channel resolver all pass SecretString
            // around unopened, which is what keeps this list to three.
            "HttpNotifySender.cs",
            "PushoverNotifySender.cs",
            "SmtpNotifySender.cs",
        ];

        // Assembled rather than written out, so that this file - which scans every file under
        // src/ and tests/, including itself - does not match its own search string. The
        // alternative, excluding this file, would make it the one place a violation could hide.
        var needle = "." + "Reveal" + "()";

        var root = RepoRoot.Find().FullName;

        var offenders = new[] { "src", "tests" }
            .Select(d => Path.Combine(root, d))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains(Path.Combine("obj", ""), StringComparison.Ordinal))
            .Where(f => !f.Contains(Path.Combine("bin", ""), StringComparison.Ordinal))
            .Where(f => !allowed.Contains(Path.GetFileName(f), StringComparer.Ordinal))
            .Where(f => File.ReadAllText(f).Contains(needle, StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToArray();

        offenders.ShouldBeEmpty(
            "a secret's value has one destination - the transport that authenticates with it. "
            + "If a new transport needs it, add that file to the allowlist deliberately.");
    }

    /// <summary>
    /// Every identity the product publishes is declared in exactly one place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The publisher, the copyright holder and the author are claims about a real person or
    /// organisation. They belong in LICENSE, Directory.Build.props and the installer script, and
    /// nowhere else - so that changing who owns this is one edit rather than a search.
    /// </para>
    /// <para>
    /// This test exists because an identity was once derived from the working directory path and
    /// written into all three, where it survived twenty-one commits and five releases before
    /// anybody noticed. It was compiled into both executables' file properties and shown in
    /// Add/Remove Programs on every machine that installed it. Nothing was watching, which is the
    /// only reason it lasted.
    /// </para>
    /// </remarks>
    [Fact]
    public void ThePublishedIdentityIsDeclaredInOnePlaceOnly()
    {
        var root = RepoRoot.Find().FullName;

        // The four places the owner is named on purpose, each for a different reason. Anywhere
        // else is a copy, and a copy is what goes stale or turns out to have been wrong all along.
        string[] declarations =
        [
            // The legal attribution.
            Path.Combine(root, "LICENSE"),

            // Company, Authors and Copyright, compiled into both executables' file properties.
            Path.Combine(root, "Directory.Build.props"),

            // The Publisher shown in Add/Remove Programs.
            Path.Combine(root, "packaging", "winlogrotate.nsi"),

            // The release feed the updater polls, which is necessarily an address under the
            // owner's account.
            Path.Combine(root, "src", "WinLogRotate.Cli", "Commands", "UpdateCommand.cs"),
        ];

        // Read from the declaration rather than hard-coded, so this keeps working when the owner
        // changes and fails when a copy of the old value is left behind somewhere.
        var props = File.ReadAllText(Path.Combine(root, "Directory.Build.props"));
        var company = System.Text.RegularExpressions.Regex
            .Match(props, @"<Company>([^<]+)</Company>").Groups[1].Value;

        company.ShouldNotBeNullOrWhiteSpace("Directory.Build.props must declare a Company");

        var offenders = new[] { "src", "tests", "docs", ".github" }
            .Select(d => Path.Combine(root, d))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
            .Where(f => !f.Contains(Path.Combine("obj", ""), StringComparison.Ordinal))
            .Where(f => !f.Contains(Path.Combine("bin", ""), StringComparison.Ordinal))
            .Where(f => !declarations.Contains(f, StringComparer.Ordinal))
            .Where(f => File.ReadAllText(f).Contains(company, StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetRelativePath(root, f))
            .ToArray();

        offenders.ShouldBeEmpty(
            $"'{company}' identifies the owner and belongs only in LICENSE, Directory.Build.props "
            + "and the installer script. A copy anywhere else is one more thing to miss when it "
            + "changes, and one more place for a wrong value to hide.");
    }

    /// <summary>
    /// The engine's platform-neutral half must stay neutral.
    /// <para>
    /// Marking the whole Core assembly [SupportedOSPlatform("windows")] is the tempting
    /// shortcut - it silences CA1416 in one line. It also makes CA1416 fire on every call
    /// site in the neutral CLI and test projects, and it strands the pure suite, which runs
    /// on Linux precisely because glob matching, schedule arithmetic and version comparison
    /// genuinely are portable. Annotate the Win32 types instead.
    /// </para>
    /// </summary>
    [Fact]
    public void CoreIsNotMarkedWindowsOnlyAtAssemblyScope()
    {
        var core = Path.Combine(RepoRoot.Find().FullName, "src", "WinLogRotate.Core");

        var offenders = Directory
            .EnumerateFiles(core, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.Combine("obj", ""), StringComparison.Ordinal))
            .Where(f => File.ReadAllLines(f).Any(l =>
                l.TrimStart().StartsWith("[assembly: SupportedOSPlatform", StringComparison.Ordinal)))
            .Select(Path.GetFileName)
            .ToArray();

        offenders.ShouldBeEmpty();
    }

    [GeneratedRegex(@"^\|\s*(\d+)\s*\|[^|]*\|[^|]*\|\s*`(LR\d{4})`\s*\|", RegexOptions.Compiled)]
    private static partial Regex DocumentedDiagnostic();

    /// <summary>
    /// Every row of the published diagnostics table names a real code, at its real event ID.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>docs/diagnostics.md</c> calls itself a contract, and an alert rule cannot exist without
    /// a documented event ID. So the table has to be bound to the code rather than maintained
    /// beside it: a renumbering that breaks the build is a nuisance, and one that breaks somebody's
    /// alert rule six months later is not discovered at all.
    /// </para>
    /// <para>
    /// Severity is deliberately not asserted. The document says out loud that the Type column is
    /// contextual - <c>LR3003</c> is legitimately Error, Warning and Info depending on
    /// circumstance - so a test on it would fail on documented, deliberate behaviour.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryDiagnosticsDocRowNamesARealCodeAndItsRealEventId()
    {
        var doc = Path.Combine(RepoRoot.Find().FullName, "docs", "diagnostics.md");

        var rows = File.ReadAllLines(doc)
            .Select(line => DocumentedDiagnostic().Match(line))
            .Where(m => m.Success)
            .Select(m => (Id: int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), Code: m.Groups[2].Value))
            .ToArray();

        // A regex that silently stopped matching would make everything below pass while asserting
        // nothing, which is the failure this whole file exists to prevent.
        rows.Length.ShouldBeGreaterThan(25);

        var declared = typeof(DiagnosticCode)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

        rows.Select(r => r.Code).Where(c => !declared.Contains(c))
            .ShouldBeEmpty("documented codes that no longer exist");

        declared.Where(c => rows.All(r => r.Code != c))
            .ShouldBeEmpty("codes with no row in docs/diagnostics.md");

        rows.Where(r => EventIds.For(r.Code) != r.Id)
            .Select(r => $"{r.Code} is documented as {r.Id} but maps to {EventIds.For(r.Code)}")
            .ShouldBeEmpty();
    }

    [GeneratedRegex(@"\bOption<[^>\n]+>\s+(\w+)\s*=", RegexOptions.Compiled)]
    private static partial Regex OptionDeclaration();

    /// <summary>
    /// Every option the command tree declares is read by it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An option nothing reads is a lie in <c>--help</c>: it parses, it is accepted, it is
    /// documented, and it changes nothing. This found three - <c>import --probe</c>,
    /// <c>scan --deep</c> and <c>update apply --yes</c>, the last attached to a verb that installs
    /// nothing at all.
    /// </para>
    /// <para>
    /// <b>The allowlist is empty and should stay that way.</b> The fix for an unread option is to
    /// read it or delete it, never to annotate it.
    /// </para>
    /// <para>
    /// Unlike <c>OnlyTheAllowlistedFilesRevealASecret</c> this reads a single file rather than a
    /// tree, so it cannot match itself and needs none of that test's runtime-assembled needle.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryOptionDeclaredOnTheCommandTreeIsReadByIt()
    {
        var tree = Path.Combine(
            RepoRoot.Find().FullName, "src", "WinLogRotate.Cli", "Commands", "CommandTree.cs");

        var text = File.ReadAllText(tree);

        var declared = OptionDeclaration().Matches(text)
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // A regex that stopped matching would make this pass while asserting nothing.
        declared.Length.ShouldBeGreaterThan(10);

        // GetRequiredValue as well, so an option becoming required does not read as unused.
        declared
            .Where(name => !text.Contains($"GetValue({name})", StringComparison.Ordinal)
                        && !text.Contains($"GetRequiredValue({name})", StringComparison.Ordinal))
            .ShouldBeEmpty("options the command tree declares and never reads");
    }

    /// <summary>
    /// Every diagnostic code is raised by something under <c>src/</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A code with an event ID, a documented row and no emitter is an alert rule that can never
    /// fire. This found three - <c>LR4002</c>, <c>LR2003</c> and <c>LR2004</c> - and would have
    /// found <c>LR9003</c> and <c>LR3102</c> before the milestones that wired them, since each
    /// appeared nowhere but the ID table.
    /// </para>
    /// <para>
    /// <b>Comment lines are stripped first, and that is the whole difficulty.</b> Several files
    /// name a code only inside a <c>&lt;see cref="..."/&gt;</c>, so a scan that counted prose would
    /// be satisfied by a doc comment - which is exactly the failure this exists to catch.
    /// </para>
    /// <para>
    /// Its reach stops there, honestly: it proves a code is named in live code, not that any path
    /// reaches it. <c>LR9004</c> had one such reference for a long time, in a switch arm mapping a
    /// verdict nothing produced, and this test would have been green throughout. Reachability is
    /// what the revert tables and the one-real-path tests are for.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryDiagnosticCodeIsRaisedBySomethingUnderSrc()
    {
        var src = Path.Combine(RepoRoot.Find().FullName, "src");

        // EventIds.cs names every code by construction - it is the mapping - so counting it would
        // make this test vacuous. DiagnosticCode.cs needs no exclusion: it declares the literals
        // and never spells "DiagnosticCode.Name", so it cannot match its own needle.
        var code = Directory
            .EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => Path.GetFileName(f) != "EventIds.cs")
            .Select(f => string.Join(
                '\n',
                File.ReadAllLines(f)
                    .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal))
                    .Where(l => !l.TrimStart().StartsWith('*'))))
            .ToArray();

        var names = typeof(DiagnosticCode)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => f.Name)
            .ToArray();

        names.Length.ShouldBeGreaterThan(15);

        names
            .Where(name => !code.Any(c => c.Contains($"DiagnosticCode.{name}", StringComparison.Ordinal)))
            .ShouldBeEmpty("diagnostic codes nothing under src/ ever raises");
    }

    /// <summary>
    /// Every option the guard is configured with is read by the guard.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A guard option nothing consults is worse than an unused field: it is a safety control that
    /// appears to be set. Three shipped that way. <c>FollowReparsePoints</c> was deleted in
    /// milestone 15 when its rule died; <c>Elevated</c> outlived the same rule by a milestone and
    /// was still being populated at three call sites; and <c>MaxFilesOverride</c> was set by the
    /// journal's own guard while nothing on that path ever reached a match count.
    /// </para>
    /// <para>
    /// <b>The allowlist is empty and should stay that way.</b> The fix for an option nothing reads
    /// is to read it or delete it, never to annotate it.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryGuardOptionIsReadByTheGuard()
    {
        var guard = Path.Combine(
            RepoRoot.Find().FullName, "src", "WinLogRotate.Core", "Safety", "PathGuard.cs");

        // Comments stripped first. GuardOptions and PathGuard share this file, so a declaration
        // and its doc comment both name every option - and a scan satisfied by prose would pass
        // while asserting nothing, which is the failure this file exists to catch.
        var code = string.Join(
            '\n',
            File.ReadAllLines(guard)
                .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal))
                .Where(l => !l.TrimStart().StartsWith('*')));

        var options = typeof(GuardOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToArray();

        options.Length.ShouldBeGreaterThan(1);

        options
            .Where(name => !code.Contains($"Options.{name}", StringComparison.Ordinal))
            .ShouldBeEmpty("guard options the guard never reads");
    }

    /// <summary>
    /// The link checks cannot be handed an override, and the location checks must be.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "A link refusal is never overridable" was a promise in a doc comment, kept only because one
    /// method happened not to consult a field that was in scope. That is the exact shape of the
    /// defect milestone 15 fixed - <c>CheckReparsePoint</c> was dead and nobody noticed - so here
    /// the promise is made structural instead: there is no parameter to pass.
    /// </para>
    /// <para>
    /// The second half matters as much. A default value on the three checks that <i>do</i> take a
    /// scope is precisely how <c>allowdangerous</c> would go quiet again: a new call site omits
    /// it, the code compiles, and the override silently evaporates. Reflection over the parameter
    /// rather than a text scan, so neither half can be satisfied by a comment.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheLinkChecksCannotBeHandedAnOverride()
    {
        var methods = typeof(PathGuard).GetMethods(BindingFlags.Public | BindingFlags.Instance);
        var scope = typeof(GuardScope);

        string[] links = ["CheckLinkTarget", "LinkedFileNotFollowed", "UnresolvableLink"];
        string[] located = ["CheckPattern", "CheckPath", "CheckMatchCount"];

        // A renamed method would otherwise make both assertions below vacuously true.
        methods.Count(m => links.Contains(m.Name, StringComparer.Ordinal)).ShouldBe(links.Length);
        methods.Count(m => located.Contains(m.Name, StringComparer.Ordinal)).ShouldBe(located.Length);

        methods
            .Where(m => links.Contains(m.Name, StringComparer.Ordinal))
            .Where(m => m.GetParameters().Any(p => p.ParameterType == scope))
            .Select(m => m.Name)
            .ShouldBeEmpty("link checks that can be handed an override");

        methods
            .Where(m => located.Contains(m.Name, StringComparer.Ordinal))
            .Where(m => m.GetParameters().SingleOrDefault(p => p.ParameterType == scope)
                        is not { HasDefaultValue: false })
            .Select(m => m.Name)
            .ShouldBeEmpty("location checks whose scope is missing or optional");
    }

    /// <summary>
    /// Loading a configuration requires the caller to say whether it can check a secret.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ConfigLoader.Load</c> took an optional trailing <c>ISecretLookup?</c> from milestone 7,
    /// and every production caller omitted it - so <c>LR9005</c> was declared, documented in
    /// <c>docs/diagnostics.md</c> as "a configuration names a secret that is not stored", and
    /// raised by the configuration validator exactly never. The guard was not merely unarmed: with
    /// a null lookup its <c>when</c> clause evaluated <c>(bool?)null == false</c>, so it could not
    /// have matched even if something had reached it.
    /// </para>
    /// <para>
    /// A required parameter is the fix, because "nobody thought about it" stops being spellable. A
    /// caller that genuinely cannot look passes <c>UnknownSecretLookup</c> and says why in a
    /// comment - <c>doctor</c> does, and its own remarks explain that the GUI runs it on every tab
    /// change. Restoring a default would make the silence available again.
    /// </para>
    /// </remarks>
    [Fact]
    public void LoadingAConfigurationRequiresAnAnswerAboutSecrets()
    {
        var load = typeof(ConfigLoader)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == nameof(ConfigLoader.Load));

        var secrets = load.GetParameters()
            .SingleOrDefault(p => p.ParameterType == typeof(ISecretLookup))
            .ShouldNotBeNull("ConfigLoader.Load must still take a secret lookup");

        secrets.HasDefaultValue.ShouldBeFalse(
            "an optional lookup is how LR9005 stayed unreachable for nine milestones");
    }

    [GeneratedRegex(@"^\|\s*`([a-z_]+)`\s*\|", RegexOptions.Compiled)]
    private static partial Regex DocumentedJobKey();

    /// <summary>
    /// Every <c>[job]</c> key has a row in the configuration reference, and every row is real.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Eight keys were documented nowhere at all - <c>allowdangerous</c>, <c>hourly</c>,
    /// <c>yearly</c>, <c>monthday</c>, <c>maxfiles</c>, <c>dateformat</c>, <c>olddir</c> and
    /// <c>createolddir</c> - and the unknown-key remedy sent operators to a README section that
    /// contains one example and no key reference at all. <c>allowdangerous</c> is the sharpest
    /// case: it is the only escape hatch from a guard refusal, and the refusal's own remedy named
    /// it while no document did.
    /// </para>
    /// <para>
    /// Read from <see cref="ConfigBinder.JobKeys"/> directly rather than by scanning the binder's
    /// source, because that array is the thing the binder actually consults - a source scan could
    /// pass while the binder disagreed with itself.
    /// </para>
    /// <para>
    /// Both directions, like the diagnostics rule. A stale row is worse than a missing one: it
    /// tells an operator to write a key that will be warned about and ignored.
    /// </para>
    /// <para>
    /// <b>What this deliberately does not assert:</b> row order, which section a key sits in, the
    /// Default column against the code, or anything about the prose. Defaults live in three places
    /// and are rendered for humans ("on", "beside the log"), so pinning them would either be wrong
    /// or force the page into a machine-readable straitjacket - and a doc test that fails on a
    /// wording change gets deleted within a month, which is worse than having none.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryJobKeyIsDocumentedInTheConfigurationReference()
    {
        var doc = Path.Combine(RepoRoot.Find().FullName, "docs", "configuration.md");

        var documented = File.ReadAllLines(doc)
            .Select(line => DocumentedJobKey().Match(line))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // A regex that silently stopped matching would make everything below pass while asserting
        // nothing, which is the failure this whole file exists to prevent.
        documented.Count.ShouldBeGreaterThan(30);

        ConfigBinder.JobKeys
            .Where(k => !documented.Contains(k))
            .ShouldBeEmpty("[job] keys with no row in docs/configuration.md");

        documented
            .Where(k => !ConfigBinder.JobKeys.Contains(k, StringComparer.OrdinalIgnoreCase))
            .ShouldBeEmpty("documented keys the binder does not accept");
    }

    /// <summary>The unknown-key remedy points at a document that lists the keys.</summary>
    /// <remarks>
    /// It pointed at README's Configuration section, which has one example block naming eleven
    /// keys and no reference of any kind - so the single-edit suggester was doing the work the
    /// documentation was credited with, and an operator two edits away from a real key was sent
    /// somewhere that never mentions it.
    /// </remarks>
    [Fact]
    public void TheUnknownKeyRemedyPointsAtTheConfigurationReference()
    {
        var binder = Path.Combine(
            RepoRoot.Find().FullName, "src", "WinLogRotate.Core", "Configuration", "ConfigBinder.cs");

        File.ReadAllText(binder).ShouldContain("docs/configuration.md");
    }

    /// <summary>
    /// Every journal operation the vocabulary declares is written by something.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Op</c> calls itself a closed set that <c>winlogrotate journal</c> is queried by, and
    /// eight of its twenty members were written by nothing. A journal that answers a query for
    /// <c>guard.override</c> with silence - not because no override happened, but because no line
    /// was ever written - is worse than one that does not offer the query: it looks like an
    /// answer. <c>GuardDecision.Overridden</c> meanwhile promised in its own doc comment that an
    /// override "must never be quietly forgotten", which is precisely what happened to it.
    /// </para>
    /// <para>
    /// Scoped to the <c>Op</c> class rather than the file. <c>CliEvent.cs</c> also declares
    /// <c>Phase</c> and <c>OpResult</c>, and a reflection sweep over all three reports members
    /// of the other two as dead ops - which is how an earlier count of this reached twelve.
    /// </para>
    /// <para>
    /// <b>The allowlist is empty and should stay that way.</b> The fix for an operation nothing
    /// writes is to write it or delete it, never to annotate it.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryJournalOpIsEmittedBySomethingUnderSrc()
    {
        var src = Path.Combine(RepoRoot.Find().FullName, "src");

        // Comments stripped, and the declaring file excluded: CliEvent.cs names every member by
        // construction, so counting it would make this vacuous.
        var code = Directory
            .EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => Path.GetFileName(f) != "CliEvent.cs")
            .Select(f => string.Join(
                '\n',
                File.ReadAllLines(f)
                    .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal))
                    .Where(l => !l.TrimStart().StartsWith('*'))))
            .ToArray();

        var ops = typeof(Op)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => f.Name)
            .ToArray();

        ops.Length.ShouldBeGreaterThan(10);

        ops
            .Where(name => !code.Any(c => c.Contains($"Op.{name}", StringComparison.Ordinal)))
            .ShouldBeEmpty("journal operations nothing under src/ ever writes");
    }

    [GeneratedRegex(@"GlobalOptions\.AddTo\((\w+)(,\s*configDir:\s*false)?\)", RegexOptions.Compiled)]
    private static partial Regex GlobalOptionsAdded();

    /// <summary>
    /// A command that declares --config-dir reads it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>EveryOptionDeclaredOnTheCommandTreeIsReadByIt</c> searches the whole file, so one
    /// reader anywhere satisfies every declaration - and <c>--config-dir</c> was added to all
    /// twenty-nine commands and read by twenty-one. The other eight accepted it, listed it in
    /// --help, and ignored it. That is the same shape as the unread options milestone 15 deleted,
    /// hidden from the rule that was written to catch them by being declared in a loop.
    /// </para>
    /// <para>
    /// Scoped per command: each <c>AddTo</c> owns the text up to the next one, which is where its
    /// <c>SetAction</c> sits. A command that genuinely does not read configuration says so with
    /// <c>configDir: false</c> rather than being allowlisted here - the exemption lives beside the
    /// declaration, where the next person to add a verb will see it.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryCommandDeclaringAConfigDirectoryReadsIt()
    {
        var tree = Path.Combine(
            RepoRoot.Find().FullName, "src", "WinLogRotate.Cli", "Commands", "CommandTree.cs");

        var text = File.ReadAllText(tree);
        var added = GlobalOptionsAdded().Matches(text).ToArray();

        // A regex that stopped matching would make this pass while asserting nothing.
        added.Length.ShouldBeGreaterThan(20);

        var unread = new List<string>();

        for (var i = 0; i < added.Length; i++)
        {
            // Opted out, and the reason is written at the call site.
            if (added[i].Groups[2].Success)
            {
                continue;
            }

            var from = added[i].Index;
            var to = i + 1 < added.Length ? added[i + 1].Index : text.Length;

            if (!text[from..to].Contains("GlobalOptions.ConfigDir", StringComparison.Ordinal))
            {
                unread.Add(added[i].Groups[1].Value);
            }
        }

        unread.ShouldBeEmpty("commands that declare --config-dir and never read it");
    }

    /// <summary>
    /// One NSIS instruction, with its string literals and comments removed.
    /// </summary>
    /// <remarks>
    /// Both removals are the point. A <c>MessageBox</c> whose <i>message</i> happens to contain
    /// the characters "/SD " is not a MessageBox with a silent default, and a scanner that cannot
    /// tell those apart passes the one case that matters.
    /// </remarks>
    private readonly record struct NsisInstruction(int Line, string Code);

    /// <summary>
    /// Reads an NSIS script the way the compiler does, near enough to judge one rule.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Line continuations are joined, because a MessageBox is routinely split across several lines
    /// with a trailing backslash and the <c>/SD</c> usually lives on the last of them. Carriage
    /// returns are stripped first: <c>.gitattributes</c> checks <c>.nsi</c> out as CRLF - which is
    /// right for an NSIS script - so a continued line ends backslash-CR rather than backslash, and
    /// a join that does not know it never matches.
    /// </para>
    /// <para>
    /// Strings are removed rather than skipped, so a quoted message cannot satisfy or break a rule
    /// about the instruction around it. NSIS escapes a quote as <c>$\"</c>, and single quotes and
    /// backticks open strings too. Line comments are <c>;</c> and <c>#</c>; block comments are not
    /// handled and nothing in this repository uses them.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<NsisInstruction> ReadNsis(string text)
    {
        var lines = text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        var instructions = new List<NsisInstruction>();

        for (var i = 0; i < lines.Length; i++)
        {
            var start = i;
            var joined = new System.Text.StringBuilder();

            while (true)
            {
                var line = lines[i];
                if (line.EndsWith('\\') && i + 1 < lines.Length)
                {
                    joined.Append(line, 0, line.Length - 1);
                    i++;
                    continue;
                }

                joined.Append(line);
                break;
            }

            instructions.Add(new NsisInstruction(start + 1, StripStringsAndComments(joined.ToString())));
        }

        return instructions;
    }

    private static string StripStringsAndComments(string line)
    {
        var code = new System.Text.StringBuilder(line.Length);

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (c is ';' or '#')
            {
                break;
            }

            if (c is not ('"' or '\'' or '`'))
            {
                code.Append(c);
                continue;
            }

            // A string. Consume to its close, honouring NSIS's $\" escape, and emit nothing.
            var quote = c;
            for (i++; i < line.Length; i++)
            {
                if (line[i] == '$' && i + 2 < line.Length && line[i + 1] == '\\' && line[i + 2] == quote)
                {
                    i += 2;
                    continue;
                }

                if (line[i] == quote)
                {
                    break;
                }
            }
        }

        return code.ToString();
    }

    [GeneratedRegex(@"^\s*MessageBox\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex NsisMessageBox();

    [GeneratedRegex(@"(^|\s)/SD\s", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex NsisSilentDefault();

    /// <summary>
    /// Every MessageBox in an installer script carries an <c>/SD</c> silent default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// NSIS does not suppress message boxes under <c>/S</c>. A box without a default therefore
    /// hangs an unattended install for ever, on a dialog nobody can see, and the deployment that
    /// invoked it simply never returns. The reference project calls this its single most important
    /// gotcha.
    /// </para>
    /// <para>
    /// This was four lines of grep in <c>.github/scripts/check-nsis-silent-defaults.sh</c>, and it
    /// could not fail in three ways. Given a file with no MessageBox at all - a renamed script, a
    /// changed path - the loop never ran and it printed success. Its pattern was case-sensitive
    /// while <b>NSIS instruction names are not</b>, so <c>messagebox MB_OK "..."</c> with no
    /// default compiled, shipped, and reported clean. And it matched <c>/SD </c> anywhere on the
    /// line, including inside the message text.
    /// </para>
    /// <para>
    /// Being a test rather than a workflow step is not incidental. The release path runs
    /// <c>dotnet test</c> and did not run that script, so this is also the difference between a
    /// rule that guards releases and one that guards only the builds nobody ships.
    /// </para>
    /// </remarks>
    [Fact]
    public void EverySilentInstallHasADefault()
    {
        var packaging = Path.Combine(RepoRoot.Find().FullName, "packaging");

        var scripts = Directory.EnumerateFiles(packaging, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetExtension(f).Equals(".nsi", StringComparison.OrdinalIgnoreCase)
                     || Path.GetExtension(f).Equals(".nsh", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToArray();

        scripts.ShouldNotBeEmpty("there is no installer script to check");

        var boxes = new List<string>();
        var undefaulted = new List<string>();

        foreach (var script in scripts)
        {
            foreach (var instruction in ReadNsis(File.ReadAllText(script)))
            {
                if (!NsisMessageBox().IsMatch(instruction.Code))
                {
                    continue;
                }

                var where = $"{Path.GetFileName(script)}:{instruction.Line}";
                boxes.Add(where);

                if (!NsisSilentDefault().IsMatch(instruction.Code))
                {
                    undefaulted.Add(where);
                }
            }
        }

        // The floor the shell version never had. Zero matches is not a clean bill of health, it is
        // a scanner that stopped understanding its input - which is the failure this whole file
        // exists to prevent.
        boxes.Count.ShouldBeGreaterThan(4,
            "found almost no MessageBox at all, so everything below asserted nothing");

        undefaulted.ShouldBeEmpty(
            "a MessageBox with no /SD hangs a silent install for ever on a dialog nobody can see");
    }

    /// <summary>
    /// The scanner reads NSIS, not text that looks like it.
    /// </summary>
    /// <remarks>
    /// Every case here is one the shipped rule depends on, and two of them are cases the shell
    /// script it replaces got wrong. Without this the rule above is only ever exercised against a
    /// file that passes, so nothing would show that it can fail.
    /// </remarks>
    [Fact]
    public void TheSilentDefaultScannerReadsNsisRatherThanText()
    {
        const string Script = """
            ; MessageBox MB_OK "a commented-out box is not a call"
            Function Cases
              MessageBox MB_OK "plain, with a default" /SD IDOK
              MessageBox MB_OK "plain, without one"
              messagebox MB_OK "lowercase, without one"
              MessageBox MB_OK \
                "split across lines" \
                /SD IDOK
              MessageBox MB_OK "type /SD IDOK to continue"
              MessageBox MB_OK "quoted $\"/SD IDOK$\" inside a message"
              DetailPrint "MessageBox MB_OK is not a call here"
            FunctionEnd
            """;

        var found = ReadNsis(Script)
            .Where(i => NsisMessageBox().IsMatch(i.Code))
            .ToArray();

        // Six calls: the comment and the DetailPrint are not among them.
        found.Select(i => i.Line).ShouldBe([3, 4, 5, 6, 9, 10]);

        found.Where(i => !NsisSilentDefault().IsMatch(i.Code)).Select(i => i.Line)
            .ShouldBe([4, 5, 9, 10],
                "a lowercase instruction is still an instruction, and /SD inside a message is not a default");
    }
    /// <summary>One job in a workflow file: its id, and every line from its key to the next one.</summary>
    private readonly record struct WorkflowJob(string Id, string Text);

    [GeneratedRegex(@"^  ([A-Za-z0-9_-]+):[ \t]*$", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex WorkflowJobKey();

    /// <summary>
    /// The jobs declared in <c>.github/workflows/ci.yml</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Indentation parsing rather than a YAML library, because this project's test suite carries
    /// three package references and none of them reads YAML. It is enough for the questions asked
    /// here, all of which are about keys at a known depth, and the floor below is what stops a
    /// reformat turning these rules into no rules.
    /// </para>
    /// <para>
    /// Scanning starts at the <c>jobs:</c> line on purpose: <c>push:</c>, <c>schedule:</c> and the
    /// rest of the trigger block sit at the same indentation as a job id and are not jobs.
    /// </para>
    /// </remarks>
    private static WorkflowJob[] WorkflowJobs()
    {
        var path = Path.Combine(RepoRoot.Find().FullName, ".github", "workflows", "ci.yml");
        File.Exists(path).ShouldBeTrue($"{path} is where the pipeline lives");

        var text = File.ReadAllText(path).Replace("\r", string.Empty, StringComparison.Ordinal);

        var start = text.IndexOf("\njobs:\n", StringComparison.Ordinal);
        start.ShouldBeGreaterThan(0, "the workflow declares no jobs block");

        var body = text[(start + "\njobs:\n".Length)..];
        var keys = WorkflowJobKey().Matches(body);

        var jobs = keys
            .Select((m, n) => new WorkflowJob(
                m.Groups[1].Value,
                body[m.Index..(n + 1 < keys.Count ? keys[n + 1].Index : body.Length)]))
            .ToArray();

        // The floor every rule below inherits. A parse that stopped matching would make each of
        // them pass while asserting nothing about anything, which is the failure this whole file
        // exists to prevent.
        jobs.Length.ShouldBeGreaterThan(5, "found almost no jobs, so the parse has stopped working");

        return jobs;
    }

    /// <summary>The value of a top-level key inside one job, including folded continuations.</summary>
    private static string JobKey(WorkflowJob job, string key)
    {
        var marker = $"\n    {key}:";
        var at = job.Text.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
        {
            return string.Empty;
        }

        var value = new System.Text.StringBuilder();
        foreach (var line in job.Text[(at + 1)..].Split('\n').Skip(1).Prepend(job.Text[(at + 1)..].Split('\n')[0]))
        {
            if (value.Length > 0 && !line.StartsWith("      ", StringComparison.Ordinal))
            {
                break;
            }

            value.Append(line).Append(' ');
        }

        return value.ToString();
    }

    [GeneratedRegex(@"^    continue-on-error:[ \t]*true[ \t]*$", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex JobMayFail();

    [GeneratedRegex(@"^    timeout-minutes:[ \t]*([0-9]+)[ \t]*$", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex JobHangBudget();

    [GeneratedRegex(@"\b(always|cancelled|failure|success)\s*\(", RegexOptions.Compiled)]
    private static partial Regex StatusFunction();

    /// <summary>
    /// Nothing is released without every gate having passed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This repository published a release from a commit whose CI was red. <c>ci.yml</c> and
    /// <c>release.yml</c> both fired on a push to main and were wholly independent, so commit
    /// ac81774 failed <c>installer-smoke</c> and shipped v0.9.0 with six assets in the same
    /// minute. The release path's whole quality gate was one <c>dotnet test</c>.
    /// </para>
    /// <para>
    /// Three things are asserted, and all three are needed. The publishing job is found by the
    /// fact that it <b>publishes</b> rather than by its name, or moving the upload step into
    /// another job would evade every rule here. Its <c>needs</c> must cover every other job that
    /// is capable of failing. And its <c>if:</c> must contain no status function, because
    /// <c>always()</c>, <c>failure()</c>, <c>cancelled()</c> or <c>success()</c> anywhere in that
    /// expression replaces the implicit "every need succeeded" and silently restores exactly the
    /// behaviour this rule exists to prevent - a <c>needs:</c> list that holds nothing back.
    /// </para>
    /// <para>
    /// A job marked <c>continue-on-error</c> is excluded, because a dependent runs even when such
    /// a job fails. Depending on one would look like a gate and be none.
    /// </para>
    /// </remarks>
    [Fact]
    public void NothingIsReleasedWithoutEveryGateHavingPassed()
    {
        var jobs = WorkflowJobs();

        var publishers = jobs
            .Where(j => j.Text.Contains("softprops/action-gh-release", StringComparison.Ordinal))
            .ToArray();

        var publisher = publishers.ShouldHaveSingleItem(
            "exactly one job may publish a release - none means the upload vanished, two means it "
            + "happens twice");

        var needs = JobKey(publisher, "needs");
        needs.ShouldNotBeNullOrWhiteSpace($"'{publisher.Id}' publishes and depends on nothing");

        var gates = jobs
            .Where(j => j.Id != publisher.Id)
            .Where(j => !JobMayFail().IsMatch(j.Text))
            .Select(j => j.Id)
            .ToArray();

        gates.Length.ShouldBeGreaterThan(3, "almost nothing was classified as a gate");

        var ungated = gates
            .Where(id => !Regex.IsMatch(needs, $@"[\[\s,]{Regex.Escape(id)}[\]\s,]"))
            .ToArray();

        ungated.ShouldBeEmpty(
            $"'{publisher.Id}' can publish while these have not passed: {string.Join(", ", ungated)}");

        StatusFunction().IsMatch(JobKey(publisher, "if")).ShouldBeFalse(
            "a status function in the publishing job's if: replaces the implicit success() over "
            + "needs, so every gate above would be advisory");
    }

    /// <summary>
    /// Every job declares how long it may hang for.
    /// </summary>
    /// <remarks>
    /// The default is six hours. The signature failure of this pipeline is a hang - the whole
    /// reason an NSIS MessageBox must carry a silent default is that an unattended install which
    /// meets one never returns - and three of installer-smoke's waits are unbounded. Six hours of
    /// a held runner reports that as a timeout rather than as a diagnosis.
    /// </remarks>
    [Fact]
    public void EveryJobDeclaresAHangBudget()
    {
        var jobs = WorkflowJobs();

        jobs.Where(j => !JobHangBudget().IsMatch(j.Text)).Select(j => j.Id)
            .ShouldBeEmpty("jobs with no timeout-minutes, which may therefore hang for six hours");

        // A floor on the numbers themselves rather than on how many were found: counting matches
        // against the collection they were derived from is a comparison with itself.
        var smoke = jobs.Single(j => j.Id == "installer-smoke");
        int.Parse(JobHangBudget().Match(smoke.Text).Groups[1].Value, CultureInfo.InvariantCulture)
            .ShouldBeInRange(10, 90, "installer-smoke's budget has stopped being a real number");
    }

    /// <summary>
    /// The runner canary is the only job allowed to fail.
    /// </summary>
    /// <remarks>
    /// <c>continue-on-error</c> makes a job's verdict advisory, and a second one appearing quietly
    /// is how a gate stops being a gate. Anchored to the job key's own indentation: the literal
    /// also appears in two comments in this file, and installer-smoke's body is eight hundred
    /// lines of PowerShell in which a matching line could hide.
    /// </remarks>
    [Fact]
    public void TheOnlyJobAllowedToFailIsTheRunnerCanary()
    {
        WorkflowJobs().Where(j => JobMayFail().IsMatch(j.Text)).Select(j => j.Id)
            .ShouldBe(["runner-canary"]);
    }
    [GeneratedRegex(@"Start-Process\b[^\r\n]*(\r?\n[^\r\n]*)?", RegexOptions.Compiled)]
    private static partial Regex StartProcess();

    /// <summary>
    /// A process this pipeline waits for has its exit code read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Start-Process -Wait</c> without <c>-PassThru</c> returns nothing, so every installer
    /// invocation in the smoke job discarded the number the installer had just set to report its
    /// own outcome. <c>winlogrotate.nsi</c> sets <c>SetErrorLevel 2</c> for an unusable
    /// <c>/HOST=</c> and <c>3</c> for an unelevated silent per-machine install, and nothing ever
    /// looked. An installer that reported failure and one that reported success were
    /// indistinguishable to CI - on the pipeline whose own history is a silent install reporting
    /// success with nothing scheduled to run.
    /// </para>
    /// <para>
    /// Keyed on <c>-Wait</c>, which is the honest boundary. The three uninstaller launches
    /// deliberately do not wait: an NSIS uninstaller copies itself to temp and returns
    /// immediately, so its exit code describes the copy rather than the uninstall, and
    /// <c>Wait-Removed</c> polls for the result instead. Requiring <c>-PassThru</c> there would
    /// be requiring a number that means nothing.
    /// </para>
    /// <para>
    /// Both the workflow and the helper script are read. The helper holds one of the launches,
    /// and a rule that looked only at YAML could be satisfied by moving a launch into PowerShell.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryStartProcessCapturesItsExitCode()
    {
        var root = RepoRoot.Find().FullName;

        var sources = new[] { Path.Combine(root, ".github", "workflows", "ci.yml") }
            .Concat(Directory.EnumerateFiles(Path.Combine(root, ".github", "scripts"), "*.ps1"))
            .Where(File.Exists)
            .Order(StringComparer.Ordinal)
            .ToArray();

        sources.Length.ShouldBeGreaterThan(1, "the pipeline's PowerShell has gone missing");

        var waiting = new List<(string Where, string Text)>();

        foreach (var source in sources)
        {
            var text = File.ReadAllText(source).Replace("\r", string.Empty, StringComparison.Ordinal);

            foreach (Match m in StartProcess().Matches(text))
            {
                // The launch plus its continuation, so an argument list wrapped onto a second line
                // is judged as one command rather than as a truncated one.
                if (m.Value.Contains("-Wait", StringComparison.Ordinal))
                {
                    var line = text.Take(m.Index).Count(c => c == '\n') + 1;
                    waiting.Add(($"{Path.GetFileName(source)}:{line}", m.Value));
                }
            }
        }

        // Four installs wait today. A floor here is not decoration: a scan that stopped matching
        // would report every launch as compliant.
        waiting.Count.ShouldBeGreaterThan(2,
            "found almost no waiting launch, so the scan has stopped understanding its input");

        waiting.Where(w => !w.Text.Contains("-PassThru", StringComparison.Ordinal))
            .Select(w => w.Where)
            .ShouldBeEmpty("a process this pipeline waits for whose exit code it then discards");
    }
    /// <summary>
    /// One doc comment describes one member.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Turning <c>GenerateDocumentationFile</c> on gave the compiler CS1571 and CS1572 - a
    /// duplicated <c>&lt;param&gt;</c>, and one naming a parameter that does not exist - and it
    /// found both. It cannot see this one. Two <c>&lt;summary&gt;</c> blocks on a single member
    /// produce no warning at all, and the second silently wins, so the first is left describing
    /// something else while reading as though it describes this.
    /// </para>
    /// <para>
    /// Eight of them had accumulated. Every one was a doc comment that had lost its member to a
    /// refactor: the summary for <c>FileOps.Create</c> sitting on <c>CreateDirectory</c>,
    /// <c>GetSize</c>'s on <c>GetDuration</c>, the archive source's on the file enumerator. In a
    /// codebase whose doc comments are its design record, that is the record describing the wrong
    /// thing - which is the defect this whole file exists to make expensive.
    /// </para>
    /// </remarks>
    [Fact]
    public void OneDocCommentDescribesOneMember()
    {
        var root = RepoRoot.Find().FullName;

        var files = new[] { "src", "tests" }
            .Select(d => Path.Combine(root, d))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();

        files.Length.ShouldBeGreaterThan(50, "found almost no source, so the scan asserted nothing");

        var stacked = new List<string>();
        var blocks = 0;

        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);

            for (var i = 0; i < lines.Length;)
            {
                if (!lines[i].TrimStart().StartsWith("///", StringComparison.Ordinal))
                {
                    i++;
                    continue;
                }

                // One contiguous doc comment, attributes included - an [Fact] between the comment
                // and the method does not start a new one.
                var start = i;
                var summaries = 0;

                while (i < lines.Length
                    && (lines[i].TrimStart().StartsWith("///", StringComparison.Ordinal)
                        || lines[i].TrimStart().StartsWith('[')))
                {
                    summaries += lines[i].Split("<summary>").Length - 1;
                    i++;
                }

                blocks++;

                if (summaries > 1)
                {
                    stacked.Add($"{Path.GetFileName(file)}:{start + 1}");
                }
            }
        }

        // The floor, on the quantity that moves: doc blocks found, not offenders found.
        blocks.ShouldBeGreaterThan(400, "found almost no doc comments, so the scan has stopped working");

        stacked.ShouldBeEmpty(
            "doc comment blocks carrying more than one <summary> - the second wins silently and "
            + "the first is left describing a member it is no longer attached to");
    }
    [GeneratedRegex(@"RetryPolicy\.Execute[^;]*?FileOps\.CopyTruncate", RegexOptions.Singleline | RegexOptions.Compiled)]
    private static partial Regex RetriedCopyTruncate();

    /// <summary>
    /// Nothing retries a whole copytruncate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>CopyTruncate</c> commits the archive and only then empties the live log, so what a
    /// failure after that point retries decides whether the archive survives. Wrapped from
    /// outside, the second attempt measured a source the first had already truncated, copied
    /// nothing, and moved the nothing over the archive it had just saved - and returned normally,
    /// so the run recorded a success and the rotation clock advanced.
    /// </para>
    /// <para>
    /// The retries live inside it now, one unit for opening, one for the copy, one for the cut.
    /// Restoring a wrapper anywhere would restore the defect in full, and the behavioural pin for
    /// that is Windows-only - <c>FileOps</c> goes straight to <c>CreateFile</c> and has no seam -
    /// so this is the half that runs on every leg.
    /// </para>
    /// </remarks>
    [Fact]
    public void NothingRetriesTheWholeCopyTruncate()
    {
        var root = RepoRoot.Find().FullName;

        var sources = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();

        var callers = sources
            .Where(f => File.ReadAllText(f).Contains("FileOps.CopyTruncate", StringComparison.Ordinal))
            .ToArray();

        // The needle has to be live, or the scan below is checking nothing at all.
        callers.ShouldNotBeEmpty("nothing calls FileOps.CopyTruncate any more, so this asserts nothing");

        callers
            .Where(f => RetriedCopyTruncate().IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .ShouldBeEmpty(
                "a copytruncate wrapped in a retry re-copies a source its own previous attempt "
                + "truncated, and overwrites the archive it had already committed");
    }
    /// <summary>
    /// Every retry says that it happened.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>RetryPolicy.Execute</c> has taken an <c>onRetry</c> callback since it was written,
    /// documented as existing so a contended file leaves evidence - "useful evidence when someone
    /// asks why a rotation was slow". Every call site passed three arguments, so the evidence
    /// never existed and the parameter was a promise nothing kept.
    /// </para>
    /// <para>
    /// A source rule rather than a behavioural one, and the reason is worth stating: a retry only
    /// fires for a Win32 sharing or lock violation, and no Linux file API produces one - so the
    /// retry path cannot be provoked on the leg this runs on, and a test that drove
    /// <c>RetryPolicy</c> with a callback of its own would prove that <c>RetryPolicy</c> works,
    /// which was never in question, rather than that production asks it to.
    /// </para>
    /// <para>
    /// The argument list is found by balancing brackets rather than by reading to the next
    /// semicolon. Two of these calls take a block-bodied lambda, so the statement's own
    /// semicolons arrive long before its arguments do - a first version of this rule stopped at
    /// one and reported the compliant calls as offenders.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryRetryReportsThatItHappened()
    {
        var root = Path.Combine(RepoRoot.Find().FullName, "src");
        const string Needle = "RetryPolicy.Execute";

        var offenders = new List<string>();
        var calls = 0;

        foreach (var file in Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var text = string.Join(
                '\n',
                File.ReadAllLines(file).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

            // RetryPolicy's own file is where the overload forwards to the generic one; it is the
            // declaration, not a caller.
            if (Path.GetFileName(file) == "RetryPolicy.cs")
            {
                continue;
            }

            for (var at = text.IndexOf(Needle, StringComparison.Ordinal); at >= 0;
                 at = text.IndexOf(Needle, at + 1, StringComparison.Ordinal))
            {
                var open = text.IndexOf('(', at);
                if (open < 0)
                {
                    continue;
                }

                var depth = 0;
                var close = open;

                for (; close < text.Length; close++)
                {
                    if (text[close] == '(') { depth++; }
                    else if (text[close] == ')' && --depth == 0) { break; }
                }

                calls++;

                if (!text[open..Math.Min(close + 1, text.Length)].Contains("onRetry", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)} @ {at}");
                }
            }
        }

        calls.ShouldBeGreaterThan(8, "found almost no RetryPolicy.Execute, so this rule checks nothing");

        offenders.ShouldBeEmpty(
            "a retry that reports nothing: the file was contended, the run slept for it, and the "
            + "record says only that the operation took a while");
    }
}
