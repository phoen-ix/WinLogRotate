using Shouldly;
using WinLogRotate.Contracts;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Hooks;
using WinLogRotate.Core.Notify;
using WinLogRotate.Core.Safety;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// Which hooks are permitted to run, and how the rest are refused.
/// </summary>
/// <remarks>
/// Pure, so all of it runs on the Linux leg - which matters more here than anywhere else in the
/// product, because these are the rules that decide what executes as SYSTEM.
/// </remarks>
public sealed class HookPlanTests
{
    private static PlannedHooks Plan(
        string entry, HookGate? gate = null, HookStage stage = HookStage.PostRotate) =>
        HookPlan.For("app", stage, [entry], gate ?? HookGate.Open);

    private static CliDiagnostic Only(PlannedHooks planned)
    {
        planned.Hooks.ShouldBeEmpty();
        return planned.Refusals.ShouldHaveSingleItem();
    }

    // ---- service: ----------------------------------------------------------------------------

    [Fact]
    public void ParamchangeIsAccepted()
    {
        var hook = Plan("service:paramchange:W3SVC").Hooks.ShouldHaveSingleItem();

        hook.Action.Scheme.ShouldBe(HookScheme.Service);
        hook.Action.Target.ShouldBe("W3SVC");
        hook.StageName.ShouldBe("postrotate");
    }

    /// <summary>A verbless <c>service:NAME</c> means the default control code, which is this one.</summary>
    [Fact]
    public void AVerblessServiceTargetIsParamchange()
    {
        Plan("service:W3SVC").Hooks.ShouldHaveSingleItem().Action.Target.ShouldBe("W3SVC");
    }

    /// <summary>
    /// Nothing but <c>paramchange</c> is sent.
    /// </summary>
    /// <remarks>
    /// It is the only verb the README promises, the only one that maps to what postrotate is for -
    /// logrotate's kill -HUP, "re-read your configuration" - and the only one that cannot cause an
    /// outage. A nightly rotation that restarts IIS should have to say so in full.
    /// </remarks>
    [Theory]
    [InlineData("stop")]
    [InlineData("start")]
    [InlineData("restart")]
    public void OnlyParamchangeIsAccepted(string verb)
    {
        var refusal = Only(Plan($"service:{verb}:W3SVC"));

        refusal.Code.ShouldBe(DiagnosticCode.HookRefused);
        refusal.Remedy.ShouldNotBeNull().ShouldContain("command:");
        refusal.Remedy.ShouldContain("net.exe stop W3SVC");
    }

    [Fact]
    public void AServiceTargetWithNoNameIsRefused()
    {
        Only(Plan("service:paramchange:")).Message.ShouldContain("names no service");
    }

    // ---- command: ----------------------------------------------------------------------------

    [Fact]
    public void ACommandIsSplitOnceHereAndNotAgainLater()
    {
        var hook = Plan(@"command:C:\Windows\System32\net.exe stop W3SVC")
            .Hooks.ShouldHaveSingleItem();

        hook.Program.ShouldBe(@"C:\Windows\System32\net.exe");
        hook.Arguments.ShouldBe(["stop", "W3SVC"]);
    }

    /// <summary>logrotate's own form - no scheme at all - still works.</summary>
    /// <remarks>
    /// It is what somebody pastes out of a ticket, and HookParser has always accepted it. A hook
    /// that silently stopped working because its author omitted <c>command:</c> would be the same
    /// class of defect this milestone was written to remove.
    /// </remarks>
    [Fact]
    public void ASchemelessCommandLineIsACommand()
    {
        Plan(@"C:\tools\reload.exe -q").Hooks.ShouldHaveSingleItem()
            .Program.ShouldBe(@"C:\tools\reload.exe");
    }

    [Fact]
    public void AnAmbiguousCommandIsRefusedWithTheQuotedFormNamed()
    {
        var refusal = Only(Plan(@"command:C:\Program Files\App\reload.exe --now"));

        refusal.Code.ShouldBe(DiagnosticCode.HookRefused);
        refusal.Remedy.ShouldNotBeNull().ShouldContain("Quote the program");
    }

    // ---- event: ------------------------------------------------------------------------------

    [Fact]
    public void AnEventTargetIsAccepted()
    {
        Plan(@"event:Global\AppReload").Hooks.ShouldHaveSingleItem()
            .Action.Target.ShouldBe(@"Global\AppReload");
    }

    // ---- the gate ----------------------------------------------------------------------------

    /// <summary>
    /// Nothing that runs code runs when the configuration directory is not trustworthy.
    /// </summary>
    /// <remarks>
    /// <c>ConfDirGuard.HooksAllowed</c> was computed, printed by <c>doctor</c> and gated nothing at
    /// all until this milestone. Opening the gate in <c>HookPlan.For</c> makes every one of these
    /// three run again.
    /// </remarks>
    [Theory]
    [InlineData(@"command:C:\tools\reload.exe")]
    [InlineData("service:paramchange:W3SVC")]
    [InlineData(@"event:Global\AppReload")]
    public void NothingExecutesBehindAShutGate(string entry)
    {
        var refusal = Only(Plan(entry, HookGate.Shut("conf.d is writable by everyone", "icacls ...")));

        refusal.Code.ShouldBe(DiagnosticCode.HookRefused);
        refusal.Message.ShouldContain("conf.d is writable by everyone");
        refusal.Remedy.ShouldBe("icacls ...");
    }

    /// <summary>
    /// A runner nobody handed a verdict to runs nothing.
    /// </summary>
    /// <remarks>
    /// The default has to refuse. A gate that opens because no caller supplied one is
    /// indistinguishable from no gate at all, and it would open on exactly the path a future
    /// caller forgot about.
    /// </remarks>
    [Fact]
    public void AnUnknownGateIsAShutGate()
    {
        HookGate.Unknown.Allowed.ShouldBeFalse();
        Only(Plan(@"command:C:\tools\reload.exe", HookGate.Unknown));
    }

    // ---- schemes that are not hooks ----------------------------------------------------------

    /// <summary>
    /// A reporting scheme in a hook is refused, and told where it belongs.
    /// </summary>
    /// <remarks>
    /// [notify] already delivers these with a time budget, a circuit breaker and change detection.
    /// A second path to them reachable from postrotate would have none of that, and the first
    /// anyone would learn of the difference is a rotation that hung on a webhook.
    /// </remarks>
    [Theory]
    [InlineData("https://hooks.example.com/abc")]
    [InlineData("smtp:ops@example.com")]
    [InlineData("eventlog:")]
    public void AReportingSchemeIsNotAHook(string entry)
    {
        var refusal = Only(Plan(entry));

        refusal.Code.ShouldBe(DiagnosticCode.HookRefused);
        refusal.Remedy.ShouldNotBeNull().ShouldContain("[notify].to");
    }

    /// <summary>
    /// The gate list and the hook list are the same list.
    /// </summary>
    /// <remarks>
    /// <c>HookPlan.For</c> refuses anything <c>NeedsHardenedConfDir</c> says no to, and then runs
    /// everything it says yes to. If a scheme were ever added to one and not the other, either a
    /// hook would run ungated or a valid one would fall through to a default arm that refuses it
    /// with the wrong reason. This is the assertion that keeps the two honest.
    /// </remarks>
    [Fact]
    public void EveryExecutingSchemeIsAHookAndNoOtherIs()
    {
        foreach (var scheme in Enum.GetValues<HookScheme>())
        {
            var executes = HookSchemes.NeedsHardenedConfDir(scheme);
            var target = scheme switch
            {
                HookScheme.Command => @"command:C:\tools\x.exe",
                HookScheme.Service => "service:paramchange:x",
                HookScheme.Event => @"event:Global\x",
                HookScheme.Http => "https://example.com/x",
                HookScheme.Smtp => "smtp:a@example.com",
                HookScheme.Pushover => "pushover:key",
                _ => "eventlog:",
            };

            Plan(target).Hooks.Count.ShouldBe(
                executes ? 1 : 0, $"{scheme} executes={executes}");
        }
    }

    [Fact]
    public void AnUnknownSchemeIsRefusedNotRunAsACommand()
    {
        // "htp://typo" must never become an attempt to execute a program called htp://typo.
        Only(Plan("htp://typo.example.com")).Message.ShouldContain("is not a hook scheme");
    }

    [Fact]
    public void AnEmptyEntryIsRefused()
    {
        Only(Plan("   ")).Message.ShouldContain("empty");
    }

    // ---- the stage shows up in the message ---------------------------------------------------

    /// <summary>
    /// A hook on a manage job is reported, because it could never fire.
    /// </summary>
    /// <remarks>
    /// A manage job never moves a live log - the application rotates, and this compresses and
    /// retains what it left behind - and hooks run only when a live log moves. Without this the
    /// configuration reads exactly like one that works.
    /// </remarks>
    [Fact]
    public void AHookOnAManageJobIsReported()
    {
        var bag = new DiagnosticBag();
        var file = TomlFile.Parse("""
            schema = 1
            [job]
            name  = "iis"
            kind  = "manage"
            paths = ["C:/inetpub/logs/*.log"]
            postrotate = ["service:paramchange:W3SVC"]
            """, "iis.toml");

        var job = SettingsMerge.Resolve(
            ConfigBinder.BindJob(file, bag).ShouldNotBeNull(), null);

        ConfigValidator.Validate(job, new PathGuard(new GuardOptions()), bag);

        bag.Items.ShouldContain(i =>
            i.Code == DiagnosticCode.HookRefused && i.Message.Contains("never run"));
    }

    /// <summary>
    /// A refusal names which bracket it came from.
    /// </summary>
    /// <remarks>
    /// The two stages have different consequences - a prerotate refusal skips the job, a
    /// postrotate refusal does not - so an operator reading one line has to be able to tell which
    /// they are looking at.
    /// </remarks>
    [Fact]
    public void ARefusalNamesItsStage()
    {
        Only(Plan("service:restart:W3SVC", stage: HookStage.PreRotate))
            .Message.ShouldContain("prerotate");

        Only(Plan("service:restart:W3SVC", stage: HookStage.PostRotate))
            .Message.ShouldContain("postrotate");
    }
}
