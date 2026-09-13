using Shouldly;
using WinLogRotate.Cli.Commands;
using WinLogRotate.Cli.Output;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Core.Configuration;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// The command line can reach every key the binder reads.
/// </summary>
/// <remarks>
/// <para>
/// This is the rule the whole milestone rests on, and the one that would have caught the outage
/// it opened with. <c>TomlEditor</c> could write only quoted strings, and the one test that
/// claimed to check the general property - <c>WhatIsWrittenParsesBackThroughTheRealBinder</c> -
/// set <c>password</c>, a string key, through a string-only writer. Its name promised everything
/// and it exercised the one case that could not fail.
/// </para>
/// <para>
/// It is also what makes the GUI's premise true. A form generated from <see cref="JobSchema"/>
/// offers a field for every key; if a key cannot survive the round trip from the command line,
/// the field for it is a field that silently does nothing - and the operator has no way to know
/// which one.
/// </para>
/// <para>
/// One key per job file, because seven keys write the same <c>Schedule</c> field and the last
/// one in document order wins. A single file carrying all thirty-eight would report six of them
/// as unreachable and be right for the wrong reason.
/// </para>
/// </remarks>
public sealed class JobKeyReachabilityTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-reach-");

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    private sealed class Silent : IOutputSink
    {
        private readonly List<CliDiagnostic> _diagnostics = [];

        public bool Verbose => false;

        public IReadOnlyList<CliDiagnostic> Diagnostics => _diagnostics;

        public void Diagnostic(CliDiagnostic diagnostic) => _diagnostics.Add(diagnostic);

        public void Event(CliEvent evt) { }

        public void Line(string text) { }

        public int Complete<T>(string verb, int exitCode, T? result) => exitCode;
    }

    /// <summary>
    /// Every key in the schema survives being written by the verb and read by the binder.
    /// </summary>
    /// <remarks>
    /// Asserted three ways, because each catches a different failure. The verb has to accept it;
    /// the file has to end up saying what the schema said it would, which is what catches a value
    /// written as the wrong TOML type; and the binder has to raise nothing about it, which is
    /// what catches a key the writer and the reader spell differently.
    /// </remarks>
    [Fact]
    public void EveryJobKeyIsReachableFromTheCommandLine()
    {
        var unreachable = new List<string>();

        // Self-check, in the idiom the other architecture rules use: a schema that had shrunk to
        // nothing would make every assertion below vacuously true.
        JobSchema.Keys.Count.ShouldBeGreaterThan(30);

        foreach (var row in JobSchema.Keys)
        {
            // The name is the job's identity rather than a key an edit may change, and it is
            // reached by the argument instead - which every job below exercises.
            if (row.Key == "name")
            {
                continue;
            }

            var root = Path.Combine(_dir.FullName, row.Key);
            Directory.CreateDirectory(Path.Combine(root, "conf.d"));

            var sink = new Silent();
            var ctx = new CommandContext(sink, CommandTree.Build().Parse(["job", "add", "x"]));

            var exit = JobCommand.Add(
                ctx, "reach", root,
                row.Key == "paths"
                    ? [new JobEdit("paths", row.Sample.Trim('"'))]
                    : [new JobEdit("paths", "C:/logs/*.log"), new JobEdit(row.Key, row.Sample.Trim('"'))],
                dryRun: false,
                () => true);

            if (exit != ExitCode.Ok)
            {
                unreachable.Add(
                    $"{row.Key}: the verb refused it - "
                    + string.Join("; ", sink.Diagnostics.Select(d => d.Message)));
                continue;
            }

            var path = Path.Combine(root, "conf.d", "reach.toml");
            var file = TomlFile.Load(path);

            JobSchema.TryParse(row.Key, row.Sample.Trim('"'), out var expected, out _);

            if (!TomlEditor.TryRead(file, JobDocument.Table, row.Key, out var written, out _))
            {
                unreachable.Add($"{row.Key}: the verb reported success and wrote no such key");
                continue;
            }

            if (written != expected.ToString())
            {
                unreachable.Add($"{row.Key}: the file says {written}, the schema says {expected}");
                continue;
            }

            var bag = new DiagnosticBag();
            ConfigBinder.BindJob(file, bag);

            var complaints = bag.Items
                .Where(d => d.Message.Contains(row.Key, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (complaints.Length > 0)
            {
                unreachable.Add(
                    $"{row.Key}: written, then the binder said "
                    + string.Join("; ", complaints.Select(d => d.Message)));
            }
        }

        unreachable.ShouldBeEmpty(
            "every key a form would offer has to survive the round trip, or the field for it "
            + "silently does nothing and nobody can tell which one");
    }

    /// <summary>
    /// The set the command line can reach is the set the binder reads - no more, no less.
    /// </summary>
    /// <remarks>
    /// The test above proves each key works. This one proves there are no others: a key added to
    /// <c>ConfigBinder</c> and not to <c>JobSchema</c> would be read by the engine, documented
    /// nowhere, and reachable by nothing.
    /// </remarks>
    [Fact]
    public void TheJobKeyListIsDerivedAndNotRestated()
    {
        JobSchema.Keys.Select(k => k.Key).Order(StringComparer.Ordinal)
            .ShouldBe(ConfigBinder.JobKeys.Order(StringComparer.Ordinal));

        // And the help text a caller reads is that same list, so --help cannot fall behind.
        JobSchema.Summary.Split(", ").ShouldBe(JobSchema.Keys.Select(k => k.Key));
    }
}
