using Shouldly;
using WinLogRotate.Core.Safety;
using WinLogRotate.Gui.Cli;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// A picked file becomes the line that names its series, and a name is offered from its folder.
/// </summary>
public sealed class PatternSuggestionTests
{
    /// <summary>A name that carries a date is one of a series; the date becomes the wildcard.</summary>
    [Theory]
    [InlineData(@"C:\inetpub\logs\LogFiles\W3SVC1\u_ex240915.log", "C:/inetpub/logs/LogFiles/W3SVC1/u_ex*.log")]
    [InlineData(@"D:\app\app-2026-09-18.log", "D:/app/app-*.log")]
    [InlineData(@"D:\app\error20260918-1.log", "D:/app/error*.log")]
    [InlineData(@"D:\app\20260918.txt", "D:/app/*.txt")]
    public void AFileWithADateInItsNameBecomesTheLineForItsSeries(string picked, string line) =>
        PatternSuggestion.FromPick(picked).ShouldBe(line);

    /// <summary>A name without a date is the live log, and is named exactly - a lone digit is a name, not a date.</summary>
    [Theory]
    [InlineData(@"C:\logs\app.log", "C:/logs/app.log")]
    [InlineData(@"C:\logs\log4net.log", "C:/logs/log4net.log")]
    [InlineData(@"C:\logs\site2.log", "C:/logs/site2.log")]
    [InlineData(@"\\srv\share\logs\app.log", "//srv/share/logs/app.log")]
    public void AFileWithoutADateIsNamedExactly(string picked, string line) =>
        PatternSuggestion.FromPick(picked).ShouldBe(line);

    /// <summary>What Browse offers is a line the guard accepts, and the refusal branch is reachable.</summary>
    [Fact]
    public void ASuggestedLinePassesTheGuardOutsideProtectedFolders()
    {
        var guard = new PathGuard(new GuardOptions());

        guard.CheckPattern(PatternSuggestion.FromPick(@"C:\inetpub\logs\u_ex240915.log"), GuardScope.None)
            .IsAllowed.ShouldBeTrue();
        guard.CheckPattern(PatternSuggestion.FromPick(@"C:\Windows\System32\x.log"), GuardScope.None)
            .IsAllowed.ShouldBeFalse("so the preview has a refusal to show");
    }

    [Theory]
    [InlineData("C:/inetpub/logs/LogFiles/W3SVC1/u_ex*.log", "C:/inetpub/logs/LogFiles/W3SVC1")]
    [InlineData(@"C:\logs\app.log", "C:/logs")]
    [InlineData("C:/logs/**/*.log", "C:/logs")]
    [InlineData("app.log", "")]
    public void TheFolderALineStartsFromIsWhereTheDialogOpens(string line, string folder) =>
        PatternSuggestion.Folder(line).ShouldBe(folder);

    /// <summary>The name comes from the folder that says what the logs are, not from one that only says "logs".</summary>
    [Theory]
    [InlineData("C:/nginx/logs/*.log", "nginx")]
    [InlineData("C:/inetpub/logs/LogFiles/W3SVC1/u_ex*.log", "w3svc1")]
    [InlineData(@"C:\Program Files\My App\logs\app.log", "my-app")]
    [InlineData("C:/logs/app.log", "")]
    [InlineData("C:/*.log", "")]
    public void TheNameComesFromTheFolderNotTheGenericLogsSegment(string line, string name) =>
        JobNameSuggestion.From(line).ShouldBe(name);

    [Fact]
    public void ATypedNameIsNeverOverwritten()
    {
        JobNameSuggestion.Apply(current: "", previousSuggestion: "", suggestion: "nginx").ShouldBe("nginx");
        JobNameSuggestion.Apply(current: "nginx", previousSuggestion: "nginx", suggestion: "w3svc1").ShouldBe("w3svc1");
        JobNameSuggestion.Apply(current: "my-site", previousSuggestion: "nginx", suggestion: "w3svc1").ShouldBe("my-site");
        JobNameSuggestion.Apply(current: "my-site", previousSuggestion: "", suggestion: "").ShouldBe("my-site");
    }
}
