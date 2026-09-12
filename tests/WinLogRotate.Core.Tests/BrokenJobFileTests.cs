using Shouldly;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Safety;
using WinLogRotate.Core.Secrets;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// What one broken file in <c>conf.d</c> costs.
/// </summary>
/// <remarks>
/// <para>
/// Four doc comments promised the answer and none of them was true.
/// <c>ConfigLoader</c>'s own: "a broken file is quarantined to .bad, reported, and skipped, and
/// the rest of the run proceeds". <c>TomlFile</c>'s: "One malformed job file must not take the
/// whole configuration down - a typo in an experimental job should not stop forty healthy ones
/// from rotating". <c>ConfigDiagnostic.Job</c>'s: "The doctrine is ConfigLoader's own, already
/// applied to a file that will not parse". And <c>LoadedConfig.HasErrors</c>' own list of what
/// still stops everything, which named <c>config.toml</c> and not a job file.
/// </para>
/// <para>
/// <c>DiagnosticBag.Error</c> sets no <c>Job</c>, so every syntax error from a conf.d file
/// counted as a fault with nothing to blame, <c>HasErrors</c> was true, and <c>run</c> returned
/// <c>ExitCode.ConfigInvalid</c> with a null payload having attempted nothing on the machine. The
/// same file was renamed <c>.bad</c> on the way out, so the second run looked healthy and the
/// first one's outage did not repeat - which is how this survives being noticed.
/// </para>
/// <para>
/// It is the shape the same file already fixed once, for refused paths: "A refused path in one
/// file used to set this and return ExitCode.ConfigInvalid with nothing attempted anywhere, which
/// turned one operator's typo into a disk-space incident."
/// </para>
/// </remarks>
public sealed class BrokenJobFileTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-broken-");

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    private LoadedConfig Load(params (string File, string Toml)[] files)
    {
        File.WriteAllText(Path.Combine(_dir.FullName, "config.toml"), "schema = 1\n");
        var confd = Directory.CreateDirectory(Path.Combine(_dir.FullName, "conf.d"));

        foreach (var (name, toml) in files)
        {
            File.WriteAllText(Path.Combine(confd.FullName, name), toml);
        }

        // quarantineBadFiles: false, as config check passes it. Asking the question must not
        // rearrange the answer, and it is also what lets this assert the same directory twice.
        return ConfigLoader.Load(
            InstallPaths.Resolve(_dir.FullName), new PathGuard(new GuardOptions()),
            new UnknownSecretLookup(), quarantineBadFiles: false);
    }

    private static string Healthy(string name) => $"""
        schema = 1
        [job]
        name  = "{name}"
        kind  = "manage"
        paths = ["C:/logs/{name}/*.log"]
        """;

    /// <summary>An unparseable job file costs that file, and the healthy jobs still load.</summary>
    [Fact]
    public void AJobFileThatWillNotParseCostsThatFileAlone()
    {
        var config = Load(
            ("good.toml", Healthy("good")),
            ("broken.toml", "schema = 1\n[job\nname = \"broken\"\n"),
            ("alsogood.toml", Healthy("alsogood")));

        config.Jobs.Select(j => j.Name).OrderBy(n => n, StringComparer.Ordinal)
            .ShouldBe(["alsogood", "good"]);

        // The assertion that matters. True here meant run returned ExitCode.ConfigInvalid with a
        // null payload and rotated nothing at all, on a machine where two jobs were fine.
        config.HasErrors.ShouldBeFalse(
            "one file that will not parse is not a fault with the configuration as a whole");
    }

    /// <summary>It is still reported, and still counted, so the run cannot report success.</summary>
    /// <remarks>
    /// The opposite mistake, and the one a careless fix makes: carry on, rotate the healthy jobs,
    /// and exit 0 with a file silently out of the configuration. <c>Unloadable</c> is what
    /// <c>RunCommand</c> reads to exit 1 instead.
    /// </remarks>
    [Fact]
    public void AnUnparseableFileIsStillReportedAndStillCounted()
    {
        var config = Load(
            ("good.toml", Healthy("good")),
            ("broken.toml", "schema = 1\n[job\nname = \"broken\"\n"));

        config.Unloadable.Count.ShouldBe(1, "the file has to be counted or the run reports success");
        config.Unloadable.Single().ShouldEndWith("broken.toml");

        var errors = config.Diagnostics.Where(d => d.Severity >= Severity.Error).ToArray();

        errors.ShouldNotBeEmpty("a file that will not parse is an error, not a shrug");
        errors.ShouldAllBe(d => d.FileScoped);
        errors.ShouldAllBe(d => d.File.EndsWith("broken.toml", StringComparison.Ordinal));
    }

    /// <summary>
    /// A fault with nothing smaller than the machine to blame still stops everything.
    /// </summary>
    /// <remarks>
    /// The half a careless fix loses. <c>config.toml</c> carries <c>[notify]</c>, the defaults
    /// every job inherits and the journal settings, so there is no one job to charge it to and
    /// no way to carry on meaning what the operator wrote. It is deliberately NOT file-scoped,
    /// and this is what says so.
    /// </remarks>
    [Fact]
    public void AnUnparseableRootFileStillStopsEverything()
    {
        File.WriteAllText(Path.Combine(_dir.FullName, "config.toml"), "schema = 1\n[defaults\n");
        var confd = Directory.CreateDirectory(Path.Combine(_dir.FullName, "conf.d"));
        File.WriteAllText(Path.Combine(confd.FullName, "good.toml"), Healthy("good"));

        var config = ConfigLoader.Load(
            InstallPaths.Resolve(_dir.FullName), new PathGuard(new GuardOptions()),
            new UnknownSecretLookup(), quarantineBadFiles: false);

        config.HasErrors.ShouldBeTrue("config.toml has nothing smaller than the machine to blame");
        config.Unloadable.ShouldBeEmpty("the root file is not one job's file");
    }
}
