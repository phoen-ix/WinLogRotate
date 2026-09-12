using System.CommandLine;
using Shouldly;
using WinLogRotate.Cli;
using WinLogRotate.Cli.Commands;
using WinLogRotate.Cli.Output;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Secrets;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// The secret verbs, on Linux.
/// </summary>
/// <remarks>
/// This is what ISecretPlatform was added for. The verbs need entropy, a machine fingerprint, an
/// elevation check and ACL hardening - all Windows-only - so without the seam none of the
/// argument handling, none of the refusals and none of the diagnostics could be exercised
/// anywhere but a Windows runner, and a mistake in any of them would surface minutes later in CI
/// instead of instantly here.
/// </remarks>
public sealed class SecretCommandTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-secretcmd-");
    private readonly RecordingSink _sink = new();

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    // ---- doubles --------------------------------------------------------------------------

    private sealed class XorProtector : IByteProtector
    {
        public string Scheme => ProtectionScheme.Test;

        public byte[] Protect(ReadOnlySpan<byte> plaintext) => Xor(plaintext);

        public byte[] Unprotect(ReadOnlySpan<byte> ciphertext) => Xor(ciphertext);

        private static byte[] Xor(ReadOnlySpan<byte> input)
        {
            var output = input.ToArray();
            for (var i = 0; i < output.Length; i++)
            {
                output[i] ^= 0x5A;
            }

            return output;
        }
    }

    private sealed class FakePlatform : ISecretPlatform
    {
        public bool IsSupported { get; init; } = true;
        public bool IsElevated { get; init; } = true;
        public string? EntropyId => "1111111111111111";
        public string? MachineFingerprint => "2222222222222222";
        public string? CurrentUserSid => "S-1-5-21-1-2-3-1001";
        public bool RepairedKeyProtection { get; init; }

        /// <summary>Null models a machine key that could not be created or read.</summary>
        public bool HasProtector { get; init; } = true;

        public string? HardenedPath { get; private set; }

        public IByteProtector? CreateProtector(bool provision) => HasProtector ? new XorProtector() : null;

        public void Harden(string temporaryPath, string? ownerSid) => HardenedPath = temporaryPath;

        public SecretsProtection Verify(string path, string? ownerSid) =>
            File.Exists(path) ? SecretsProtection.Hardened : SecretsProtection.Missing;
    }

    private sealed class FakeInput(string? piped) : IInputSource
    {
        public bool IsRedirected => true;

        public string ReadAllText() => piped ?? string.Empty;

        public bool TryReadSecret(string prompt, out SecretString value, out string? error)
        {
            if (piped is null)
            {
                value = SecretString.None;
                error = "No value was given.";
                return false;
            }

            value = SecretString.From(piped);
            error = null;
            return true;
        }
    }

    private sealed class RecordingSink : IOutputSink
    {
        private readonly List<CliDiagnostic> _diagnostics = [];

        public List<string> Lines { get; } = [];

        public bool Verbose => true;

        public IReadOnlyList<CliDiagnostic> Diagnostics => _diagnostics;

        public void Diagnostic(CliDiagnostic diagnostic) => _diagnostics.Add(diagnostic);

        public void Event(CliEvent evt) { }

        public void Line(string text) => Lines.Add(text);

        public int Complete<T>(string verb, int exitCode, T? result) => exitCode;

        public string? CodeOf(Severity atLeast) =>
            _diagnostics.FirstOrDefault(d => d.Severity >= atLeast)?.Code;
    }

    // ---- harness --------------------------------------------------------------------------

    private CommandContext Context()
    {
        var parse = CommandTree.Build().Parse(["secret", "list"]);
        return new CommandContext(_sink, parse);
    }

    private string Root => _dir.FullName;

    private string StorePath => Path.Combine(Root, "secrets.dat");

    private int Set(ISecretPlatform platform, string name, string? piped = "hunter2") =>
        SecretCommand.Set(Context(), new FakeInput(piped), platform, name, null, Root);

    private string ConfigPath => Path.Combine(Root, "config.toml");

    /// <summary>A config with one provider of each kind, all credentials in the clear.</summary>
    private const string PlaintextConfig =
        "schema = 1\r\n"
        + "\r\n"
        + "# the shipper cannot read .gz - do not turn compression on.  -- rmk\r\n"
        + "[notify.email.relay]\r\n"
        + "host     = \"smtp.example\"\r\n"
        + "auth     = \"login\"\r\n"
        + "password = \"hunter2\"\r\n"
        + "\r\n"
        + "[notify.webhook.slack]\r\n"
        + "url = \"https://hooks.example/services/T/B/xxxx\"\r\n"
        + "\r\n"
        + "[notify.pushover.oncall]\r\n"
        + "token    = \"apptoken\"\r\n"
        + "user_key = \"userkey\"\r\n";

    private int SetForProvider(
        ISecretPlatform platform, string provider, string field, string? piped = "s3cret") =>
        SecretCommand.SetForProvider(Context(), new FakeInput(piped), platform, provider, field, Root);

    // ---- notify set-secret ------------------------------------------------------------------

    [Fact]
    public void SetSecretStoresTheValueAndRewritesTheReference()
    {
        // The LR9006 remedy as one command. Doing it in two steps is how the second step gets
        // forgotten, leaving the password in the file with a warning nobody reads any more.
        File.WriteAllText(ConfigPath, PlaintextConfig);

        SetForProvider(new FakePlatform(), "email.relay", "password").ShouldBe(ExitCode.Ok);

        var after = File.ReadAllText(ConfigPath);
        after.ShouldContain("password = \"@secret:email-relay\"");
        after.ShouldNotContain("hunter2");

        // Read back, not merely counted. Asserting the config text and the file's existence would
        // pass just as well if the store had been handed the prompt string, an empty value, or
        // the previous secret - and every one of those looks like a working setup until the relay
        // rejects it.
        var store = SecretStore.Load(StorePath, new XorProtector(), "1111111111111111", "2222222222222222");
        store.TryGet("email-relay", out var stored, out _).ShouldBeTrue();
        stored.Reveal().ShouldBe("s3cret");

        // And the name it chose is the one the remedy would have told them to use.
        ConfigLoader.SuggestSecretName("email.relay", "password").ShouldBe("email-relay");
    }

    [Fact]
    public void SetSecretLeavesTheRestOfTheFileExactlyAsItWas()
    {
        File.WriteAllText(ConfigPath, PlaintextConfig);
        SetForProvider(new FakePlatform(), "email.relay", "password");

        var before = PlaintextConfig.Replace("\r\n", "\n").Split('\n');
        var after = File.ReadAllText(ConfigPath).Replace("\r\n", "\n").Split('\n');

        after.Length.ShouldBe(before.Length);
        before.Zip(after).Count(p => p.First != p.Second).ShouldBe(1);
        File.ReadAllText(ConfigPath).ShouldContain("the shipper cannot read .gz");
    }

    [Fact]
    public void SetSecretWorksForEveryKindOfProvider()
    {
        File.WriteAllText(ConfigPath, PlaintextConfig);
        var platform = new FakePlatform();

        SetForProvider(platform, "webhook.slack", "url").ShouldBe(ExitCode.Ok);
        SetForProvider(platform, "pushover.oncall", "token").ShouldBe(ExitCode.Ok);
        SetForProvider(platform, "pushover.oncall", "user_key").ShouldBe(ExitCode.Ok);

        var after = File.ReadAllText(ConfigPath);
        after.ShouldContain("url = \"@secret:webhook-slack\"");
        after.ShouldContain("token    = \"@secret:pushover-oncall-token\"");
        after.ShouldContain("user_key = \"@secret:pushover-oncall-user-key\"");
        after.ShouldNotContain("apptoken");
    }

    /// <summary>
    /// The value is stored before the configuration is touched.
    /// </summary>
    /// <remarks>
    /// A failed rewrite leaves a stored secret nothing references, which is inert. The other order
    /// leaves a configuration referencing a secret that does not exist - LR9005, and a provider
    /// that has silently stopped authenticating. The save is made to fail by putting a directory
    /// where TomlFile.Save wants to write its temporary sibling.
    /// </remarks>
    [Fact]
    public void TheSecretIsStoredEvenWhenTheConfigCannotBeWritten()
    {
        File.WriteAllText(ConfigPath, PlaintextConfig);
        Directory.CreateDirectory(ConfigPath + ".tmp");

        SetForProvider(new FakePlatform(), "email.relay", "password").ShouldBe(ExitCode.Errors);

        File.ReadAllText(ConfigPath).ShouldBe(PlaintextConfig, "a failed save must change nothing");
        File.Exists(StorePath).ShouldBeTrue("the secret was stored first, and is still there");

        // And the operator is told what to do with it rather than left guessing.
        _sink.Diagnostics.ShouldContain(d => d.Remedy != null && d.Remedy.Contains("@secret:"));
    }

    /// <summary>
    /// A store that cannot be written is a failure to store, not a defect in the product.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four call sites saved with nothing around them, so a full disk or a locked file reached
    /// <c>CommandContext.Guarded</c> and came out as <c>LR1006</c> - "a defect in the product,
    /// not a problem with the machine", above a remedy saying nothing about what was done can be
    /// relied on. Wrong twice over: <c>AtomicJson</c> writes a temporary sibling and moves it, so
    /// a failure leaves the previous contents exactly as they were and nothing is half written.
    /// </para>
    /// <para>
    /// Obstructed the way this file already obstructs a configuration save - a directory where
    /// the temporary sibling wants to go - which fails the same way on both legs.
    /// </para>
    /// </remarks>
    [Fact]
    public void AStoreThatCannotBeWrittenIsAFailureToStore()
    {
        File.WriteAllText(ConfigPath, PlaintextConfig);
        Directory.CreateDirectory(StorePath + ".tmp");

        SetForProvider(new FakePlatform(), "email.relay", "password").ShouldBe(ExitCode.Errors);

        _sink.Diagnostics.ShouldContain(d => d.Code == DiagnosticCode.SecretStoreUnwritable);

        File.Exists(StorePath).ShouldBeFalse("nothing was stored, which is what the remedy says");
    }

    [Fact]
    public void SetSecretRefusesAProviderThatIsNotThere()
    {
        File.WriteAllText(ConfigPath, PlaintextConfig);

        SetForProvider(new FakePlatform(), "email.nope", "password").ShouldBe(ExitCode.Errors);

        File.Exists(StorePath).ShouldBeFalse("nothing should be stored for a provider that does not exist");
        _sink.Diagnostics.ShouldContain(d => d.Message.Contains("email.nope", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("email.relay", "token")]
    [InlineData("email.relay", "url")]
    [InlineData("webhook.slack", "password")]
    [InlineData("pushover.oncall", "url")]
    public void SetSecretRefusesAFieldThatKindDoesNotHave(string provider, string field)
    {
        // A token on an email provider is a typo. Writing it would produce a key the binder
        // ignores and a secret nothing ever reads.
        File.WriteAllText(ConfigPath, PlaintextConfig);

        SetForProvider(new FakePlatform(), provider, field).ShouldBe(ExitCode.Errors);
        File.ReadAllText(ConfigPath).ShouldBe(PlaintextConfig);
    }

    [Fact]
    public void SetSecretWillNotEditAFileItCouldNotParse()
    {
        // A rewrite driven by a partial parse is how a typo becomes data loss.
        const string Broken = "schema = 1\n[notify.email.relay\nhost = \"x\"\n";
        File.WriteAllText(ConfigPath, Broken);

        SetForProvider(new FakePlatform(), "email.relay", "password").ShouldBe(ExitCode.Errors);

        File.ReadAllText(ConfigPath).ShouldBe(Broken);
        File.Exists(StorePath).ShouldBeFalse();
    }

    [Fact]
    public void SetSecretRefusesWithoutElevation()
    {
        File.WriteAllText(ConfigPath, PlaintextConfig);

        SetForProvider(new FakePlatform { IsElevated = false }, "email.relay", "password")
            .ShouldBe(ExitCode.Errors);

        _sink.CodeOf(Severity.Error).ShouldBe(DiagnosticCode.NeedsAdministrator);
        File.ReadAllText(ConfigPath).ShouldBe(PlaintextConfig);
    }

    [Fact]
    public void SetSecretStoresNothingWhenNoValueArrives()
    {
        File.WriteAllText(ConfigPath, PlaintextConfig);

        SetForProvider(new FakePlatform(), "email.relay", "password", piped: null)
            .ShouldBe(ExitCode.Errors);

        File.Exists(StorePath).ShouldBeFalse();
        File.ReadAllText(ConfigPath).ShouldBe(PlaintextConfig);
    }

    /// <summary>
    /// There is no way to pass a credential as an argument, and there must never be one.
    /// </summary>
    /// <remarks>
    /// A command line is readable by every local administrator through Win32_Process, is recorded
    /// verbatim in 4688 audit events, and is captured by essentially every EDR agent. This asserts
    /// the shape of the verb rather than trusting the comment that says so.
    /// </remarks>
    [Fact]
    public void NoSecretVerbAcceptsAValueOnTheCommandLine()
    {
        var forbidden = new[] { "--value", "--password", "--secret", "--token" };

        foreach (var verb in new[]
                 {
                     new[] { "secret", "set", "n" },
                     ["secret", "import"],
                     ["notify", "set-secret", "email.relay", "password"],
                 })
        {
            foreach (var option in forbidden)
            {
                CommandTree.Build().Parse([.. verb, option, "hunter2"])
                    .Errors.ShouldNotBeEmpty($"{string.Join(' ', verb)} must reject {option}");
            }
        }
    }

    // ---- refusals -------------------------------------------------------------------------

    [Fact]
    public void OffWindowsEveryVerbRefusesPolitely()
    {
        // A stack trace is not an error message. Somebody who runs this on the wrong machine
        // should be told why in one sentence.
        var platform = new FakePlatform { IsSupported = false };

        Set(platform, "k").ShouldBe(ExitCode.Errors);

        _sink.CodeOf(Severity.Error).ShouldBe(DiagnosticCode.NotSupportedHere);
        File.Exists(StorePath).ShouldBeFalse();
    }

    [Fact]
    public void WritingWithoutElevationIsRefusedBeforeAnythingIsRead()
    {
        var platform = new FakePlatform { IsElevated = false };

        Set(platform, "k").ShouldBe(ExitCode.Errors);

        _sink.CodeOf(Severity.Error).ShouldBe(DiagnosticCode.NeedsAdministrator);
    }

    [Fact]
    public void ListingWithoutElevationIsAllowed()
    {
        // Reading the names needs no write access, and a GUI polling this must not need a UAC
        // prompt to draw a list of names it is allowed to see.
        SecretCommand.List(Context(), new FakePlatform { IsElevated = false }, Root)
            .ShouldBe(ExitCode.Ok);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("has:colon")]
    [InlineData("has/slash")]
    public void AnUnusableNameIsRefused(string name)
    {
        // The name is written into TOML as @secret:NAME, so anything needing quoting there is
        // refused here rather than producing a config nobody can read back.
        Set(new FakePlatform(), name).ShouldBe(ExitCode.Errors);
        _sink.CodeOf(Severity.Error).ShouldBe(DiagnosticCode.ConfigInvalid);
    }

    [Fact]
    public void AMissingMachineKeyIsReportedRatherThanCrashing()
    {
        Set(new FakePlatform { HasProtector = false }, "k").ShouldBe(ExitCode.Errors);
        _sink.CodeOf(Severity.Error).ShouldBe(DiagnosticCode.SecretStoreUnreadable);
    }

    [Fact]
    public void NothingPipedInIsRefused()
    {
        Set(new FakePlatform(), "k", piped: null).ShouldBe(ExitCode.Errors);
        File.Exists(StorePath).ShouldBeFalse();
    }

    // ---- the happy path -------------------------------------------------------------------

    [Fact]
    public void ASecretIsStoredAndCanBeTested()
    {
        var platform = new FakePlatform();

        Set(platform, "ses-smtp").ShouldBe(ExitCode.Ok);
        SecretCommand.Test(Context(), platform, "ses-smtp", Root).ShouldBe(ExitCode.Ok);

        _sink.Lines.ShouldContain(l => l.Contains("7 character(s)"));
    }

    [Fact]
    public void TheStoredFileNeverContainsTheValue()
    {
        Set(new FakePlatform(), "k", piped: "super-secret-value");

        File.ReadAllText(StorePath).ShouldNotContain("super-secret-value");
    }

    [Fact]
    public void HardeningIsAppliedToTheTemporaryFile()
    {
        // Before the move, never after: a file created in the configuration directory arrives
        // readable by every local user, so there must be no moment where it has its real name
        // and the wrong permissions.
        var platform = new FakePlatform();
        Set(platform, "k");

        platform.HardenedPath.ShouldNotBeNull().ShouldEndWith(".tmp");
    }

    [Fact]
    public void TestingAMissingSecretIsAnError()
    {
        // The one verb where not knowing is a failure: the operator asked a direct question.
        SecretCommand.Test(Context(), new FakePlatform(), "nope", Root).ShouldBe(ExitCode.Errors);
        _sink.CodeOf(Severity.Error).ShouldBe(DiagnosticCode.SecretMissing);
    }

    [Fact]
    public void RemovingAMissingSecretIsAnErrorRatherThanASilentSuccess()
    {
        SecretCommand.Remove(Context(), new FakePlatform(), "nope", Root).ShouldBe(ExitCode.Errors);
        _sink.CodeOf(Severity.Warning).ShouldBe(DiagnosticCode.SecretMissing);
    }

    [Fact]
    public void RemovingActuallyRemoves()
    {
        var platform = new FakePlatform();
        Set(platform, "k");

        SecretCommand.Remove(Context(), platform, "k", Root).ShouldBe(ExitCode.Ok);
        SecretCommand.Test(Context(), platform, "k", Root).ShouldBe(ExitCode.Errors);
    }

    [Fact]
    public void ARepairedMachineKeyIsReported()
    {
        // A repair nobody is told about is indistinguishable from a problem that never existed.
        Set(new FakePlatform { RepairedKeyProtection = true }, "k").ShouldBe(ExitCode.Ok);

        _sink.Diagnostics.ShouldContain(d =>
            d.Severity == Severity.Warning && d.Code == DiagnosticCode.SecretStoreUnreadable);
    }

    // ---- import ---------------------------------------------------------------------------

    [Fact]
    public void ImportStoresEveryPair()
    {
        var platform = new FakePlatform();

        SecretCommand.Import(
            Context(), new FakeInput("a=one\nb=two\n"), platform, null, Root).ShouldBe(ExitCode.Ok);

        SecretCommand.Test(Context(), platform, "a", Root).ShouldBe(ExitCode.Ok);
        SecretCommand.Test(Context(), platform, "b", Root).ShouldBe(ExitCode.Ok);
    }

    [Fact]
    public void ImportSplitsOnTheFirstEqualsOnly()
    {
        // A base64 secret ends in '=', and a token that lost its padding is a token that does
        // not work. This is the bug that would take a day to find.
        var platform = new FakePlatform();

        SecretCommand.Import(
            Context(), new FakeInput("tok=YWJjZA==\n"), platform, null, Root).ShouldBe(ExitCode.Ok);

        var store = SecretStore.Load(StorePath, new XorProtector(), "1111111111111111", "2222222222222222");
        store.TryGet("tok", out var value, out _).ShouldBeTrue();
        value.Reveal().ShouldBe("YWJjZA==");
    }

    [Fact]
    public void ImportSkipsBlankLinesAndComments()
    {
        var platform = new FakePlatform();

        SecretCommand.Import(
            Context(), new FakeInput("# a deployment file\n\na=one\n"), platform, null, Root)
            .ShouldBe(ExitCode.Ok);

        SecretCommand.Test(Context(), platform, "a", Root).ShouldBe(ExitCode.Ok);
    }

    [Fact]
    public void ImportRefusesALineThatIsNotAPair()
    {
        SecretCommand.Import(
            Context(), new FakeInput("a=one\nnonsense\n"), new FakePlatform(), null, Root)
            .ShouldBe(ExitCode.Errors);

        _sink.Diagnostics.ShouldContain(d => d.Line == 2);

        // Nothing partially applied: the whole file is refused, so a fixed file can be re-run
        // without wondering which half already landed.
        File.Exists(StorePath).ShouldBeFalse();
    }

    [Fact]
    public void ImportPreservesAValueContainingSpaces()
    {
        var platform = new FakePlatform();

        SecretCommand.Import(
            Context(), new FakeInput("p=two words \n"), platform, null, Root).ShouldBe(ExitCode.Ok);

        var store = SecretStore.Load(StorePath, new XorProtector(), "1111111111111111", "2222222222222222");
        store.TryGet("p", out var value, out _).ShouldBeTrue();
        value.Reveal().ShouldBe("two words ");
    }

    // ---- refusing to destroy what it cannot read ------------------------------------------

    [Fact]
    public void AStoreFromAnotherMachineIsNotOverwritten()
    {
        // Saving over a store whose entries this machine cannot decrypt would destroy them
        // silently, which is the one outcome worse than refusing to help.
        var platform = new FakePlatform();
        Set(platform, "k");

        var moved = SecretStore.Load(StorePath, new XorProtector(), "1111111111111111", "9999999999999999");
        moved.Status.ShouldBe(SecretStoreStatus.WrongMachine);

        var before = File.ReadAllText(StorePath);

        SecretCommand.Set(
            Context(), new FakeInput("x"),
            new WrongMachinePlatform(), "other", null, Root).ShouldBe(ExitCode.Errors);

        File.ReadAllText(StorePath).ShouldBe(before);
        _sink.CodeOf(Severity.Error).ShouldBe(DiagnosticCode.SecretStoreUnreadable);
    }

    private sealed class WrongMachinePlatform : ISecretPlatform
    {
        public bool IsSupported => true;
        public bool IsElevated => true;
        public string? EntropyId => "1111111111111111";
        public string? MachineFingerprint => "9999999999999999";
        public string? CurrentUserSid => null;
        public bool RepairedKeyProtection => false;

        public IByteProtector? CreateProtector(bool provision) => new XorProtector();

        public void Harden(string temporaryPath, string? ownerSid) { }

        public SecretsProtection Verify(string path, string? ownerSid) => SecretsProtection.Hardened;
    }
}
