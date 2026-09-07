using System.CommandLine;
using Shouldly;
using WinLogRotate.Cli.Commands;
using WinLogRotate.Core;
using Xunit;

namespace WinLogRotate.Core.Tests;

public class CommandTreeTests
{
    private static IEnumerable<Command> Walk(Command c)
    {
        yield return c;
        foreach (var child in c.Subcommands.SelectMany(Walk))
        {
            yield return child;
        }
    }

    // The help text is the product's documentation for most people who ever read any. A verb
    // that ships without one ships undocumented.
    [Fact]
    public void EveryCommandHasADescription()
    {
        var undocumented = Walk(CommandTree.Build())
            .Where(c => string.IsNullOrWhiteSpace(c.Description))
            .Select(c => c.Name)
            .ToArray();

        undocumented.ShouldBeEmpty();
    }

    // Every option a user can type is one they may need explained. Same argument.
    [Fact]
    public void EveryOptionHasADescription()
    {
        var undocumented = Walk(CommandTree.Build())
            .SelectMany(c => c.Options.Select(o => (Command: c.Name, Option: o)))
            .Where(x => string.IsNullOrWhiteSpace(x.Option.Description))
            .Where(x => x.Option is not System.CommandLine.Help.HelpOption)
            .Select(x => $"{x.Command} {x.Option.Name}")
            .ToArray();

        undocumented.ShouldBeEmpty();
    }

    // --json is the GUI's contract with the CLI, so it has to exist on every leaf verb. A
    // verb that quietly lacks it is one the GUI can only shell out to and then guess about.
    [Theory]
    [InlineData("run")]
    [InlineData("probe")]
    [InlineData("doctor")]
    [InlineData("journal")]
    [InlineData("scan")]
    public void LeafCommandsAcceptJson(string name)
    {
        var command = Walk(CommandTree.Build()).Single(c => c.Name == name);
        command.Options.ShouldContain(o => o.Name == "--json");
    }

    [Fact]
    public void HostVerbOffersAllThreeRunModels()
    {
        var host = Walk(CommandTree.Build()).Single(c => c.Name == "host");
        host.Subcommands.Select(s => s.Name).ShouldBe(
            ["use", "status", "repair", "pause", "export-task"], ignoreOrder: true);
    }

    // The built-in VersionOption's action runs before ours and prints bare text, which would
    // make `--version --json` emit no envelope. Removing it is load-bearing, not tidying.
    [Fact]
    public void TheBuiltInVersionOptionIsReplacedByOurs()
    {
        var root = CommandTree.Build();
        root.Options.ShouldNotContain(o => o is VersionOption);
        root.Options.ShouldContain(o => o.Name == "--version");
    }

    // A mistyped flag means nothing was attempted, which is exit 2, not 1. Keeping 1 to mean
    // "the run happened and something went wrong" is what lets a scheduled task treat it as
    // "go read the logs" without ambiguity.
    [Fact]
    public void UnknownOptionIsAParseError()
    {
        var parse = CommandTree.Build().Parse(["--nonsense"]);
        parse.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void MistypedSubcommandIsAParseError()
    {
        CommandTree.Build().Parse(["host", "use"]).Errors.ShouldNotBeEmpty();
    }
}
