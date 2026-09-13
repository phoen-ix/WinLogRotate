using Shouldly;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Safety;
using WinLogRotate.Core.Secrets;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// One spelling of what this product makes of a job file.
/// </summary>
/// <remarks>
/// <para>
/// The chain that decides a job's fate - parse, bind, merge, validate - existed only inside
/// <c>ConfigLoader.Load</c>'s per-file loop, which is driven by a directory. Anything wanting to
/// judge a single proposed job before writing it had to assemble the chain a second time, and two
/// spellings of the same question is exactly the defect <c>JobFiles</c> was created to close one
/// level down.
/// </para>
/// <para>
/// So it was extracted rather than reimplemented, and the loader calls it. These tests are what
/// says that is true rather than intended.
/// </para>
/// </remarks>
public sealed class JobVerdictTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-verdict-");

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    private InstallPaths Paths => new() { Scope = InstallScope.Portable, Root = _dir.FullName };

    private string Job(string name, string body)
    {
        Directory.CreateDirectory(Paths.ConfigDirectory);
        var path = Path.Combine(Paths.ConfigDirectory, JobFiles.NameFor(name));
        File.WriteAllText(path, body);
        return path;
    }

    private static PathGuard Guard() => new(new GuardOptions { ProtectedRoots = [] });

    /// <summary>
    /// The loader and the single-job check reach the same verdict, job for job.
    /// </summary>
    /// <remarks>
    /// The whole point of the extraction. Asserted over a directory holding a good job, a job
    /// whose validation fails, and a duplicate name - the three outcomes that are handled
    /// differently, so a check that agreed only about healthy jobs would pass vacuously.
    /// </remarks>
    [Fact]
    public void TheLoaderAndTheSingleJobCheckAgree()
    {
        File.WriteAllText(Paths.ConfigFile, "schema = 1\n\n[defaults]\nrotate = 4\n");

        Job("good", "schema = 1\n\n[job]\nname = \"good\"\npaths = [\"C:/logs/*.log\"]\n");

        // rotate = -2 is an error ConfigValidator raises, so this job is skipped rather than run.
        Job("invalid", "schema = 1\n\n[job]\nname = \"invalid\"\npaths = [\"C:/logs/*.log\"]\nrotate = -2\n");

        // A second file claiming a name the first already has.
        File.WriteAllText(
            Path.Combine(Paths.ConfigDirectory, "also-good.toml"),
            "schema = 1\n\n[job]\nname = \"good\"\npaths = [\"C:/other/*.log\"]\n");

        var loaded = ConfigLoader.Load(Paths, Guard(), new UnknownSecretLookup(), quarantineBadFiles: false);

        var index = JobIndex.Of(Paths);
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var judged = new List<JobVerdict>();

        foreach (var path in JobFiles.In(Paths.ConfigDirectory))
        {
            var verdict = ConfigLoader.Judge(TomlFile.Load(path), path, index.Defaults, Guard(), seen);
            judged.Add(verdict);

            if (verdict.Job is not null && verdict.Outcome != JobOutcome.Duplicate)
            {
                seen[verdict.Job.Name] = path;
            }
        }

        // The fixture really did produce all three outcomes, or the agreement below is about
        // one case rather than three.
        judged.Select(v => v.Outcome).Order().ShouldBe(
            [JobOutcome.Duplicate, JobOutcome.Invalid, JobOutcome.Ready]);

        judged.Where(v => v.Outcome == JobOutcome.Ready).Select(v => v.Effective!.Name)
            .ShouldBe(loaded.Jobs.Select(j => j.Name));

        judged.Where(v => v.Outcome == JobOutcome.Invalid).Select(v => v.Effective!.Name)
            .ShouldBe(loaded.SkippedJobs.Select(j => j.Name));

        judged.SelectMany(v => v.Diagnostics).Select(d => d.Message)
            .ShouldBe(
                loaded.Diagnostics
                    .Where(d => d.Code != DiagnosticCode.NoJobsConfigured)
                    .Select(d => d.Message),
                "the same findings, in the same order");
    }

    /// <summary>
    /// The merged value comes from <c>[defaults]</c>, which a single-job check cannot see alone.
    /// </summary>
    [Fact]
    public void TheDefaultsTableReachesASingleJobCheck()
    {
        File.WriteAllText(Paths.ConfigFile, "schema = 1\n\n[defaults]\nrotate = 4\n");
        var path = Job("iis", "schema = 1\n\n[job]\nname = \"iis\"\npaths = [\"C:/logs/*.log\"]\n");

        var index = JobIndex.Of(Paths);

        index.Defaults.ShouldNotBeNull().Rotate.ShouldBe(4);

        var verdict = ConfigLoader.Judge(
            TomlFile.Load(path), path, index.Defaults, Guard(), index.NamesInUse(path));

        verdict.Outcome.ShouldBe(JobOutcome.Ready);
        verdict.Effective.ShouldNotBeNull().Rotate.ShouldBe(4, "inherited, not the built-in 7");
        verdict.Job.ShouldNotBeNull().Rotate.ShouldBeNull("and the file itself still says nothing");
    }

    /// <summary>
    /// A job is not a duplicate of itself.
    /// </summary>
    /// <remarks>
    /// Editing an existing job asks the same question as adding a new one, and without excluding
    /// its own file every edit would be refused for colliding with the job being edited.
    /// </remarks>
    [Fact]
    public void AJobIsNotADuplicateOfItself()
    {
        var path = Job("iis", "schema = 1\n\n[job]\nname = \"iis\"\npaths = [\"C:/logs/*.log\"]\n");

        var index = JobIndex.Of(Paths);

        index.FileFor("iis").ShouldBe(path);

        ConfigLoader.Judge(TomlFile.Load(path), path, null, Guard(), index.NamesInUse(path))
            .Outcome.ShouldBe(JobOutcome.Ready);

        // And without the exclusion it is exactly what a second file would be.
        ConfigLoader.Judge(TomlFile.Load(path), path, null, Guard(), index.NamesInUse(null))
            .Outcome.ShouldBe(JobOutcome.Duplicate);
    }

    /// <summary>
    /// A name a form can produce becomes a filename Windows can create.
    /// </summary>
    /// <remarks>
    /// The importer lowercased and replaced spaces, which was enough for names taken from a
    /// logrotate file. <c>IIS: W3SVC1</c> produced <c>iis:-w3svc1.toml</c> - legal on Linux, and
    /// an alternate-data-stream spelling on Windows. A text box will contain a colon on day one.
    /// </remarks>
    [Theory]
    [InlineData("iis", "iis.toml")]
    [InlineData("My App", "my-app.toml")]
    [InlineData("IIS: W3SVC1", "iis-w3svc1.toml")]
    [InlineData("a/b\\c", "a-b-c.toml")]
    [InlineData("  spaced  out  ", "spaced-out.toml")]
    [InlineData(".hidden", "hidden.toml")]
    [InlineData("***", "job.toml")]
    public void AJobNameBecomesAFilenameWindowsCanCreate(string name, string expected)
    {
        JobFiles.NameFor(name).ShouldBe(expected);

        // Spelled out rather than taken from Path.GetInvalidFileNameChars(), which on the Linux
        // leg is { '\0', '/' } - it would watch every Windows-only character pass and call it a
        // pass. This is the set the product's one platform actually refuses.
        JobFiles.NameFor(name).IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|'])
            .ShouldBe(-1, "a name Windows will accept, whichever leg is asking");

        JobFiles.NameFor(name).ShouldNotContain(" ");
    }
}
