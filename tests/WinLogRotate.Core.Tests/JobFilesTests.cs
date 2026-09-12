using Shouldly;
using WinLogRotate.Core.Configuration;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// Which files are job files - asked once, so the loader, the guard and the repair cannot
/// disagree.
/// </summary>
/// <remarks>
/// Two spellings of this question is the exact shape of the defect being closed: the gate
/// inspected one <c>DirectoryInfo</c>, the loader opened the files inside it, and the file - the
/// thing actually executed as SYSTEM - was judged by nobody. A repair that fixed a different set
/// from the one the guard judges would be the same defect wearing a fix.
/// </remarks>
public sealed class JobFilesTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-jobfiles-");

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    private void Touch(string name) => File.WriteAllText(Path.Combine(_dir.FullName, name), "");

    [Fact]
    public void JobFilesAreTheTomlFilesInOrdinalOrder()
    {
        Touch("web.toml");
        Touch("apache.toml");
        Touch("Zeta.toml");

        // Ordinal, so two machines given the same files read them in the same sequence and the
        // first refusal a guard reports is the same one twice running. 'Z' is 0x5A and 'a' is
        // 0x61, so a culture-aware comparison would put Zeta last and this one puts it first.
        JobFiles.In(_dir.FullName)
            .Select(Path.GetFileName)
            .ShouldBe(["Zeta.toml", "apache.toml", "web.toml"]);
    }

    /// <summary>
    /// A quarantined file is not a job file, and neither is anything else in the directory.
    /// </summary>
    /// <remarks>
    /// <c>TomlFile.Quarantine</c> renames an unparseable job to <c>.toml.bad</c> so one typo
    /// cannot stop forty healthy jobs. Nothing is loaded from it and nothing is executed from
    /// it, so the guard has nothing to judge and the repair has nothing to fix. Rename one back
    /// and it is a job file again, on all three counts at once.
    /// </remarks>
    [Fact]
    public void OnlyTomlFilesCount()
    {
        Touch("live.toml");
        Touch("broken.toml.bad");
        Touch("notes.txt");
        Touch("config.toml.tmp");
        _dir.CreateSubdirectory("archive.toml");

        JobFiles.In(_dir.FullName).Select(Path.GetFileName).ShouldBe(["live.toml"]);
    }

    [Fact]
    public void ADirectoryThatIsNotThereHasNoJobFiles() =>
        JobFiles.In(Path.Combine(_dir.FullName, "nowhere")).ShouldBeEmpty();
}
