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
