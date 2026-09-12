using Shouldly;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Import;
using Xunit;

namespace WinLogRotate.Core.Tests;

public class LogrotateParserTests
{
    /// <summary>
    /// The reason this is a lexer and not a regular expression. A script body may contain
    /// braces, and any regex approach breaks on the first awk one-liner - silently dropping
    /// the script from the imported job, which is the worst possible way to fail.
    /// </summary>
    [Fact]
    public void ScriptBodiesMayContainBraces()
    {
        var stanzas = LogrotateParser.Parse("""
            "C:/logs/*.log" {
                daily
                postrotate
                    awk '{print $1}' /var/log/access.log > /tmp/ips
                endscript
                rotate 4
            }
            """);

        var stanza = stanzas.ShouldHaveSingleItem();
        stanza.Scripts["postrotate"].ShouldContain("{print $1}");

        // And the directive after endscript is still read, rather than the parser having lost
        // its place at the brace.
        stanza.Directives.ShouldContain(d => d.Name == "rotate" && d.Argument == "4");
    }

    [Fact]
    public void GlobalsApplyToBlocksParsedAfterThem()
    {
        var stanzas = LogrotateParser.Parse("""
            compress
            notifempty

            "C:/logs/a/*.log" {
                daily
            }
            """);

        var stanza = stanzas.ShouldHaveSingleItem();
        stanza.Directives.ShouldContain(d => d.Name == "compress");
        stanza.Directives.ShouldContain(d => d.Name == "notifempty");
    }

    [Fact]
    public void MultiplePatternsPerBlockAreRead()
    {
        var stanza = LogrotateParser.Parse("""
            "C:/logs/a.log" "C:/logs/b.log" {
                daily
            }
            """).ShouldHaveSingleItem();

        stanza.Patterns.ShouldBe(["C:/logs/a.log", "C:/logs/b.log"]);
    }

    [Fact]
    public void CommentsAreIgnoredOutsideScripts()
    {
        var stanza = LogrotateParser.Parse("""
            # a comment
            "C:/logs/*.log" {
                daily     # trailing
                rotate 7
            }
            """).ShouldHaveSingleItem();

        stanza.Directives.ShouldContain(d => d.Name == "rotate" && d.Argument == "7");
        stanza.Directives.ShouldNotContain(d => d.Name.StartsWith('#'));
    }

    [Fact]
    public void SeveralBlocksAreRead() =>
        LogrotateParser.Parse("""
            "C:/a/*.log" { daily
            }
            "C:/b/*.log" { weekly
            }
            """).Count.ShouldBe(2);
}

public class LogrotateImporterTests
{
    private static ImportedJob Import(string config) =>
        LogrotateImporter.Import(config, "test.conf").ShouldHaveSingleItem();

    /// <summary>
    /// Nothing imported is ever enabled. An import that quietly began deleting files under
    /// rules nobody had read would be indefensible.
    /// </summary>
    [Fact]
    public void EverythingIsImportedDisabled() =>
        Import("""
            "C:/logs/*.log" {
                daily
                rotate 7
            }
            """).Toml.ShouldContain("enabled = false");

    /// <summary>
    /// Two blocks that suggest the same name get two names, and two loadable files.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>SuggestName</c> reads the name out of the first pattern and walks back past generic
    /// container directories, so <c>C:/nginx/logs/*.log</c> and <c>C:/nginx/log/*.log</c> both
    /// came back "nginx". <c>ImportCommand</c> already de-duplicated the <i>file</i> names, so
    /// two files were written - and both declared a job called nginx.
    /// </para>
    /// <para>
    /// <c>ConfigLoader</c> then raises "A job called 'nginx' is already defined in nginx-2.toml",
    /// an error with no single job to blame, so <c>run</c> exits 2 having attempted nothing on the
    /// machine. Measured on the real CLI: before, <c>config check</c> exits 2 on the importer's
    /// own output; after, it exits 0.
    /// </para>
    /// </remarks>
    [Fact]
    public void TwoBlocksThatSuggestOneNameGetTwo()
    {
        var jobs = LogrotateImporter.Import("""
            C:/nginx/logs/*.log {
                daily
            }

            C:/nginx/log/*.log {
                weekly
            }
            """, "test.conf");

        jobs.Count.ShouldBe(2);

        // Both halves, because the file name is derived from the job name and a collision in
        // either one costs a job: the second file overwrites the first, or the second job name
        // stops the machine.
        jobs.Select(j => JobName(j.Toml)).ShouldBeUnique();
        jobs.Select(j => j.SuggestedFileName).ShouldBeUnique();

        foreach (var job in jobs)
        {
            var bag = new DiagnosticBag();
            ConfigBinder.BindJob(TomlFile.Parse(job.Toml, job.SuggestedFileName), bag);
            bag.HasErrors.ShouldBeFalse(job.SuggestedFileName);
        }
    }

    /// <summary>
    /// A block whose every path is POSIX still imports to a file that loads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the ordinary outcome for a logrotate configuration taken off a Linux box, which is
    /// the only kind there is: every pattern fails to translate, so the paths list held nothing
    /// but TODO comments and the job bound to <c>paths = []</c>. <c>ConfigBinder</c> refuses that
    /// - "Job 'nginx' lists no paths" - with no job to blame, so the whole machine stopped
    /// rotating on the night after the migration.
    /// </para>
    /// <para>
    /// Neither the duplicate-key fix nor the file-scoping fix reaches it: the file parses
    /// perfectly, and the error comes from the binder rather than the parser.
    /// </para>
    /// </remarks>
    [Fact]
    public void APosixOnlyBlockStillImportsToAFileThatLoads()
    {
        var job = Import("""
            /var/log/nginx/*.log {
                daily
                rotate 14
            }
            """);

        job.NeedsReview.ShouldBeTrue("nothing about this was translated silently");
        job.Toml.ShouldContain("/var/log/nginx/*.log", Case.Sensitive);
        job.Toml.ShouldContain("enabled = false");

        var bag = new DiagnosticBag();
        var bound = ConfigBinder.BindJob(TomlFile.Parse(job.Toml, "imported.toml"), bag);

        bag.HasErrors.ShouldBeFalse("a file import wrote has to be one the loader accepts");
        bound.ShouldNotBeNull().Paths.ShouldNotBeEmpty();
    }

    private static string JobName(string toml) => toml
        .Split(Environment.NewLine)
        .First(l => l.StartsWith("name", StringComparison.Ordinal));

    /// <summary>
    /// The stock shape of a logrotate configuration imports to TOML that parses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Globals at the top, blocks below - which is what <c>/etc/logrotate.conf</c> looks like on
    /// every distribution - and the two collide. <c>LogrotateParser</c> hands each block its
    /// file's globals in front of its own directives, upstream's rule, and nothing de-duplicated
    /// them; TOML makes a duplicate key an error. So <c>import</c> wrote a file the loader
    /// refused, reported success, and put it in the live <c>conf.d</c>. Until the previous commit
    /// that refusal then stopped every rotation on the machine.
    /// </para>
    /// <para>
    /// <c>TheOutputIsValidTomlThatOurOwnBinderAccepts</c> could not see it: its fixture has no
    /// globals and no directive that collides, so it never reaches the concatenation at all.
    /// </para>
    /// <para>
    /// The values are asserted, not just the parse. Emitting neither <c>rotate</c> nor
    /// <c>compress</c> would also produce a file that parses, and would be a different way of
    /// losing what the operator wrote.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheStockGlobalsAndBlockShapeImportsToTomlThatParses()
    {
        var job = Import("""
            weekly
            rotate 4
            compress

            "C:/nginx/logs/*.log" {
                daily
                rotate 14
                nocompress
                missingok
            }
            """);

        var bag = new DiagnosticBag();
        var bound = ConfigBinder.BindJob(TomlFile.Parse(job.Toml, "imported.toml"), bag);

        bag.HasErrors.ShouldBeFalse("the file import just wrote has to be one the loader accepts");
        bound = bound.ShouldNotBeNull();

        // The block's value, not the global's. Keeping the first would parse just as well and be
        // the wrong answer - logrotate says so out loud as it reads: "note: 'daily' overrides
        // previously specified 'weekly'".
        bound.Rotate.ShouldBe(14);
        bound.Compress.ShouldBe(false);
        bound.MissingOk.ShouldBe(true);

        // Both frequency flags survive, because they are two TOML keys rather than one - and the
        // binder resolves them by document order, so the block's daily still wins.
        bound.Schedule.ShouldBe(Schedule.Daily);
    }

    [Fact]
    public void TheOutputIsValidTomlThatOurOwnBinderAccepts()
    {
        var job = Import("""
            "C:/nginx/logs/*.log" {
                daily
                rotate 14
                compress
                delaycompress
                missingok
                dateext
                dateformat -%Y%m%d
            }
            """);

        var bag = new DiagnosticBag();
        var bound = ConfigBinder.BindJob(TomlFile.Parse(job.Toml, "imported.toml"), bag);

        bag.HasErrors.ShouldBeFalse();
        bound.ShouldNotBeNull();
        bound.Rotate.ShouldBe(14);
        bound.DelayCompress.ShouldBe(true);
        bound.DateExt.ShouldBe(true);

        // strftime is not .NET's format language; translating it silently would produce
        // archives named nothing like what the operator expected.
        bound.DateFormat.ShouldBe("-yyyyMMdd");
    }

    [Fact]
    public void CopyTruncateBecomesAnExplicitLockStrategy() =>
        Import("""
            "C:/logs/*.log" {
                daily
                copytruncate
            }
            """).Toml.ShouldContain("lockstrategy = \"copytruncate\"");

    /// <summary>
    /// The one translation worth doing automatically, because it is exact: a SIGUSR1 to nginx
    /// is a service control code on Windows.
    /// </summary>
    [Fact]
    public void ASignalToNginxBecomesAServiceControlCode()
    {
        var job = Import("""
            "C:/nginx/logs/*.log" {
                daily
                postrotate
                    [ -f /var/run/nginx.pid ] && kill -USR1 `cat /var/run/nginx.pid`
                endscript
            }
            """);

        job.Toml.ShouldContain("service:paramchange:nginx");

        // The original is kept as a comment: the reader must be able to check the translation.
        job.Toml.ShouldContain("kill -USR1");
    }

    /// <summary>
    /// A shell script that is not exactly recognised is preserved as a comment, never
    /// half-translated. A hook that runs approximately the right thing as SYSTEM is worse than
    /// one that does not run.
    /// </summary>
    [Fact]
    public void AnUnrecognisedScriptIsCommentedAndTheJobFlagged()
    {
        var job = Import("""
            "C:/logs/*.log" {
                daily
                postrotate
                    /opt/bin/ship-logs --flush && rm -rf /tmp/staging
                endscript
            }
            """);

        job.NeedsReview.ShouldBeTrue();
        job.Toml.ShouldContain("# TODO: postrotate was a shell script:");
        job.Toml.ShouldContain("ship-logs --flush");

        // Crucially, it is not turned into something executable.
        job.Toml.ShouldNotContain("postrotate = [");
    }

    [Fact]
    public void SuIsDroppedWithAnExplanation()
    {
        var job = Import("""
            "C:/logs/*.log" {
                daily
                su www-data adm
            }
            """);

        job.Toml.ShouldContain("# dropped: su www-data adm");
        job.Toml.ShouldContain("setuid");
        job.Warnings.ShouldContain(w => w.Contains("su", StringComparison.Ordinal));
    }

    [Fact]
    public void APosixPathIsFlaggedRatherThanGuessedAt()
    {
        var job = Import("""
            /var/log/myapp/*.log {
                daily
            }
            """);

        job.NeedsReview.ShouldBeTrue();
        job.Toml.ShouldContain("has no Windows equivalent");
    }

    /// <summary>The last path segment is almost always the least informative one.</summary>
    [Theory]
    [InlineData("C:/nginx/logs/*.log", "nginx")]
    [InlineData("C:/inetpub/logs/LogFiles/*.log", "inetpub")]
    [InlineData("C:/apps/myapp/*.log", "myapp")]
    public void TheJobIsNamedAfterSomethingMeaningful(string pattern, string expected) =>
        Import($$"""
            "{{pattern}}" {
                daily
            }
            """).Toml.ShouldContain($"name  = \"{expected}\"");
}
