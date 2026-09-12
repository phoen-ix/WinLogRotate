using Shouldly;
using WinLogRotate.Core;
using WinLogRotate.Gui.Cli;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// What the Settings page says after repairing the configuration directory's permissions.
/// </summary>
/// <remarks>
/// <para>
/// The page said "The configuration directory has been secured." whenever the verb exited 0. On
/// a per-user installation `host repair --acl` deliberately changes nothing: the hardened
/// descriptor grants SYSTEM and Administrators full control and everyone else read, so applying
/// it to a directory inside the user's own profile would take away their write access to their
/// own jobs. It emits a Warning saying exactly that, prints "its permissions were left as they
/// are", and returns exit 0.
/// </para>
/// <para>
/// So the one person whose directory cannot be secured was the one person told it had been.
/// </para>
/// </remarks>
public sealed class RepairProjectionTests
{
    private const string PerUserRefusal = """
        { "ok": true, "exitCode": 0, "verb": "host repair",
          "diagnostics": [ { "severity": "Warning", "code": "LR9001",
            "path": "C:/Users/dana/AppData/Roaming/WinLogRotate",
            "message": "'C:/Users/dana/AppData/Roaming/WinLogRotate' belongs to a per-user installation and was left alone.",
            "remedy": "Install for all users if you need hooks." } ],
          "result": { "host": "None" } }
        """;

    private static CliResult Result(int exitCode, string json) =>
        new() { ExitCode = exitCode, StdOut = json, StdErr = "", Verb = "host repair" };

    /// <summary>
    /// A repair that declined to change anything does not report that it secured anything.
    /// </summary>
    /// <remarks>
    /// The message is the verb's own words rather than a second wording of them. It already
    /// explains why it declined and what to do instead, and two sentences for one fact is the
    /// pair that drifts.
    /// </remarks>
    [Fact]
    public void ARepairThatDeclinedDoesNotReportSuccess()
    {
        var view = RepairProjection.From(Result(ExitCode.Ok, PerUserRefusal));

        view.Tone.ShouldBe(CheckTone.Warning);
        view.Message.ShouldNotContain("has been secured");
        view.Message.ShouldContain("per-user installation and was left alone");
        view.Details.ShouldContain("Install for all users if you need hooks.");
    }

    /// <summary>A repair that really did secure the directory still says so.</summary>
    [Fact]
    public void ARepairThatSecuredTheDirectorySaysSo()
    {
        var view = RepairProjection.From(Result(
            ExitCode.Ok,
            """{ "ok": true, "exitCode": 0, "diagnostics": [], "result": { "host": "None" } }"""));

        view.Tone.ShouldBe(CheckTone.Clean);
        view.Message.ShouldBe("The configuration directory has been secured.");
    }

    /// <summary>An Info diagnostic is not a problem, and does not suppress the good news.</summary>
    /// <remarks>
    /// Without this the fix would be "any diagnostic at all means it did not work", and a verb
    /// that says something harmless in passing would stop the page ever confirming anything.
    /// </remarks>
    [Fact]
    public void AnInfoDiagnosticIsNotAProblem()
    {
        var view = RepairProjection.From(Result(
            ExitCode.Ok,
            """
            { "ok": true, "exitCode": 0, "result": { "host": "None" },
              "diagnostics": [ { "severity": "Info", "message": "Nothing needed changing." } ] }
            """));

        view.Tone.ShouldBe(CheckTone.Clean);
    }

    /// <summary>A repair that failed is an error, and says what failed.</summary>
    [Fact]
    public void ARepairThatFailedIsAnError()
    {
        var view = RepairProjection.From(Result(
            ExitCode.Errors,
            """
            { "ok": false, "exitCode": 1, "result": null,
              "diagnostics": [ { "severity": "Critical", "code": "LR9001",
                "message": "Could not secure C:/ProgramData/WinLogRotate: access is denied." } ] }
            """));

        view.Tone.ShouldBe(CheckTone.Error);
        view.Message.ShouldContain("access is denied");
    }

    /// <summary>A verb that never ran is not a directory that was secured.</summary>
    [Fact]
    public void AVerbThatNeverRanIsNotSuccess()
    {
        var view = RepairProjection.From(new CliResult
        {
            ExitCode = 0,
            StdOut = "",
            StdErr = "",
            Failure = CliFailure.NotFound,
            Verb = "host repair",
        });

        view.Tone.ShouldBe(CheckTone.Error);
        view.Message.ShouldNotContain("has been secured");
    }
}
