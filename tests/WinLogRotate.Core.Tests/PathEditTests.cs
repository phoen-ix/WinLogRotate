using Shouldly;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Hosting.Hosts;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// The list arithmetic behind <c>host path-add</c> and <c>host path-remove</c>.
/// </summary>
/// <remarks>
/// The edit is judged on the text the registry holds, unexpanded, because that is what the verb
/// now reads and writes. Every entry that is not the install directory belongs to some other
/// program, so the property under test is mostly "nothing else moved": not the
/// <c>%SystemRoot%</c> tokens, not an entry that ends in a space, not the case anybody typed.
/// </remarks>
public sealed class PathEditTests
{
    private const string Install = @"C:\Program Files\WinLogRotate";

    [Fact]
    public void AddingAppendsOnceAndCarriesEveryOtherEntryVerbatim()
    {
        const string before = @"%SystemRoot%\system32;%SystemRoot%;C:\Tools ;""C:\Quoted Dir""";

        var (after, changed) = PathEdit.Apply(before, Install, add: true);

        changed.ShouldBeTrue();
        after.ShouldBe(before + ";" + Install);
    }

    [Theory]
    [InlineData(@"C:\Program Files\WinLogRotate")]
    [InlineData(@"C:\Program Files\WinLogRotate\")]
    [InlineData(@"c:\program files\winlogrotate")]
    [InlineData(@"""C:\Program Files\WinLogRotate""")]
    public void ADirectoryAlreadyPresentInAnySpellingIsNotAddedAgain(string spelling)
    {
        var before = @"%SystemRoot%\system32;" + spelling + @";C:\Tools";

        var (after, changed) = PathEdit.Apply(before, Install, add: true);

        changed.ShouldBeFalse();
        after.ShouldBeSameAs(before, "an unchanged list is the same string, so nothing is written");
    }

    [Fact]
    public void RemovingTakesEveryOccurrenceAndNothingElse()
    {
        var before = Install + @";%SystemRoot%\system32;" + Install + @"\;C:\Tools ;" + Install.ToUpperInvariant();

        var (after, changed) = PathEdit.Apply(before, Install, add: false);

        changed.ShouldBeTrue();
        after.ShouldBe(@"%SystemRoot%\system32;C:\Tools ");
    }

    [Fact]
    public void RemovingWhatWasNeverThereChangesNothing()
    {
        const string before = @"%SystemRoot%\system32;C:\Tools";

        var (after, changed) = PathEdit.Apply(before, Install, add: false);

        changed.ShouldBeFalse();
        after.ShouldBeSameAs(before);
    }

    [Fact]
    public void AnEmptyPathBecomesJustTheDirectory()
    {
        var (after, changed) = PathEdit.Apply(string.Empty, Install, add: true);

        changed.ShouldBeTrue();
        after.ShouldBe(Install);
    }

    [Fact]
    public void EmptyEntriesAreTheOneThingDropped()
    {
        var (after, _) = PathEdit.Apply(@"C:\Tools;;C:\Other;", Install, add: true);

        after.ShouldBe(@"C:\Tools;C:\Other;" + Install);
    }

    /// <summary>
    /// <c>--machine</c> without elevation is refused, under the code every other verb uses for
    /// that, and reported under the verb's real name.
    /// </summary>
    /// <remarks>
    /// It used to fall through to this user's PATH and exit 0 - so an installer running the verb
    /// unelevated was told the machine PATH had been edited when nothing of the kind had happened.
    /// Windows only, because the platform refusal comes first everywhere else and there is no
    /// seam to make <c>OperatingSystem.IsWindows()</c> answer otherwise.
    /// </remarks>
    [Theory]
    [InlineData(true, "host path-add")]
    [InlineData(false, "host path-remove")]
    public void TheMachinePathIsRefusedToAnUnelevatedCallerByName(bool add, string verb)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "off Windows the platform refusal comes first.");

        var sink = new RecordingSink();
        var ctx = new Cli.Commands.CommandContext(sink, Cli.Commands.CommandTree.Build().Parse(verb.Split(' ')));

        var exit = Cli.Commands.HostCommand.Path(ctx, add, machine: true, elevated: () => false);

        exit.ShouldBe(ExitCode.Errors);
        var d = sink.Diagnostics.ShouldHaveSingleItem();
        d.Code.ShouldBe(DiagnosticCode.NeedsAdministrator);
        d.Remedy.ShouldNotBeNull();
        d.Remedy.ShouldContain("--machine", Case.Sensitive, "the caller is told how to edit the PATH it can edit");
    }
}
