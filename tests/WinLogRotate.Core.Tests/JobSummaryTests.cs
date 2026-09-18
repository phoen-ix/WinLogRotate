using Shouldly;
using WinLogRotate.Gui.Cli;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// The sentence under the Basics view restates the job in English, and reads right for every choice.
/// </summary>
public sealed class JobSummaryTests
{
    [Fact]
    public void ADefaultJobIsSaidInFull() =>
        JobSummary.Sentence(manage: false, null, null, null, null, null, "C:/logs/*.log")
            .ShouldBe("Every day, move C:/logs/*.log aside, keep the 7 newest copies, and compress the rest.");

    [Fact]
    public void EveryChoiceChangesTheSentence()
    {
        JobSummary.Sentence(false, "weekly", "100M", "14", "90", null, "C:/logs/*.log")
            .ShouldBe("Every week, or earlier once it exceeds 100M, move C:/logs/*.log aside, keep the 14 newest copies, "
                      + "compress the rest, and delete any older than 90 days.");

        JobSummary.Sentence(false, "size", "100M", "1", "1", false, null)
            .ShouldBe("Once it reaches its size limit, move the matching logs aside, keep the 1 newest copy, "
                      + "leave the rest uncompressed, and delete any older than 1 day.");
    }

    [Fact]
    public void AManagedJobSaysWhatIsLeftAlone() =>
        JobSummary.Sentence(manage: true, "daily", "100M", "30", null, null, "C:/inetpub/logs/*.log")
            .ShouldBe("Each run, leave the newest file of C:/inetpub/logs/*.log to the application, "
                      + "keep the 30 newest of the rest, and compress the rest.");

    [Fact]
    public void ANumberThatIsNotOneFallsBackToTheDefault() =>
        JobSummary.Sentence(false, null, null, "soon", "lots", null, "C:/l/a.log")
            .ShouldBe("Every day, move C:/l/a.log aside, keep the 7 newest copies, and compress the rest.");
}
