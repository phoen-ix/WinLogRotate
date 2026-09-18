using Shouldly;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// The help for <c>--wait-for-state-lock</c> says how long it waits.
/// </summary>
/// <remarks>
/// The option said "block until the run lock is free", and <c>RunLockOptions.WaitFor</c> gives
/// up after ten minutes - deliberately, so a run Task Scheduler would otherwise kill at its
/// ExecutionTimeLimit ends with a recorded reason instead. An operator who read the help and
/// planned around "until" was planning around a promise the code does not make. The figure is
/// read from the record, so the two cannot drift apart again without this noticing.
/// </remarks>
public sealed class RunLockHelpTests
{
    [Fact]
    public void TheWaitOptionSaysHowLongItWaits()
    {
        var run = Cli.Commands.CommandTree.Build().Subcommands.Single(c => c.Name == "run");
        var option = run.Options.Single(o => o.Name.TrimStart('-') == "wait-for-state-lock");

        var minutes = (int)new Cli.Commands.RunLockOptions().WaitFor.TotalMinutes;

        option.Description.ShouldNotBeNull().ShouldContain(
            $"{minutes} minutes", Case.Sensitive, "the cap the code applies is the one the help states");
    }
}
