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
    /// The History page has an opinion about every journal operation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It shows file operations and filters bookkeeping, by prefix. Milestone 17 gave emitters
    /// to guard.refuse, guard.override and nulfill - decisions rather than operations - and the
    /// filter did not know about them, so verdicts became rows in a grid whose own doc comment
    /// says it shows "what was actually compressed, moved or deleted", and were counted as
    /// operations underneath it.
    /// </para>
    /// <para>
    /// A text scan, because the GUI cannot be referenced by a test that runs on the Linux leg:
    /// it carries a Microsoft.WindowsDesktop.App framework reference, so such a project fails to
    /// resolve its framework before a single test executes. What makes this more than a grep is
    /// the other side - the expected set is read from <c>Op</c> by reflection, so adding an
    /// operation forces a decision here rather than silently landing in the grid.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheHistoryPageHasAnOpinionAboutEveryJournalOperation()
    {
        var page = File.ReadAllText(Path.Combine(
            RepoRoot.Find().FullName, "src", "WinLogRotate.Gui", "Pages", "HistoryPage.cs"));

        // What a history of file operations is for. Everything else is bookkeeping or a verdict
        // and belongs behind the filter.
        // Hook is shown alongside the file operations: it ran as part of a rotation, and "did
        // the postrotate script fire?" is a question people come to a history for.
        string[] shown =
        [
            Op.Compress, Op.Delete, Op.Rename, Op.CopyTruncate,
            Op.Copy, Op.Create, Op.CreateDir, Op.Plan, Op.Hook,
        ];

        var all = typeof(Op)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();

        all.Length.ShouldBeGreaterThan(10);

        // A filtered operation is named by the page, either whole or by its prefix up to the dot.
        bool Filtered(string op) =>
            page.Contains($"\"{op}\"", StringComparison.Ordinal)
            || (op.IndexOf('.', StringComparison.Ordinal) is > 0 and var dot
                && page.Contains($"\"{op[..(dot + 1)]}\"", StringComparison.Ordinal));

        all.Except(shown, StringComparer.Ordinal)
            .Where(op => !Filtered(op))
            .ShouldBeEmpty("journal operations the History page neither shows nor filters");
    }
}
