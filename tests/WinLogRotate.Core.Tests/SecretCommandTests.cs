using System.CommandLine;
using Shouldly;
using WinLogRotate.Cli;
using WinLogRotate.Cli.Commands;
using WinLogRotate.Cli.Output;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
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
