using Shouldly;
using WinLogRotate.Core.Hooks;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// The suites that plant a file whose name is fixed, run one at a time.
/// </summary>
/// <remarks>
/// The name has to be exactly what the code would look up, so it cannot be made unique per test,
/// and it lives in the process's working directory because that is what a Windows-shaped relative
/// path resolves against on Unix. Two of these overlapping would race on one path.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class PlantedFileCollection
{
    public const string Name = "plants a file by its real name";
}

/// <summary>
/// A file on disk does not change where a hook's program ends.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CommandLine.TrySplit"/> used to take a <c>Func&lt;string, bool&gt;</c> so this
/// question could be asked without Windows, and one of its two callers asked it about the
/// <i>truncated</i> head - CreateProcess' own first leg, and the escalation this type exists to
/// reject. The parameter is gone, which is what makes that unrepeatable; the cost is that
/// <see cref="CommandLineTests"/> can no longer discriminate, because a refusal is what any
/// implementation gives when there is nothing to plant.
/// </para>
/// <para>
/// So this plants a real one. <c>C:\Program</c> has no leading slash, so on Unix it is a relative
/// path and <c>File.Exists</c> resolves it against the working directory - and a backslash is an
/// ordinary character in an ext4 filename. Re-introduce any probe of the head, however it is
/// spelled, and this goes red.
/// </para>
/// <para>
/// <b>Ubuntu only, and said out loud.</b> On Windows the same string is rooted, so answering it
/// would mean writing to the root of the system drive - which is the very thing an attacker
/// cannot usually do, and is why the plausible plant sites are elsewhere. The test skips there
/// rather than pretending to cover it; <c>ArchitectureTests.TheHookSplitDoesNoIo</c> is what runs
/// on both legs.
/// </para>
/// </remarks>
[Collection(PlantedFileCollection.Name)]
public sealed class CommandLineWitnessTests
{
    [Fact]
    public void APlantedFileDoesNotChangeTheSplit()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(),
            @"C:\Program is rooted on Windows, so planting it means writing to the system drive.");

        // The exact string the old probe was handed, and the one the test that was supposed to
        // cover this never planted.
        const string Plant = @"C:\Program";

        File.WriteAllText(Plant, "not a program, and it does not matter that it is not");

        try
        {
            File.Exists(Plant).ShouldBeTrue("the plant has to be there for this to prove anything");

            CommandLine.TrySplit(
                @"C:\Program Files\App\reload.exe --now",
                out var program, out var arguments, out var error, out _).ShouldBeFalse();

            program.ShouldBeEmpty();
            arguments.ShouldBeEmpty();
            error.ShouldBe(CommandLineError.Ambiguous);
        }
        finally
        {
            File.Delete(Plant);
        }
    }
}
