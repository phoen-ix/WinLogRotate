using Shouldly;
using WinLogRotate.Gui.Cli;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// Every command line the GUI runs, against the command tree that has to parse it.
/// </summary>
/// <remarks>
/// <para>
/// None of them had ever been parsed by a test. The builder lived on <c>MainForm</c>, which is in
/// a project that carries a Microsoft.WindowsDesktop.App framework reference and cannot run on
/// this leg at all - so sixteen invocations were checked only by somebody reading them.
/// </para>
/// <para>
/// What is asserted is the command line as it actually leaves the process, suffixes included:
/// <c>RunAsync</c> appends <c>--no-color</c> and <c>RunElevatedAsync</c> appends
/// <c>--json-stream --output &lt;file&gt;</c>. A test of the prefix would pass while the real thing
/// failed on the part the GUI adds without being asked.
/// </para>
/// </remarks>
public sealed class CliArgsTests
{
    private static void ShouldParse(params string[] args) =>
        Cli.Commands.CommandTree.Build().Parse(args).Errors
            .ShouldBeEmpty($"the GUI runs this: {string.Join(' ', args)}");

    /// <summary>
    /// Every unelevated invocation parses, with and without a configuration directory.
    /// </summary>
    /// <remarks>
    /// <c>CliRunner.RunAsync</c> appends <c>--no-color</c>, so that is part of what is asserted:
    /// a test of the prefix would pass while the real thing failed on the part the GUI adds
    /// without being asked.
    /// </remarks>
    [Theory]
    [InlineData("doctor --json")]
    [InlineData("doctor")]
    [InlineData("config show --json")]
    [InlineData("config check")]
    [InlineData("journal --json")]
    [InlineData("notify show --json")]
    [InlineData("notify test")]
    [InlineData("notify reset")]
    [InlineData("run --dry-run --verbose --no-notify")]
    public void EveryUnelevatedCommandLineParses(string verb)
    {
        string[] words = verb.Split(' ');

        ShouldParse([.. CliArgs.For(null, words), "--no-color"]);
        ShouldParse([.. CliArgs.For(@"C:\ProgramData\WinLogRotate", words), "--no-color"]);
    }

    /// <summary>
    /// And every elevated one, with the stream flags the runner adds.
    /// </summary>
    /// <remarks>
    /// The three that carry a value from the window are spelled with every value the window can
    /// produce: the scheduling page offers exactly task, service and none, and
    /// <c>SecretPrompt.FieldsFor</c> is the list of fields a provider can be given.
    /// </remarks>
    [Theory]
    [InlineData("run --verbose --no-notify")]
    [InlineData("host repair --acl")]
    [InlineData("host use task")]
    [InlineData("host use service")]
    [InlineData("host use none")]
    [InlineData("notify set-secret email password --from-pipe WinLogRotate-secret-abc")]
    [InlineData("notify set-secret pushover token --from-pipe WinLogRotate-secret-abc")]
    [InlineData("notify set-secret pushover user_key --from-pipe WinLogRotate-secret-abc")]
    [InlineData("notify set-secret webhook url --from-pipe WinLogRotate-secret-abc")]
    public void EveryElevatedCommandLineParses(string verb)
    {
        string[] words = verb.Split(' ');
        string[] streamed = ["--json-stream", "--output", @"C:\Temp\op\events.ndjson"];

        ShouldParse([.. CliArgs.For(null, words), .. streamed]);
        ShouldParse([.. CliArgs.For(@"C:\ProgramData\WinLogRotate", words), .. streamed]);
    }

    /// <summary>
    /// The directory is appended when there is one, and nothing is added when there is not.
    /// </summary>
    /// <remarks>
    /// Unconditional is the whole character of this builder, and the reason a root-level verb
    /// must not come through it - see <c>CliIdentity</c>, which builds its own.
    /// </remarks>
    [Fact]
    public void TheConfigDirectoryIsAppendedOnlyWhenThereIsOne()
    {
        CliArgs.For(null, "doctor", "--json").ShouldBe(["doctor", "--json"]);

        CliArgs.For(@"C:\conf", "doctor", "--json")
            .ShouldBe(["doctor", "--json", "--config-dir", @"C:\conf"]);
    }

    /// <summary>
    /// The verb a command line names, for a message that has to say what did not work.
    /// </summary>
    /// <remarks>
    /// Stops at the first option, so the configuration directory this class appends
    /// unconditionally never becomes part of the verb.
    /// </remarks>
    [Theory]
    [InlineData("run", new[] { "run" })]
    [InlineData("host repair", new[] { "host", "repair", "--acl" })]
    [InlineData("config check", new[] { "config", "check", "--json", "--config-dir", "C:/pd" })]
    [InlineData("", new[] { "--version" })]
    public void TheVerbIsTheLeadingWords(string expected, string[] args) =>
        CliArgs.VerbOf(args).ShouldBe(expected);

    /// <summary>
    /// A failure is described in terms of the verb that failed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exit 1 said "Completed, but some files could not be rotated." for everything. That is a
    /// sentence about <c>run</c>, and exit 1 is not: <c>host repair --acl</c> failing showed it
    /// under a dialog titled "Permissions", telling an operator that files had not been rotated
    /// by a verb that rotates nothing.
    /// </para>
    /// <para>
    /// The <c>run</c> row is asserted beside it, because the specific sentence is the right one
    /// there and losing it would be a worse trade than the defect.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("run", "some files could not be rotated")]
    [InlineData("run --catchup", "some files could not be rotated")]
    [InlineData("host repair", "'host repair' did not finish")]
    [InlineData("secret set", "'secret set' did not finish")]
    [InlineData("", "Some of the work could not be completed.")]
    public void AFailureIsDescribedInTermsOfTheVerbThatFailed(string verb, string expected) =>
        new CliResult
        {
            ExitCode = Core.ExitCode.Errors,
            StdOut = "",
            StdErr = "",
            Verb = verb,
        }
        .Describe().ShouldContain(expected);

    /// <summary>Success and the other failures say the same thing for every verb.</summary>
    /// <remarks>
    /// Only exit 1 is ambiguous about what was attempted. "The configuration has errors, so
    /// nothing was attempted" is true of whichever verb read the configuration, and a bare
    /// "Completed." needs no subject.
    /// </remarks>
    [Theory]
    [InlineData(Core.ExitCode.Ok, "Completed.")]
    [InlineData(Core.ExitCode.ConfigInvalid, "The configuration has errors, so nothing was attempted.")]
    [InlineData(Core.ExitCode.LockHeld, "Another rotation is already running.")]
    public void TheOtherCodesNeedNoVerb(int exitCode, string expected) =>
        new CliResult { ExitCode = exitCode, StdOut = "", StdErr = "", Verb = "host repair" }
            .Describe().ShouldBe(expected);
}
