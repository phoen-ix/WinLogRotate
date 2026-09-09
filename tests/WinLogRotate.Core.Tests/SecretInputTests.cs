using System.IO.Pipes;
using Shouldly;
using WinLogRotate.Cli.Output;
using WinLogRotate.Core.Secrets;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// Reading a secret: the rules, and the pipe the GUI has to use.
/// </summary>
/// <remarks>
/// <para>
/// The rules had no tests at all before this. <c>ConsoleInputSource</c> was covered only through
/// <c>FakeInput</c>, which short-circuits every one of them - it never applies the length cap,
/// never strips a newline and never refuses an empty value - so the three things that actually
/// decide what gets stored were asserted by nothing.
/// </para>
/// <para>
/// Named pipes work on Linux too (over a Unix socket), so the client half runs on the main leg.
/// The server half is Windows-only because its ACL is, and lives in Hosting for that reason.
/// </para>
/// </remarks>
public sealed class SecretInputTests
{
    // ---- the rules ---------------------------------------------------------------------------

    /// <summary>
    /// Exactly one trailing newline goes, and nothing else.
    /// </summary>
    /// <remarks>
    /// Not Trim(). A password may legitimately end in a space, and helpfully removing it stores
    /// something other than what was given - which surfaces as an authentication failure somewhere
    /// else entirely, months later. This is why <c>secret test</c> reports a length.
    /// </remarks>
    [Theory]
    [InlineData("hunter2\r\n", "hunter2")]
    [InlineData("hunter2\n", "hunter2")]
    [InlineData("hunter2\n\n", "hunter2\n")]
    [InlineData("hunter2 ", "hunter2 ")]
    [InlineData("  hunter2", "  hunter2")]
    [InlineData("hunter2\t", "hunter2\t")]
    [InlineData("hunter2", "hunter2")]
    public void OneTrailingNewlineIsStrippedAndNothingElseIs(string given, string expected) =>
        SecretInput.StripOneNewline(given).ShouldBe(expected);

    [Fact]
    public void AnEmptySecretIsRefusedRatherThanStored()
    {
        SecretInput.Validate(string.Empty, out _, out var error).ShouldBeFalse();
        error.ShouldNotBeNull().ShouldContain("secret remove");
    }

    [Fact]
    public void NothingAtAllIsRefused() =>
        SecretInput.Validate(null, out _, out _).ShouldBeFalse();

    [Fact]
    public void AWholeFilePipedInByMistakeIsRefused()
    {
        // The cap is what turns "I meant to pipe the password and piped the file" into a refusal
        // instead of a stored blob that never authenticates.
        SecretInput.Validate(new string('x', SecretInput.MaxChars + 1), out _, out var error)
            .ShouldBeFalse();

        error.ShouldNotBeNull().ShouldContain("--from-file");
    }

    [Fact]
    public void AValueExactlyAtTheLimitIsAccepted() =>
        SecretInput.Validate(new string('x', SecretInput.MaxChars), out _, out _).ShouldBeTrue();

    /// <summary>
    /// The console reader still applies the rules, now that they live somewhere else.
    /// </summary>
    /// <remarks>
    /// M11 moved the newline stripping, the cap and the empty check out of ConsoleInputSource so
    /// the pipe could share them. Asserting against SecretInput alone would prove the rules work
    /// and say nothing about whether the console still calls them - and the console is the path
    /// every scripted rollout uses.
    /// </remarks>
    [Theory]
    [InlineData("hunter2\n", true, 7)]
    [InlineData("hunter2 \r\n", true, 8)]
    [InlineData("hunter2", true, 7)]
    [InlineData("", false, 0)]
    [InlineData("\n", false, 0)]
    public void TheConsoleReaderAppliesTheSameRules(string piped, bool ok, int length)
    {
        ConsoleInputSource.FromPiped(piped, out var value, out _).ShouldBe(ok);
        value.Length.ShouldBe(length);
    }

    [Fact]
    public void TheConsoleReaderEnforcesTheCapToo()
    {
        ConsoleInputSource.FromPiped(new string('x', SecretInput.MaxChars + 1), out _, out var error)
            .ShouldBeFalse();

        error.ShouldNotBeNull().ShouldContain("--from-file");
    }

    // ---- the frame ---------------------------------------------------------------------------

    [Fact]
    public void AFrameRoundTrips()
    {
        var message = SecretFrame.Wrap("hunter2");
        var length = SecretFrame.PayloadLength(message.AsSpan(0, 4));

        length.ShouldBe(message.Length - 4);
        PlaintextPadding.TryUnwrap(message.AsSpan(4), out var text).ShouldBeTrue();
        text.ShouldBe("hunter2");
    }

    /// <summary>
    /// A length that is not plausible is refused before it is used to size anything.
    /// </summary>
    /// <remarks>
    /// This number arrives from another process. The pipe's ACL is what makes that process
    /// trustworthy; the prefix is not, and allocating from it unchecked is how a hostile or
    /// confused peer turns a credential prompt into an out-of-memory.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    [InlineData(63)]
    [InlineData(SecretFrame.MaxPayloadBytes + 64)]
    public void AnImplausibleLengthIsRefused(int length)
    {
        Span<byte> prefix = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(prefix, length);

        SecretFrame.PayloadLength(prefix).ShouldBe(-1);
    }

    // ---- the wiring ---------------------------------------------------------------------------

    /// <summary>
    /// <c>--from-pipe</c> is what decides where the value comes from.
    /// </summary>
    /// <remarks>
    /// Every other test here drives PipeInputSource directly, so all of them would still pass with
    /// the option reduced to a flag nothing reads - and the GUI, whose only channel this is, would
    /// silently fall back to a console that is not attached to anything.
    /// </remarks>
    [Theory]
    [InlineData("secret", "set", "ses-smtp")]
    [InlineData("notify", "set-secret", "email.relay", "password")]
    public void FromPipeIsWhatDecidesWhereTheValueComesFrom(params string[] verb)
    {
        var console = new ConsoleInputSource();

        Cli.Commands.CommandTree
            .Input(Cli.Commands.CommandTree.Build().Parse([.. verb, "--from-pipe", "wlr-x"]), console)
            .ShouldBeOfType<PipeInputSource>();

        Cli.Commands.CommandTree
            .Input(Cli.Commands.CommandTree.Build().Parse(verb), console)
            .ShouldBeSameAs(console);
    }

    // ---- the pipe ----------------------------------------------------------------------------

    private static string UniqueName() => $"winlogrotate-test-{Guid.NewGuid():N}";

    /// <summary>Serves one value on a pipe, the way the GUI's server does.</summary>
    private static Task Serve(string name, byte[] message, CancellationToken token) => Task.Run(
        async () =>
        {
            using var server = new NamedPipeServerStream(name, PipeDirection.Out, 1);
            await server.WaitForConnectionAsync(token);
            await server.WriteAsync(message, token);
            await server.FlushAsync(token);
        },
        token);

    [Fact]
    public async Task ASecretArrivesOverThePipe()
    {
        var name = UniqueName();
        var serving = Serve(name, SecretFrame.Wrap("hunter2"), TestContext.Current.CancellationToken);

        var source = new PipeInputSource(name, TimeSpan.FromSeconds(10));
        source.TryReadSecret("Value", out var value, out var error).ShouldBeTrue(error);

        // The value, not its length. A length-only assertion passes for any seven characters,
        // including the seven that would arrive if the frame were misaligned by a byte.
        value.Reveal().ShouldBe("hunter2");
        await serving;
    }

    [Fact]
    public async Task ThePipeAppliesTheSameRulesAsTheConsole()
    {
        // The two implementations share SecretInput precisely so they cannot drift: a pipe that
        // trimmed where the console did not would store a different password from the one typed.
        var name = UniqueName();
        var serving = Serve(name, SecretFrame.Wrap("hunter2 \r\n"), TestContext.Current.CancellationToken);

        new PipeInputSource(name, TimeSpan.FromSeconds(10))
            .TryReadSecret("Value", out var value, out _).ShouldBeTrue();

        // One newline gone, the trailing space kept - which a length check alone cannot tell
        // apart from one character of something else.
        value.Reveal().ShouldBe("hunter2 ");
        await serving;
    }

    [Fact]
    public async Task AnEmptyValueOverThePipeIsRefusedToo()
    {
        var name = UniqueName();
        var serving = Serve(name, SecretFrame.Wrap(string.Empty), TestContext.Current.CancellationToken);

        new PipeInputSource(name, TimeSpan.FromSeconds(10))
            .TryReadSecret("Value", out _, out var error).ShouldBeFalse();

        error.ShouldNotBeNullOrWhiteSpace();
        await serving;
    }

    [Fact]
    public void AGarbledFrameIsRefusedRatherThanGuessedAt()
    {
        var name = UniqueName();
        // Discarded rather than awaited: the server writes seven bytes into a 4 KB buffer and
        // returns, so there is nothing to wait for and nothing to leak.
        _ = Serve(name, [0xFF, 0xFF, 0xFF, 0x7F, 1, 2, 3], TestContext.Current.CancellationToken);

        new PipeInputSource(name, TimeSpan.FromSeconds(10))
            .TryReadSecret("Value", out _, out var error).ShouldBeFalse();

        error.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void NothingListeningTimesOutRatherThanHanging()
    {
        // An elevated child that waits for ever on a pipe nobody serves is a process the operator
        // has to go and kill.
        new PipeInputSource(UniqueName(), TimeSpan.FromMilliseconds(250))
            .TryReadSecret("Value", out _, out var error).ShouldBeFalse();

        error.ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// A short read is not the whole message.
    /// </summary>
    /// <remarks>
    /// A pipe read returns what happens to be available, not what was asked for. Treating the
    /// first read as the whole value truncates a long credential into a shorter one that stores
    /// perfectly well and never authenticates.
    /// </remarks>
    [Fact]
    public async Task ALongValueSentInPiecesArrivesWhole()
    {
        var name = UniqueName();
        var secret = new string('x', 3000);
        var message = SecretFrame.Wrap(secret);

        var token = TestContext.Current.CancellationToken;

        var serving = Task.Run(
            async () =>
            {
                using var server = new NamedPipeServerStream(name, PipeDirection.Out, 1);
                await server.WaitForConnectionAsync(token);

                // One byte, then a pause, then the rest. Writing in even chunks lets the reader
                // take the whole message in a single Read on a fast machine, which is how a test
                // for short reads goes green with the loop that handles them deleted.
                await server.WriteAsync(message.AsMemory(0, 1), token);
                await server.FlushAsync(token);
                await Task.Delay(50, token);

                for (var offset = 1; offset < message.Length; offset += 97)
                {
                    await server.WriteAsync(
                        message.AsMemory(offset, Math.Min(97, message.Length - offset)), token);
                    await server.FlushAsync(token);
                }
            },
            token);

        new PipeInputSource(name, TimeSpan.FromSeconds(10))
            .TryReadSecret("Value", out var value, out var error).ShouldBeTrue(error);

        // Every character, in order. Length alone passes for a reader that assembled the right
        // number of bytes out of the wrong ones.
        value.Reveal().ShouldBe(secret);
        await serving;
    }
}
