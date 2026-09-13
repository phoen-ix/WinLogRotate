using Shouldly;
using WinLogRotate.Cli.Commands;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// The pause file's failures are answered, not thrown.
/// </summary>
/// <remarks>
/// <c>PausedUntil</c> runs at the top of every scheduled rotation and caught <c>IOException</c>
/// alone, so a pause file with the read-only attribute set - once expired, its own deletion throws
/// <c>UnauthorizedAccessException</c> - made every run exit 4 until somebody noticed. The verb's
/// own writes were not guarded at all, and neither was elevation.
/// </remarks>
public sealed class PauseCommandTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-pause-");

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    private InstallPaths Portable => new() { Scope = InstallScope.Portable, Root = _dir.FullName };

    private static (CommandContext Context, RecordingSink Sink) Context()
    {
        var sink = new RecordingSink();
        var parse = CommandTree.Build().Parse(["host", "pause", "--no-event-log"]);
        return (new CommandContext(sink, parse), sink);
    }

    [Fact]
    public void APauseFileThatCannotBeReadMeansRotating()
    {
        // A directory where the file should be: opening it as a file is refused, on every
        // platform, with an exception that used to escape.
        Directory.CreateDirectory(Path.Combine(_dir.FullName, PauseCommand.PauseFileName));

        Should.NotThrow(() => PauseCommand.PausedUntil(Portable)).ShouldBeNull();
    }

    [Fact]
    public void ADurationIsWrittenInTheGrammarEveryOtherDurationUses()
    {
        var (ctx, sink) = Context();

        PauseCommand.Run(ctx, "2h", Portable, elevated: () => true).ShouldBe(ExitCode.Ok);

        File.Exists(Path.Combine(_dir.FullName, PauseCommand.PauseFileName)).ShouldBeTrue();
        PauseCommand.PausedUntil(Portable).ShouldNotBeNull();
        sink.Diagnostics.ShouldBeEmpty();
    }

    /// <summary>An absurd span is a refusal, not an overflow.</summary>
    [Fact]
    public void APauseLongerThanAYearIsRefused()
    {
        var (ctx, sink) = Context();

        PauseCommand.Run(ctx, "99999.00:00:00", Portable, elevated: () => true).ShouldBe(ExitCode.ConfigInvalid);

        sink.Diagnostics.ShouldContain(d => d.Code == DiagnosticCode.ArgumentUnusable);
        File.Exists(Path.Combine(_dir.FullName, PauseCommand.PauseFileName)).ShouldBeFalse();
    }

    /// <summary>A pause file that cannot be written is LR1010 and exit 1, not a defect.</summary>
    [Fact]
    public void AnUnwritablePauseFileIsReportedNotThrown()
    {
        // A directory where the file should be, so the write cannot succeed anywhere.
        Directory.CreateDirectory(Path.Combine(_dir.FullName, PauseCommand.PauseFileName));
        var (ctx, sink) = Context();

        PauseCommand.Run(ctx, "1h", Portable, elevated: () => true).ShouldBe(ExitCode.Errors);

        sink.Diagnostics.ShouldContain(d => d.Code == DiagnosticCode.ConfigUnwritable);
    }

    [Fact]
    public void APerMachineInstallationNeedsAdministratorToPause()
    {
        var (ctx, sink) = Context();
        var perMachine = new InstallPaths { Scope = InstallScope.PerMachine, Root = _dir.FullName };

        PauseCommand.Run(ctx, "1h", perMachine, elevated: () => false).ShouldBe(ExitCode.Errors);

        sink.Diagnostics.ShouldContain(d => d.Code == DiagnosticCode.NeedsAdministrator);
        File.Exists(Path.Combine(_dir.FullName, PauseCommand.PauseFileName)).ShouldBeFalse("nothing was attempted");
    }
}

/// <summary>A sink that keeps what a verb told it, for verbs driven in-process.</summary>
internal sealed class RecordingSink : Cli.Output.IOutputSink
{
    private readonly List<CliDiagnostic> _diagnostics = [];

    public List<string> Lines { get; } = [];

    public bool Verbose => false;

    public IReadOnlyList<CliDiagnostic> Diagnostics => _diagnostics;

    public void Diagnostic(CliDiagnostic d) => _diagnostics.Add(d);

    public void Event(CliEvent e) { }

    public void Line(string text) => Lines.Add(text);

    public int Complete<T>(string verb, int exitCode, T? result) => exitCode;
}
