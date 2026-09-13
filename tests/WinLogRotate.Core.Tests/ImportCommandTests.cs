using Shouldly;
using WinLogRotate.Cli.Commands;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// The import verb's writes are guarded the way every other writing verb's are.
/// </summary>
/// <remarks>
/// <c>JobCommand</c>'s own remarks named this gap: an unelevated write into a per-machine conf.d
/// escaped as an <c>UnauthorizedAccessException</c> that the guard reported as LR1006, "a defect in
/// the product", about a machine refusing correctly. An output directory that could not be created
/// did the same.
/// </remarks>
public sealed class ImportCommandTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-import-");

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    private const string Nginx = """
        "C:/nginx/logs/*.log" {
            daily
            rotate 14
        }
        """;

    private string Source()
    {
        var path = Path.Combine(_dir.FullName, "nginx");
        File.WriteAllText(path, Nginx);
        return path;
    }

    private static (CommandContext Context, RecordingSink Sink) Context()
    {
        var sink = new RecordingSink();
        var parse = CommandTree.Build().Parse(["import", "x", "--no-event-log"]);
        return (new CommandContext(sink, parse), sink);
    }

    [Fact]
    public void AnOutputDirectoryThatCannotBeCreatedIsReportedNotThrown()
    {
        // A file where the directory should be.
        var blocked = Path.Combine(_dir.FullName, "out");
        File.WriteAllText(blocked, "not a directory");
        var (ctx, sink) = Context();

        var exit = ImportCommand.Run(
            ctx, Source(), blocked,
            new InstallPaths { Scope = InstallScope.Portable, Root = _dir.FullName }, elevated: () => true);

        exit.ShouldBe(ExitCode.Errors);
        sink.Diagnostics.ShouldContain(d => d.Code == DiagnosticCode.ConfigUnwritable);
    }

    [Fact]
    public void WritingIntoAPerMachineConfDNeedsAdministrator()
    {
        var (ctx, sink) = Context();
        var perMachine = new InstallPaths { Scope = InstallScope.PerMachine, Root = _dir.FullName };

        var exit = ImportCommand.Run(ctx, Source(), outDirectory: null, perMachine, elevated: () => false);

        exit.ShouldBe(ExitCode.Errors);
        sink.Diagnostics.ShouldContain(d => d.Code == DiagnosticCode.NeedsAdministrator);
        Directory.Exists(perMachine.ConfigDirectory).ShouldBeFalse("nothing was attempted");
    }

    [Fact]
    public void AnExplicitOutputDirectoryNeedsNoElevation()
    {
        var (ctx, sink) = Context();
        var perMachine = new InstallPaths { Scope = InstallScope.PerMachine, Root = _dir.FullName };
        var elsewhere = Path.Combine(_dir.FullName, "review");

        var exit = ImportCommand.Run(ctx, Source(), elsewhere, perMachine, elevated: () => false);

        exit.ShouldBe(ExitCode.Ok);
        sink.Diagnostics.ShouldNotContain(d => d.Code == DiagnosticCode.NeedsAdministrator);
        Directory.EnumerateFiles(elsewhere, "*.toml").ShouldHaveSingleItem();
    }
}
