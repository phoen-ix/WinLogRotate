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
}
