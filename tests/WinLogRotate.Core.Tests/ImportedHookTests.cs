using Shouldly;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Hooks;
using WinLogRotate.Core.Import;
using WinLogRotate.Core.Notify;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// What an imported logrotate stanza's scripts actually become.
/// </summary>
/// <remarks>
/// The defect this milestone exists for. <c>winlogrotate import</c> turned a <c>postrotate</c>
/// containing <c>kill -USR1</c> into <c>postrotate = ["service:paramchange:nginx"]</c>, with the
/// original quoted above it as proof of a correct translation, no TODO and no warning - and
/// nothing anywhere ran it. Setting <c>enabled = true</c> meant nginx was never signalled again,
/// silently, for as long as the job existed.
/// </remarks>
public sealed class ImportedHookTests
{
    private static string Toml(string stanza) =>
        LogrotateImporter.Import(stanza, "nginx").ShouldHaveSingleItem().Toml;

    private static EffectiveJob Bind(string toml)
    {
        var bag = new DiagnosticBag();
        var job = ConfigBinder.BindJob(TomlFile.Parse(toml, "nginx.toml"), bag).ShouldNotBeNull();

        // The importer writes enabled = false with a "review the paths" note; every generated file
        // does, and it is not what is under test here.
        return SettingsMerge.Resolve(job, null);
    }

    private const string Stanza = """
        C:/logs/nginx/*.log {
            daily
            rotate 14
            postrotate
                kill -USR1 `cat /var/run/nginx.pid`
            endscript
        }
        """;

    /// <summary>
    /// An imported postrotate survives all the way to something that would run.
    /// </summary>
    /// <remarks>
    /// End to end: logrotate text, through the importer, through the TOML binder and the settings
    /// merge, into a hook the bracket would dispatch. Every stage of that chain existed and was
    /// tested before this milestone; the last one was missing, so the chain ended in nothing.
    /// </remarks>
    [Fact]
    public void AnImportedPostrotateBecomesAHookThatWouldRun()
    {
        var toml = Toml(Stanza);
        toml.ShouldContain("postrotate = [\"service:paramchange:nginx\"]");

        var job = Bind(toml);
        job.PostRotate.ShouldHaveSingleItem();

        var planned = HookPlan.For(job.Name, HookStage.PostRotate, job.PostRotate, HookGate.Open)
            .Hooks.ShouldHaveSingleItem();

        planned.Action.Scheme.ShouldBe(HookScheme.Service);
        planned.Action.Target.ShouldBe("nginx");
    }

    /// <summary>
    /// The three script kinds with no equivalent are commented, not emitted as live keys.
    /// </summary>
    /// <remarks>
    /// They used to be written out looking exactly like working directives - next to a postrotate
    /// that does work - and were then dropped in silence by a binder that reported nothing at all
    /// about unknown keys in <c>[job]</c>.
    /// </remarks>
    [Theory]
    [InlineData("firstaction")]
    [InlineData("lastaction")]
    [InlineData("preremove")]
    public void AnUnsupportedScriptKindIsCommentedNotEmitted(string which)
    {
        var toml = Toml($$"""
            C:/logs/nginx/*.log {
                daily
                {{which}}
                    kill -USR1 `cat /var/run/nginx.pid`
                endscript
            }
            """);

        toml.ShouldNotContain($"{which} = [");
        toml.ShouldContain($"# TODO: {which} is not supported");

        // And the original is kept, so nobody has to go back to the source file to see what was
        // being asked for.
        toml.ShouldContain("kill -USR1");

        // It binds cleanly - the TODO is a comment - and produces no hook.
        Bind(toml).PostRotate.ShouldBeEmpty();
    }

    /// <summary>A shell script that cannot be translated is still preserved and still refused.</summary>
    [Fact]
    public void AnUntranslatableScriptIsNotHalfTranslated()
    {
        var toml = Toml("""
            C:/logs/app/*.log {
                daily
                postrotate
                    /usr/local/bin/rebuild-index.sh --since yesterday
                endscript
            }
            """);

        toml.ShouldNotContain("postrotate = [");
        toml.ShouldContain("# TODO: postrotate was a shell script");
        toml.ShouldContain("rebuild-index.sh");
    }

    /// <summary>sharedscripts is noted rather than honoured, because there is nothing to honour.</summary>
    /// <remarks>
    /// Hooks run once per job here whatever the source file said, which is sharedscripts
    /// behaviour. The generated comment says so, rather than leaving somebody to discover that
    /// their nosharedscripts stanza did not do what it says.
    /// </remarks>
    [Fact]
    public void SharedscriptsIsRecordedAsNotADistinctionWeMake()
    {
        Toml("""
            C:/logs/app/*.log {
                daily
                sharedscripts
                postrotate
                    kill -HUP `cat /var/run/app.pid`
                endscript
            }
            """).ShouldContain("# sharedscripts: hooks always run once per job here.");
    }
}
