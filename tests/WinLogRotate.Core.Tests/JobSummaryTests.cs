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
        JobSummary.Sentence(HowRotated.Auto, null, null, null, null, false, ArchiveCompression.Zip, null, "C:/logs/app.log")
            .ShouldBe("Every day, take C:/logs/app.log aside (renamed when the program allows it, copied out otherwise), "
                      + "and keep the 7 newest copies numbered app.log.1, app.log.2 \u2026, zipped, beside the log.");

    [Fact]
    public void EveryChoiceChangesTheSentence()
    {
        JobSummary.Sentence(HowRotated.CopyTruncate, "weekly", "100M", "14", "90", true, ArchiveCompression.Gzip, "D:/archive", "C:/logs/app.log")
            .ShouldBe("Every week, or earlier once it exceeds 100M, copy the contents of C:/logs/app.log out and empty it in place, "
                      + "keep the 14 newest copies dated like app.log-20260918, gzipped, in D:/archive, and delete any older than 90 days.");

        JobSummary.Sentence(HowRotated.Rename, "size", "100M", "1", "1", false, ArchiveCompression.None, null, "C:/logs/*.log")
            .ShouldBe("Once it reaches its size limit, rename C:/logs/*.log and start a fresh one, "
                      + "keep the 1 newest copy numbered app.log.1, app.log.2 \u2026, uncompressed, beside the log, and delete any older than 1 day.");
    }

    [Fact]
    public void AManagedJobSaysWhatIsLeftAlone() =>
        JobSummary.Sentence(HowRotated.Manage, "daily", "100M", "30", null, true, ArchiveCompression.Zip, "D:/a", "C:/inetpub/logs/u_ex*.log")
            .ShouldBe("Each run, leave the newest file of C:/inetpub/logs/u_ex*.log to the application, "
                      + "and keep the 30 newest of the rest zipped.");

    [Fact]
    public void AStrategyWithoutARadioIsSaidToBeAdvanceds() =>
        JobSummary.Sentence(null, null, null, null, null, false, ArchiveCompression.Zip, null, null)
            .ShouldStartWith("Every day, take the matching logs aside as Advanced settings say");

    [Fact]
    public void ANumberThatIsNotOneFallsBackToTheDefault() =>
        JobSummary.Sentence(HowRotated.Rename, null, null, "soon", "lots", false, ArchiveCompression.Zip, null, "C:/l/a.log")
            .ShouldBe("Every day, rename C:/l/a.log and start a fresh one, "
                      + "and keep the 7 newest copies numbered a.log.1, a.log.2 \u2026, zipped, beside the log.");
}
